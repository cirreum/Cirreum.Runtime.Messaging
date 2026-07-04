namespace Cirreum.Runtime.Messaging;

using Cirreum.Messaging;
using Cirreum.Messaging.Metrics;
using Cirreum.Messaging.Options;
using Cirreum.Runtime.Messaging.Batching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// The delivery engine for the <see cref="DistributedMessage"/> channel. Implements the
/// channel's <see cref="IDistributedTransportPublisher{TBase}"/> contract over the
/// configured <see cref="IMessagingClient"/> and provides the typed entry point used by
/// the outbound Conductor bridge (<see cref="OutboundDistributedMessageHandler{TMessage}"/>).
/// </summary>
/// <remarks>
/// Supports both direct (synchronous) and background (batched) delivery modes,
/// with integrated telemetry, distributed tracing, and error handling. Per-message
/// delivery preferences (<see cref="DistributedMessage.UseBackgroundDelivery"/> and
/// <see cref="DistributedMessage.Priority"/>) are honored on the typed path; the
/// envelope-level <see cref="PublishAsync"/> contract applies the channel defaults.
/// </remarks>
internal sealed class DefaultTransportPublisher : IDistributedTransportPublisher<DistributedMessage>, IDisposable {

	/// <summary>
	/// Activity source for distributed tracing of message publishing operations
	/// </summary>
	private readonly ActivitySource _publishMessageActivity;

	/// <summary>
	/// Logger for recording publisher events and errors
	/// </summary>
	private readonly ILogger<DefaultTransportPublisher> _logger;

	/// <summary>
	/// Processor for batched message delivery
	/// </summary>
	private readonly IBatchProcessor _batchProcessor;

	/// <summary>
	/// Service for recording messaging metrics
	/// </summary>
	private readonly IMessagingMetricsService _metricsService;

	/// <summary>
	/// Client for sending messages to the messaging infrastructure
	/// </summary>
	private readonly IMessagingClient _messagingClient;

	/// <summary>
	/// Registry for message type definitions and within-channel routing targets
	/// </summary>
	private readonly IDistributedMessageRegistry _registry;

	/// <summary>
	/// Identifier for the producer of messages
	/// </summary>
	private readonly string _producerId;

	/// <summary>
	/// Provider of the current process replica's node identity
	/// </summary>
	private readonly INodeIdProvider _nodeIdProvider;

	/// <summary>
	/// Name of the topic used for sending notification messages
	/// </summary>
	private readonly string _topicName;

	/// <summary>
	/// Name of the queue used for sending event messages
	/// </summary>
	private readonly string _queueName;

	/// <summary>
	/// The default value from configuration
	/// </summary>
	private readonly bool useBackgroundDeliveryByDefault;

	/// <summary>
	/// Flag indicating whether the publisher has been disposed
	/// </summary>
	private bool _disposed;

	/// <summary>
	/// Initializes a new instance of the DefaultTransportPublisher class.
	/// </summary>
	/// <param name="serviceProvider">Service provider for resolving dependencies</param>
	/// <param name="batchProcessor">Processor for batched message delivery</param>
	/// <param name="registry">Registry for message type definitions and routing targets</param>
	/// <param name="environment">The current <see cref="IDomainEnvironment"/></param>
	/// <param name="nodeIdProvider">Provider of the current replica's node identity, stamped on every outgoing message for self-echo prevention.</param>
	/// <param name="options">Configuration options for the distributed-messaging channel</param>
	/// <param name="logger">Logger for recording publisher events and errors</param>
	/// <param name="metricsService">Service for recording messaging metrics</param>
	public DefaultTransportPublisher(
		IServiceProvider serviceProvider,
		IBatchProcessor batchProcessor,
		IDistributedMessageRegistry registry,
		IDomainEnvironment environment,
		INodeIdProvider nodeIdProvider,
		IOptions<DistributedMessagingOptions> options,
		ILogger<DefaultTransportPublisher> logger,
		IMessagingMetricsService metricsService) {

		// Capture required services
		this._batchProcessor = batchProcessor;
		this._logger = logger;
		this._metricsService = metricsService;
		this._registry = registry;
		this._nodeIdProvider = nodeIdProvider;

		// Resolve the message client
		var instanceKey = options.Value.InstanceKey;
		if (string.IsNullOrWhiteSpace(instanceKey)) {
			throw new InvalidOperationException(DistributeMessagingStrings.Error_InstanceKeyRequired);
		}
		this._messagingClient = serviceProvider.GetRequiredKeyedService<IMessagingClient>(instanceKey);

		// Cache common state
		this._topicName = options.Value.TopicName;
		this._queueName = options.Value.QueueName;
		this._producerId =
			$"{environment.RuntimeType}:{Assembly.GetEntryAssembly()?.GetName()?.Name ?? "Unknown"}";
		this.useBackgroundDeliveryByDefault
			= options.Value.BackgroundDelivery.UseBackgroundDeliveryByDefault;
		this._publishMessageActivity = new ActivitySource(DistributeMessagingStrings.MessagingNamespace);

	}

