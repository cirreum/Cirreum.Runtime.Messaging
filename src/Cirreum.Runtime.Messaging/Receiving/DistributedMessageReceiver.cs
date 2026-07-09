namespace Cirreum.Runtime.Messaging.Receiving;

using Cirreum;
using Cirreum.Conductor;
using Cirreum.Messaging;
using Cirreum.Messaging.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
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
///   <item><description>Type resolution by wire identity through the registry (<c>ResolveType(MessageIdentifier, MessageVersion)</c>). An unresolved identity is disposed per source: a <b>queue</b> dead-letters (addressed to us — an unknown identity is a misconfiguration worth triage); a <b>topic subscription</b> completes and logs (fan-out delivers family members this consumer need not carry — normal, never a redelivery loop).</description></item>
///   <item><description>Payload deserialization: typed <see cref="DistributedMessage"/> instance via <see cref="DistributedMessageEnvelope.DeserializeMessage(Type)"/>. Failure → dead-letter.</description></item>
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
	private readonly INodeIdProvider _nodeIdProvider;
	private readonly IDistributedMessageRegistry _registry;
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
		INodeIdProvider nodeIdProvider,
		IDistributedMessageRegistry registry,
		IOptions<ReceiverOptions> options,
		ILogger<DistributedMessageReceiver> logger) {

		this._serviceProvider = serviceProvider;
		this._nodeIdProvider = nodeIdProvider;
		this._registry = registry;
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
				async (received, innerCt) => await this.ProcessAsync(received, $"queue:{queueName}", isQueueSource: true, innerCt));

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
				async (received, innerCt) => await this.ProcessAsync(received, $"subscription:{topic}/{subscription}", isQueueSource: false, innerCt));

		} catch (OperationCanceledException) {
			// expected on shutdown
		} catch (Exception ex) {
			this._logger.ConsumerLoopFailed(ex, $"subscription:{topic}/{subscription}");
		}
	}

	private async Task ProcessAsync(IMessagingReceivedMessage received, string source, bool isQueueSource, CancellationToken ct) {

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

		// 3. Type resolution by wire identity through the registry, which only ever
		// selects from this process's own vetted scan set. A null is a normal foreign
		// identity — the envelope's diagnostic MessageType is never a resolution input.
		var messageType = this._registry.ResolveType(envelope.MessageIdentifier, envelope.MessageVersion);

		if (messageType is null) {

			// Per-source disposition. A queue is addressed to us, so an unknown identity
			// is a misconfiguration (a missing assembly / a producer ahead of us) worth
			// operator triage — dead-letter it. A topic subscription fans every family
			// member out to every subscriber, so members this consumer doesn't carry are
			// normal weather — complete and move on, never a redelivery loop.
			if (isQueueSource) {
				this._logger.UnknownMessageTypeDeadLettered(
					envelope.MessageType,
					envelope.MessageIdentifier,
					envelope.MessageVersion);
				await received.DeadLetterMessageAsync(
					DistributeMessagingStrings.Event_UnknownMessageType,
					$"No type registered for identity {envelope.MessageIdentifier} v{envelope.MessageVersion}.",
					ct);
				return;
			}

			this._logger.UnknownMessageType(
				envelope.MessageType,
				envelope.MessageIdentifier,
				envelope.MessageVersion);
			await received.CompleteMessageAsync(ct);
			return;
		}

		// 4. Payload deserialization to the registry-resolved concrete type.
		object typedMessage;
		try {
			typedMessage = envelope.DeserializeMessage(messageType);
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

	// The open generic DispatchTypedAsync<T>, closed per message type and cached as a
	// delegate in _dispatchers — so the per-message dispatch path carries no reflection.
	private static readonly MethodInfo DispatchTypedMethod =
		typeof(DistributedMessageReceiver).GetMethod(
			nameof(DispatchTypedAsync),
			BindingFlags.NonPublic | BindingFlags.Static)!;

	private static Func<IPublisher, object, DistributedMessageEnvelope, CancellationToken, Task<Result>> BuildDispatcher(Type messageType) =>
		DispatchTypedMethod
			.MakeGenericMethod(messageType)
			.CreateDelegate<Func<IPublisher, object, DistributedMessageEnvelope, CancellationToken, Task<Result>>>();

	/// <summary>
	/// Typed inbound dispatch: wraps the received payload in
	/// <see cref="DistributedMessageReceived{TMessage}"/> and publishes it through Conductor.
	/// A delegate closed over the concrete message type is built once per type (via
	/// <see cref="BuildDispatcher"/>) and cached in <c>_dispatchers</c>, so the per-message
	/// path is a direct call with no reflection.
	/// </summary>
	/// <remarks>
	/// Pins <see cref="PublisherStrategy.Sequential"/> to decouple receiver behavior from the
	/// host app's global Conductor <c>PublisherStrategy</c>: apps can't usefully attribute our
	/// wrapper type, an app switching its default to Parallel shouldn't reorder inbound
	/// dispatch, and Sequential runs every handler (<c>stopOnFailure: false</c>) so
	/// audit/observer co-handlers still fire when a primary handler throws.
	/// </remarks>
	private static Task<Result> DispatchTypedAsync<TMessage>(
		IPublisher publisher,
		object message,
		DistributedMessageEnvelope envelope,
		CancellationToken ct)
		where TMessage : notnull, DistributedMessage =>
		publisher.PublishAsync(
			new DistributedMessageReceived<TMessage>((TMessage)message, envelope),
			PublisherStrategy.Sequential,
			ct);

	public void Dispose() {
		if (this._disposed) {
			return;
		}
		this._disposed = true;
		this._cts?.Cancel();
		this._cts?.Dispose();
	}

}
