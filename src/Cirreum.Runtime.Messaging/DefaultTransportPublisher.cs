namespace Cirreum.Runtime.Messaging;

using Cirreum.Messaging;
using Cirreum.Messaging.Metrics;
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
/// Default implementation of the distributed transport publisher that processes
/// and delivers messages through the messaging infrastructure.
/// </summary>
/// <remarks>
/// Supports both direct (synchronous) and background (batched) delivery modes,
/// with integrated telemetry, distributed tracing, and error handling.
/// </remarks>
internal sealed class DefaultTransportPublisher : IDistributedTransportPublisher, IDisposable {

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
	/// Registry for message type definitions
	/// </summary>
	private readonly IMessageRegistry _registry;

	/// <summary>
	/// Identifier for the producer of messages
	/// </summary>
	private readonly string _producerId;

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
	/// <param name="registry">Registry for message type definitions</param>
	/// <param name="environment">The current <see cref="IDomainEnvironment"/></param>
	/// <param name="options">Configuration options for message distribution</param>
	/// <param name="logger">Logger for recording publisher events and errors</param>
	/// <param name="metricsService">Service for recording messaging metrics</param>
	public DefaultTransportPublisher(
		IServiceProvider serviceProvider,
		IBatchProcessor batchProcessor,
		IMessageRegistry registry,
		IDomainEnvironment environment,
		IOptions<DistributionOptions> options,
		ILogger<DefaultTransportPublisher> logger,
		IMessagingMetricsService metricsService) {

		// Capture required services
		this._batchProcessor = batchProcessor;
		this._logger = logger;
		this._metricsService = metricsService;
		this._registry = registry;

		// Resolve the message client
		var instanceKey = options.Value.Sender.InstanceKey;
		if (string.IsNullOrWhiteSpace(instanceKey)) {
			throw new InvalidOperationException(DistributeMessagingStrings.Error_InstanceKeyRequired);
		}
		this._messagingClient = serviceProvider.GetRequiredKeyedService<IMessagingClient>(instanceKey);

		// Cache common state
		this._topicName = options.Value.Sender.TopicName;
		this._queueName = options.Value.Sender.QueueName;
		this._producerId =
			$"{environment.RuntimeType}:{Assembly.GetEntryAssembly()?.GetName()?.Name ?? "Unknown"}";
		this.useBackgroundDeliveryByDefault
			= options.Value.Sender.BackgroundDelivery.UseBackgroundDeliveryByDefault;
		this._publishMessageActivity = new ActivitySource(DistributeMessagingStrings.MessagingNamespace);

	}

	/// <summary>
	/// Publishes a message to the distributed transport infrastructure.
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
		var subject = $"{definition.Identifier}.v{definition.Version}";
		var target = definition.Target;

		// Received a message for delivery...
		this._metricsService.RecordMessageReceived(
			subject,
			target);

		try {

			// Generate the message envelope
			var envelope = DistributedMessageEnvelope.Create(message, definition, this._producerId);

			// Generate the outbound message
			var outboundMessage = OutboundMessage
				.AsJsonContent(envelope)
				.WithSubject(subject);

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
			if (target == MessageTarget.Topic) {
				await this._messagingClient
					.UseTopic(this._topicName)
					.BroadcastMessageAsync(outboundMessage, token);
			} else if (target == MessageTarget.Queue) {
				await this._messagingClient
					.UseQueueSender(this._queueName)
					.PublishMessageAsync(outboundMessage, token);
			}
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