	/// <summary>
	/// Publishes a typed message to the distributed transport infrastructure.
	/// </summary>
	/// <typeparam name="T">The type of message to publish</typeparam>
	/// <param name="message">The message to publish</param>
	/// <param name="token">A cancellation token to observe while waiting for the operation to complete</param>
	/// <returns>A task representing the asynchronous operation</returns>
	/// <remarks>
	/// Determines the appropriate delivery mode (background or direct) based on message configuration
	/// and global defaults. Provides comprehensive telemetry and error handling throughout
	/// the publishing process.
	/// </remarks>
	/// <exception cref="ObjectDisposedException">Thrown if the publisher has been disposed</exception>
	public async Task PublishMessageAsync<T>(T message, CancellationToken token)
		where T : DistributedMessage {

		ObjectDisposedException.ThrowIf(this._disposed, this);
		var stopwatch = Stopwatch.StartNew();

		using var scope = this._logger.BeginScope(message);
		using var activity = this._publishMessageActivity
			.CreateActivity(
				DistributeMessagingStrings.Activity_PublishMessageAsync,
				ActivityKind.Producer);
		activity?.Start();

		var definition = this._registry.GetDefinitionFor<T>();
		var target = this._registry.GetTargetFor<T>();
		var subject = $"{definition.Identifier}.v{definition.Version}";

		// Received a message for delivery...
		this._metricsService.RecordMessageReceived(
			subject,
			target);

		try {

			// Generate the message envelope
			var envelope = DistributedMessageEnvelope.Create(message, definition, this._producerId);

			// Generate the outbound message
			var outboundMessage = this.CreateOutboundMessage(envelope, subject);

			// Background (parallel) processing...
			if (message.UseBackgroundDelivery ?? this.useBackgroundDeliveryByDefault) {

				// Use the batch processor
				var queuedPriority =
					await this._batchProcessor.SubmitMessageAsync(
						outboundMessage,
						target,
						message.Priority,
						token);
				stopwatch.Stop();

				// Record message queued
				this._metricsService.RecordMessageQueued(
					subject,
					target,
					stopwatch.ElapsedMilliseconds,
					message.Priority);

				// Add trace event
				activity?.AddEvent(new(
					name: DistributeMessagingStrings.Event_MessageQueueForDelivery,
					tags: [
						new(DistributeMessagingStrings.Tag_Subject, subject),
						new(DistributeMessagingStrings.Tag_MessagePriority, message.Priority.ToString()),
						new(DistributeMessagingStrings.Tag_QueuedPriority, queuedPriority.ToString())
					])
				);

				// Done.
				return;
			}

			// Standard (sequential) processing
			await this.SendDirectAsync(outboundMessage, target, token);
			stopwatch.Stop();

			// Record successful delivery metrics
			this._metricsService.RecordMessageDelivered(
				subject,
				target,
				stopwatch.ElapsedMilliseconds);

			// Add trace event
			activity?.AddEvent(new(
				name: DistributeMessagingStrings.Event_MessageSentDirectly,
				tags: [new(DistributeMessagingStrings.Tag_Subject, subject)]));

		} catch (OperationCanceledException oex) when (oex.CancellationToken == token) {
			// no logging, just return, as we're being shutdown/cancelled
		} catch (Exception ex) {
			stopwatch.Stop();
			this._metricsService.RecordMessageFailed(
				subject,
				target,
				ex.GetType().Name,
				stopwatch.ElapsedMilliseconds);
			throw;
		}
	}

