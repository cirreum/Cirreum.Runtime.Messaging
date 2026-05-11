namespace Cirreum.Runtime.Messaging.Receiving;

using Cirreum;
using Cirreum.Conductor;
using Cirreum.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Hosted service that consumes inbound distributed messages from a queue and/or
/// topic subscription, deserializes them, wraps them in
/// <see cref="DistributedMessageReceived{TMessage}"/>, and publishes them via
/// Conductor so that registered <see cref="INotificationHandler{TNotification}"/>
/// implementations can react.
/// </summary>
/// <remarks>
/// <para>
/// Two consumer loops may run concurrently in the same process:
/// </para>
/// <list type="bullet">
///   <item><description><b>Queue loop</b> — when <see cref="ReceiverOptions.QueueName"/> is configured. Competing-consumer semantics; appropriate for work distribution.</description></item>
///   <item><description><b>Subscription loop</b> — when <see cref="ReceiverOptions.TopicName"/> + <see cref="ReceiverOptions.SubscriptionName"/> are configured. Broadcast semantics; each head's subscription receives a copy of every published message.</description></item>
/// </list>
/// <para>
/// Per-message flow:
/// </para>
/// <list type="number">
///   <item><description>Self-echo skip: if the message's <c>cirreum.node</c> application property matches this replica's <see cref="INodeIdProvider.NodeId"/>, the message is acked without further work.</description></item>
///   <item><description>Envelope deserialization: parses the JSON body into <see cref="DistributedMessageEnvelope"/>. Failure → dead-letter.</description></item>
///   <item><description>Type resolution: <see cref="Type.GetType(string)"/> against <see cref="DistributedMessageEnvelope.MessageType"/>. Failure → ack with warning (no redelivery loop for genuinely unknown types).</description></item>
///   <item><description>Payload deserialization: typed <see cref="DistributedMessage"/> instance. Failure → dead-letter.</description></item>
///   <item><description>Wrap + publish: build <see cref="DistributedMessageReceived{TMessage}"/> via cached reflection, publish through <see cref="IPublisher"/> within a fresh DI scope.</description></item>
///   <item><description>Acknowledgment: complete on success; abandon on handler failure (broker will redeliver up to its max delivery count, then dead-letter).</description></item>
/// </list>
/// <para>
/// Concurrency is bounded by <see cref="ReceiverOptions.MaxConcurrency"/> per source.
/// At the default of 1, processing is strictly FIFO within each source — the correct
/// default for registry-sync and convergence workloads where ordering matters.
/// </para>
/// </remarks>
internal sealed class DistributedMessageReceiver : IHostedService, IDisposable {

	private readonly IServiceProvider _serviceProvider;
	private readonly IMessageRegistry _registry;
	private readonly INodeIdProvider _nodeIdProvider;
	private readonly ILogger<DistributedMessageReceiver> _logger;
	private readonly ReceiverOptions _options;
	private readonly IMessagingClient _messagingClient;

	private readonly ConcurrentDictionary<Type, Func<IPublisher, object, DistributedMessageEnvelope, CancellationToken, Task<Result>>> _dispatchers = new();

	private CancellationTokenSource? _cts;
	private Task? _queueConsumerTask;
	private Task? _subscriptionConsumerTask;
	private bool _disposed;

	public DistributedMessageReceiver(
		IServiceProvider serviceProvider,
		IMessageRegistry registry,
		INodeIdProvider nodeIdProvider,
		IOptions<ReceiverOptions> options,
		ILogger<DistributedMessageReceiver> logger) {

		this._serviceProvider = serviceProvider;
		this._registry = registry;
		this._nodeIdProvider = nodeIdProvider;
		this._options = options.Value;
		this._logger = logger;

		this._messagingClient = serviceProvider.GetRequiredKeyedService<IMessagingClient>(this._options.InstanceKey);
	}

	public Task StartAsync(CancellationToken cancellationToken) {

		this._logger.ReceiverStarting(
			this._nodeIdProvider.NodeId,
			this._options.QueueName,
			this._options.TopicName,
			this._options.SubscriptionName,
			this._options.MaxConcurrency);

		this._cts = new CancellationTokenSource();
		var ct = this._cts.Token;

		if (!string.IsNullOrWhiteSpace(this._options.QueueName)) {
			this._queueConsumerTask = Task.Run(
				() => this.RunQueueConsumerAsync(this._options.QueueName, ct),
				ct);
		}

		if (!string.IsNullOrWhiteSpace(this._options.TopicName)
			&& !string.IsNullOrWhiteSpace(this._options.SubscriptionName)) {
			this._subscriptionConsumerTask = Task.Run(
				() => this.RunSubscriptionConsumerAsync(
					this._options.TopicName,
					this._options.SubscriptionName,
					ct),
				ct);
		}

		return Task.CompletedTask;
	}

	public async Task StopAsync(CancellationToken cancellationToken) {

		this._logger.ReceiverStopping(this._options.GracefulShutdownTimeout);

		if (this._cts is null) {
			return;
		}

		try {
			await this._cts.CancelAsync();
		} catch {
			// already cancelled / disposed — fine
		}

		var inFlight = new List<Task>(2);
		if (this._queueConsumerTask is not null) {
			inFlight.Add(this._queueConsumerTask);
		}
		if (this._subscriptionConsumerTask is not null) {
			inFlight.Add(this._subscriptionConsumerTask);
		}

		if (inFlight.Count == 0) {
			return;
		}

		try {
			await Task.WhenAll(inFlight).WaitAsync(this._options.GracefulShutdownTimeout, cancellationToken);
		} catch (TimeoutException) {
			this._logger.ReceiverShutdownTimeout();
		} catch (OperationCanceledException) {
			// expected on host shutdown
		}
	}