	/// <inheritdoc/>
	/// <remarks>
	/// The wire-format envelope carries no per-message delivery preferences, so this
	/// surface applies the channel defaults: background (batched) delivery at
	/// <see cref="DistributedMessagePriority.Standard"/> priority when
	/// <c>BackgroundDelivery.UseBackgroundDeliveryByDefault</c> is set, otherwise a
	/// direct send.
	/// </remarks>
	public async Task PublishAsync(
		DistributedMessageEnvelope envelope,
		MessageTarget target,
		CancellationToken cancellationToken = default) {

		ObjectDisposedException.ThrowIf(this._disposed, this);
		var stopwatch = Stopwatch.StartNew();

		using var activity = this._publishMessageActivity
			.CreateActivity(
				DistributeMessagingStrings.Activity_PublishMessageAsync,
				ActivityKind.Producer);
		activity?.Start();

		var subject = $"{envelope.MessageIdentifier}.v{envelope.MessageVersion}";

		// Received a message for delivery...
		this._metricsService.RecordMessageReceived(
			subject,
			target);

		try {

			// Generate the outbound message
			var outboundMessage = this.CreateOutboundMessage(envelope, subject);

			// Background (parallel) processing...
			if (this.useBackgroundDeliveryByDefault) {

				var queuedPriority =
					await this._batchProcessor.SubmitMessageAsync(
						outboundMessage,
						target,
						DistributedMessagePriority.Standard,
						cancellationToken);
				stopwatch.Stop();

				this._metricsService.RecordMessageQueued(
					subject,
					target,
					stopwatch.ElapsedMilliseconds,
					DistributedMessagePriority.Standard);

				activity?.AddEvent(new(
					name: DistributeMessagingStrings.Event_MessageQueueForDelivery,
					tags: [
						new(DistributeMessagingStrings.Tag_Subject, subject),
						new(DistributeMessagingStrings.Tag_QueuedPriority, queuedPriority.ToString())
					])
				);

				return;
			}

			// Standard (sequential) processing
			await this.SendDirectAsync(outboundMessage, target, cancellationToken);
			stopwatch.Stop();

			this._metricsService.RecordMessageDelivered(
				subject,
				target,
				stopwatch.ElapsedMilliseconds);

			activity?.AddEvent(new(
				name: DistributeMessagingStrings.Event_MessageSentDirectly,
				tags: [new(DistributeMessagingStrings.Tag_Subject, subject)]));

		} catch (OperationCanceledException oex) when (oex.CancellationToken == cancellationToken) {
			// no logging, just return, as we're being shutdown/cancelled
		} catch (Exception ex) {
			stopwatch.Stop();
			this._metricsService.RecordMessageFailed(
				subject,
				target,
				ex.GetType().Name,
				stopwatch.ElapsedMilliseconds);
			throw;
		}
	}

	/// <summary>
	/// Creates the broker-agnostic outbound message for an envelope, stamping the
	/// cross-broker filterable metadata as application properties.
	/// </summary>
	/// <remarks>
	/// Each broker maps <see cref="OutboundMessage.Properties"/> to its native filterable
	/// property bag (Service Bus ApplicationProperties, AWS SNS message attributes, Kafka
	/// headers, NATS headers). Filter expressions live in infrastructure-as-code per
	/// deployment.
	/// </remarks>
	private OutboundMessage CreateOutboundMessage(DistributedMessageEnvelope envelope, string subject) {

		var outboundMessage = OutboundMessage
			.AsJsonContent(envelope)
			.WithSubject(subject);

		outboundMessage.Properties[DistributeMessagingStrings.Property_Identifier] = envelope.MessageIdentifier;
		outboundMessage.Properties[DistributeMessagingStrings.Property_Version] = envelope.MessageVersion;
		outboundMessage.Properties[DistributeMessagingStrings.Property_Producer] = envelope.ProducerId;
		outboundMessage.Properties[DistributeMessagingStrings.Property_Node] = this._nodeIdProvider.NodeId;

		return outboundMessage;
	}

	/// <summary>
	/// Sends a single outbound message directly to the target's configured destination.
	/// </summary>
	private async Task SendDirectAsync(
		OutboundMessage outboundMessage,
		MessageTarget target,
		CancellationToken token) {

		if (target == MessageTarget.Topic) {
			await this._messagingClient
				.UseTopic(this._topicName)
				.BroadcastMessageAsync(outboundMessage, token);
		} else if (target == MessageTarget.Queue) {
			await this._messagingClient
				.UseQueueSender(this._queueName)
				.PublishMessageAsync(outboundMessage, token);
		}
	}

	/// <summary>
	/// Releases resources used by the DefaultTransportPublisher.
	/// </summary>
	/// <remarks>
	/// Properly disposes of all owned resources including the metrics service,
	/// activity source, and batch processor to prevent resource leaks.
	/// </remarks>
	public void Dispose() {
		if (this._disposed) {
			return;
		}
		this._disposed = true;
		this._logger.ShuttingDown();
		this._metricsService.Dispose();
		this._publishMessageActivity.Dispose();
		this._batchProcessor.Dispose();
	}

}