	private async Task RunQueueConsumerAsync(string queueName, CancellationToken ct) {
		try {
			var receiver = this._messagingClient.UseQueueReceiver(queueName);
			var parallelOptions = new ParallelOptions {
				MaxDegreeOfParallelism = this._options.MaxConcurrency,
				CancellationToken = ct,
			};

			await Parallel.ForEachAsync(
				receiver.ReceiveMessagesStreamAsync(ct),
				parallelOptions,
				async (received, innerCt) => await this.ProcessAsync(received, $"queue:{queueName}", innerCt));

		} catch (OperationCanceledException) {
			// expected on shutdown
		} catch (Exception ex) {
			this._logger.ConsumerLoopFailed(ex, $"queue:{queueName}");
		}
	}

	private async Task RunSubscriptionConsumerAsync(string topic, string subscription, CancellationToken ct) {
		try {
			var receiver = this._messagingClient.UseSubscription(topic, subscription);
			var parallelOptions = new ParallelOptions {
				MaxDegreeOfParallelism = this._options.MaxConcurrency,
				CancellationToken = ct,
			};

			await Parallel.ForEachAsync(
				receiver.ReceiveMessagesStreamAsync(ct),
				parallelOptions,
				async (received, innerCt) => await this.ProcessAsync(received, $"subscription:{topic}/{subscription}", innerCt));

		} catch (OperationCanceledException) {
			// expected on shutdown
		} catch (Exception ex) {
			this._logger.ConsumerLoopFailed(ex, $"subscription:{topic}/{subscription}");
		}
	}

	private async Task ProcessAsync(IMessagingReceivedMessage received, string source, CancellationToken ct) {

		// 1. Self-echo skip without paying deserialization cost
		if (received.Properties.TryGetValue(DistributeMessagingStrings.Property_Node, out var nodeObj)
			&& nodeObj is string remoteNode
			&& remoteNode == this._nodeIdProvider.NodeId) {

			this._logger.SelfEchoSkipped(remoteNode);
			await received.CompleteMessageAsync(ct);
			return;
		}

		// 2. Envelope deserialization
		DistributedMessageEnvelope envelope;
		try {
			envelope = DistributedMessageEnvelope.FromJson(received.ContentString);
		} catch (Exception ex) {
			this._logger.EnvelopeDeserializationFailed(ex, source);
			await received.DeadLetterMessageAsync(
				DistributeMessagingStrings.Event_EnvelopeDeserializationFailed,
				ex.Message,
				ct);
			return;
		}

		// 3. .NET type resolution
		Type? messageType = null;
		try {
			messageType = Type.GetType(envelope.MessageType);
		} catch {
			// fall through with null
		}

		if (messageType is null) {
			this._logger.UnknownMessageType(
				envelope.MessageType,
				envelope.MessageIdentifier,
				envelope.MessageVersion);
			await received.CompleteMessageAsync(ct);
			return;
		}

		// 4. Payload deserialization
		object typedMessage;
		try {
			typedMessage = envelope.DeserializeMessage();
		} catch (Exception ex) {
			this._logger.PayloadDeserializationFailed(
				ex,
				envelope.MessageType,
				envelope.MessageIdentifier,
				envelope.MessageVersion);
			await received.DeadLetterMessageAsync(
				DistributeMessagingStrings.Event_EnvelopeDeserializationFailed,
				ex.Message,
				ct);
			return;
		}

		// 5. Wrap + publish via Conductor (scoped per-dispatch DI)
		try {

			using var scope = this._serviceProvider.CreateScope();
			var publisher = scope.ServiceProvider.GetRequiredService<IPublisher>();
			var dispatcher = this._dispatchers.GetOrAdd(messageType, BuildDispatcher);
			var result = await dispatcher(publisher, typedMessage, envelope, ct);

			if (!result.IsSuccess) {
				this._logger.HandlerFailure(envelope.MessageIdentifier, envelope.MessageVersion);
				await received.AbandonMessageAsync(ct);
				return;
			}

			await received.CompleteMessageAsync(ct);

		} catch (OperationCanceledException) {
			// shutdown — don't ack so broker redelivers later
		} catch (Exception ex) {
			this._logger.HandlerException(
				ex,
				envelope.MessageIdentifier,
				envelope.MessageVersion);
			await received.AbandonMessageAsync(ct);
		}
	}

	private static Func<IPublisher, object, DistributedMessageEnvelope, CancellationToken, Task<Result>> BuildDispatcher(Type messageType) {

		var receivedType = typeof(DistributedMessageReceived<>).MakeGenericType(messageType);
		var publishMethod = typeof(IPublisher)
			.GetMethod(nameof(IPublisher.PublishAsync))
			!.MakeGenericMethod(receivedType);

		// Pin Sequential explicitly to decouple receiver behavior from the host app's
		// global Conductor PublisherStrategy. Apps can't usefully attribute our wrapper
		// type, and an app changing its default to Parallel shouldn't change inbound
		// dispatch ordering. Sequential also runs every handler (stopOnFailure: false)
		// so audit/observer-style co-handlers still fire when a primary handler throws.
		return async (publisher, message, envelope, ct) => {
			var wrapper = Activator.CreateInstance(receivedType, message, envelope)!;
			var task = (Task<Result>)publishMethod.Invoke(publisher, [wrapper, PublisherStrategy.Sequential, ct])!;
			return await task.ConfigureAwait(false);
		};
	}

	public void Dispose() {
		if (this._disposed) {
			return;
		}
		this._disposed = true;
		this._cts?.Cancel();
		this._cts?.Dispose();
	}

}
