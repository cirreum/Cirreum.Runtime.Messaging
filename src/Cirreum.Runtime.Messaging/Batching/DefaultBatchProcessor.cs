namespace Cirreum.Runtime.Messaging.Batching;

using Cirreum.Messaging;
using Cirreum.Messaging.Metrics;
using Cirreum.Messaging.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Threading.Channels;
using TagNames = BathProcessorTagNames;

/// <summary>
/// Default implementation of <see cref="IBatchProcessor"/> that provides efficient batched message delivery
/// with prioritization, rate limiting, circuit breaking, and dynamic scheduling.
/// </summary>
/// <remarks>
/// This class manages a background processing pipeline that:
/// 1. Queues outbound messages in a priority-based channel
/// 2. Dynamically adjusts batch size and timing based on load and time-of-day profiles
/// 3. Applies circuit breaking to prevent cascading failures
/// 4. Groups messages by type for parallel processing
/// 5. Collects detailed metrics for monitoring and troubleshooting
/// </remarks>
internal class DefaultBatchProcessor : IBatchProcessor, IHostedService, IDisposable {

	/// <summary>
	/// Circuit breaker that prevents processing attempts during error conditions
	/// </summary>
	private readonly BatchCircuitBreaker _circuitBreaker;

	/// <summary>
	/// Component that manages message prioritization and priority promotion
	/// </summary>
	private readonly MessagePrioritizer _messagePrioritizer;

	/// <summary>
	/// Scheduler that dynamically adjusts batch timing and capacity based on load
	/// </summary>
	private readonly BatchScheduler _batchScheduler;

	/// <summary>
	/// Name of the topic used for sending notification messages
	/// </summary>
	private readonly string _topicName;

	/// <summary>
	/// Name of the queue used for sending event messages
	/// </summary>
	private readonly string _queueName;

	/// <summary>
	/// Logger for recording processor events and errors
	/// </summary>
	private readonly ILogger<DefaultBatchProcessor> _logger;

	/// <summary>
	/// Service for recording messaging metrics
	/// </summary>
	private readonly IMessagingMetricsService _metricsService;

	/// <summary>
	/// Activity source for distributed tracing of batch processing
	/// </summary>
	private readonly ActivitySource _processBatchActivity;

	/// <summary>
	/// Client for sending messages to the messaging infrastructure
	/// </summary>
	private readonly IMessagingClient _messagingClient;

	/// <summary>
	/// Configuration options for background message delivery
	/// </summary>
	private readonly BackgroundDeliveryOptions _deliveryOptions;

	/// <summary>
	/// Channel for queueing messages for background processing
	/// </summary>
	private readonly Channel<BatchItem> _channel;

	/// <summary>
	/// Cancellation token source for stopping background tasks during disposal
	/// </summary>
	private readonly CancellationTokenSource? _cts;

	/// <summary>
	/// Timer for periodically reporting queue depth metrics
	/// </summary>
	private PeriodicTimer? _reportingTimer;

	/// <summary>
	/// Background task that reports queue depth metrics
	/// </summary>
	private Task? _reportingTask;

	/// <summary>
	/// Background task that processes queued messages
	/// </summary>
	private Task? _processingTask;

	/// <summary>
	/// Indicates if the background processor is running.
	/// </summary>
	private bool isRunning;

	private string lastBatchWaitAndFill = "";

	private readonly int minimumCircuitBreakerDelayMs = 50; // 50ms
	private readonly int maximumCircuitBreakerDelayMs = 5000; // 5s

	/// <summary>
	/// Flag indicating whether the processor has been disposed
	/// </summary>
	private bool _disposed;

	/// <summary>
	/// Initializes a new instance of the DefaultBatchProcessor class.
	/// </summary>
	/// <param name="serviceProvider">Service provider for resolving dependencies</param>
	/// <param name="options">Configuration options for message distribution</param>
	/// <param name="logger">Logger for recording processor events and errors</param>
	/// <param name="metricsService">Service for recording messaging metrics</param>
	public DefaultBatchProcessor(
		IServiceProvider serviceProvider,
		IOptions<DistributionOptions> options,
		ILogger<DefaultBatchProcessor> logger,
		IMessagingMetricsService metricsService) {

		this._logger = logger;
		this._metricsService = metricsService;
		this._processBatchActivity = new ActivitySource(DistributeMessagingStrings.MessagingNamespace);

		// Resolve Messaging Client
		var instanceKey = options.Value.Sender.InstanceKey;
		if (string.IsNullOrWhiteSpace(instanceKey)) {
			ArgumentException.ThrowIfNullOrWhiteSpace(instanceKey, nameof(instanceKey));
		}
		this._messagingClient = serviceProvider.GetRequiredKeyedService<IMessagingClient>(instanceKey);

		// Get common metadata
		this._topicName = options.Value.Sender.TopicName;
		this._queueName = options.Value.Sender.QueueName;

		// Initialize support for background delivery
		this._deliveryOptions = options.Value.Sender.BackgroundDelivery;
		this._circuitBreaker = new(
			this._deliveryOptions.CircuitBreakerThreshold,
			this._deliveryOptions.CircuitResetTimeout);
		this._messagePrioritizer = new(
			this._logger,
			this._deliveryOptions.PriorityMessageRateLimit,
			this._deliveryOptions.PriorityAgePromotionThreshold);
		var activeProfileName = this._deliveryOptions.ActiveTimeBatchingProfile;
		if (this._deliveryOptions.TimeBatchingProfiles.TryGetValue(activeProfileName, out var profile)) {
			this._logger.ActiveTimeBatchingProfile(nameof(DefaultBatchProcessor), activeProfileName);
			var profileDetailsJson = profile.ToJson();
			this._logger.ActiveTimeBatchingProfileDetails(nameof(DefaultBatchProcessor), profileDetailsJson);
			this._batchScheduler = new(profile);
		} else {
			this._batchScheduler = new();
		}
		this._channel = Channel.CreateBounded<BatchItem>(
			new BoundedChannelOptions(this._deliveryOptions.QueueCapacity) {
				FullMode = BoundedChannelFullMode.Wait,
				SingleReader = true,
				SingleWriter = false
			});

		// Initialized
		this._logger.PublisherInitialized(nameof(DefaultBatchProcessor), this._deliveryOptions.QueueCapacity);

		// Start background work...
		this._cts = new CancellationTokenSource();
	}

	/// <summary>
	/// Starts the batch processor when the application starts.
	/// </summary>
	/// <param name="cancellationToken">Token to monitor for cancellation requests</param>
	/// <returns>A task representing the asynchronous operation</returns>
	public Task StartAsync(CancellationToken cancellationToken) {
		this.Start();
		return Task.CompletedTask;
	}

	/// <summary>
	/// Stops the batch processor when the application stops.
	/// </summary>
	/// <param name="cancellationToken">Token to monitor for cancellation requests</param>
	/// <returns>A task representing the asynchronous operation</returns>
	public Task StopAsync(CancellationToken cancellationToken) {

		if (this._cts != null && !this._disposed) {
			this._logger.StoppingBackgroundProcessor();
			this._cts.Cancel();

			try {

				// Create a timeout for graceful shutdown
				var shutdownTimeout = TimeSpan.FromSeconds(5);

				// Wait for processing to complete but respect the timeout
				if (this._processingTask != null) {
					if (!this._processingTask.Wait(shutdownTimeout, cancellationToken)) {
						this._logger.ShutdownTimeout();
					}
				}

				if (this._reportingTask != null) {
					if (!this._reportingTask.Wait(shutdownTimeout, cancellationToken)) {
						this._logger.ReportingShutdownTimeout();
					}
				}
			} catch (Exception ex) {
				this._logger.ShutdownError(ex);
			}

			// Mark as not running
			this.isRunning = false;
		}

		return Task.CompletedTask;
	}

	/// <inheritdoc/>
	public void Start() {
		if (this.isRunning) {
			return;
		}
		if (this._cts is null) {
			return;
		}
		this.isRunning = true;
		this._processingTask = this.DeliverMessagesAsync(this._cts.Token);
		this._reportingTimer = new PeriodicTimer(TimeSpan.FromSeconds(10));
		this._reportingTask = this.RecordQueueDepth(this._cts.Token);
	}

	/// <inheritdoc/>
	public async Task<DistributedMessagePriority> SubmitMessageAsync(
		OutboundMessage message,
		MessageTarget target,
		DistributedMessagePriority priority,
		CancellationToken token) {

		if (!this.isRunning) {
			throw new InvalidOperationException("Background processing has not been started.");
		}

		// Message Prioritization
		var channelPriority = this._messagePrioritizer.GetQueuePriority(priority, message.GetType().Name);

		// Write the message
		await this._channel.Writer.WriteAsync(new BatchItem(
			message,
			target,
			DateTime.UtcNow,
			channelPriority
		), token);

		return channelPriority;
	}

	/// <summary>
	/// Background task that continuously processes queued messages in optimized batches.
	/// </summary>
	/// <param name="cancellationToken">Token to monitor for cancellation requests</param>
	/// <returns>A task representing the asynchronous operation</returns>
	private async Task DeliverMessagesAsync(CancellationToken cancellationToken) {

		this._logger.BackgroundProcessorStarted(
			nameof(DefaultBatchProcessor),
			this._deliveryOptions.BatchCapacity,
			this._deliveryOptions.BatchFillWaitTime);

		var delayTime = TimeSpan.FromMilliseconds(
			Math.Clamp(
				this._deliveryOptions.BatchFillWaitTime.TotalMilliseconds / 2,
				this.minimumCircuitBreakerDelayMs,
				this.maximumCircuitBreakerDelayMs
			)
		);
		var batch = new List<BatchItem>();
		try {
			while (!cancellationToken.IsCancellationRequested) {

				if (!this._circuitBreaker.TryEnter(this._logger)) {
					await Task.Delay(delayTime, cancellationToken);
					continue;
				}

				// Normal batch processing
				await this.FillBatchAndProcess(batch, cancellationToken);
			}
		} catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
			this._logger.ProcessorCancelled();
		} catch (Exception ex) {
			this._logger.BackgroundProcessorError(ex, batch.Count);
			throw;
		}

	}

	/// <summary>
	/// Fills a batch with messages from the queue and processes them.
	/// </summary>
	/// <param name="batch">The batch to fill with messages</param>
	/// <param name="cancellationToken">Token to monitor for cancellation requests</param>
	/// <returns>A task representing the asynchronous operation</returns>
	/// <remarks>
	/// Uses dynamic scheduling to determine optimal batch size and wait time based
	/// on current queue depth and time-of-day profiles.
	/// </remarks>
	private async Task FillBatchAndProcess(
		List<BatchItem> batch,
		CancellationToken cancellationToken) {

		// Calculate dynamic wait time and capacity based on current load and timing
		var (waitTime, capacity) = this._batchScheduler
			.CalculateWaitTimeAndCapacity(
			this._deliveryOptions.BatchCapacity,
			this._channel.Reader.Count,
			this._deliveryOptions.BatchFillWaitTime);
		batch.Clear();
		batch.Capacity = capacity;
		batch.Clear();
		var newBatchWaitAndFill = $"{waitTime}{capacity}";
		if (lastBatchWaitAndFill != newBatchWaitAndFill) {
			lastBatchWaitAndFill = newBatchWaitAndFill;
			this._logger.DynamicBatchParameters(waitTime, capacity);
		}

		// Set the batch deadline
		using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeoutCts.CancelAfter(waitTime);

		try {

			// Keep trying to fill the batch until we reach capacity or timeout
			while (!timeoutCts.IsCancellationRequested
					&& !cancellationToken.IsCancellationRequested
					&& batch.Count < capacity) {
				// Waits until an item is available or times out
				var item = await this._channel.Reader.ReadAsync(timeoutCts.Token);
				this._messagePrioritizer.ApplyEffectivePriority(item);
				batch.Add(item);
				this._metricsService.RecordMessageDequeued(
					item.Message.Subject,
					item.Target,
					(long)DateTime.UtcNow.Subtract(item.QueuedTime).TotalMilliseconds,
					item.EffectivePriority);
			}

		} catch (OperationCanceledException) {
		}

		// If main caller has cancelled, simply exit
		if (cancellationToken.IsCancellationRequested) {
			return;
		}

		// Process the batch if we have items...
		if (batch.Count > 0) {

			// Record partial batch
			if (batch.Count < capacity) {
				this._metricsService.RecordPartialBatch(
					capacity,
					batch.Count);
			}

			await this.ProcessBatchAsync(batch, cancellationToken);
		}
	}

	/// <summary>
	/// Processes a batch of messages, grouping them by message kind and sending them to the appropriate destinations.
	/// </summary>
	/// <param name="batch">The batch of messages to process</param>
	/// <param name="cancellationToken">Token to monitor for cancellation requests</param>
	/// <returns>A task representing the asynchronous operation</returns>
	/// <remarks>
	/// Orchestrates the batch processing workflow with distributed tracing, metrics collection,
	/// and error handling. Messages are grouped by kind (Notification or Event) and processed
	/// in parallel for optimal throughput.
	/// </remarks>
	private async Task ProcessBatchAsync(
		List<BatchItem> batch,
		CancellationToken cancellationToken) {

		// Events
		const string BatchDeliveryStarted = "BatchDeliveryStarted";
		const string BatchDeliveryCanceled = "BatchDeliveryCanceled";
		const string BatchDeliveryEnded = "BatchDeliveryEnded";

		// Setup method state
		var batchStopwatch = Stopwatch.StartNew();
		var countOfBatchKinds = 0;
		var totalSuccessCount = 0;
		var totalFailureCount = 0;

		using var batchActivity = this._processBatchActivity.CreateActivity(
			nameof(ProcessBatchAsync),
			ActivityKind.Producer,
			new ActivityContext(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded),
			null,
			null,
			ActivityIdFormat.W3C);

		// Captured error tags, if any
		ActivityTagsCollection processErrorTags = [];

		// Start...
		try {

			// Start the activity...
			batchActivity?.Start();
			batchActivity?.AddEvent(new ActivityEvent(BatchDeliveryStarted));

			// Log that we started
			this._logger.ProcessingBatch(batch.Count);

			// Sort by Priority, then Group by MessageTarget (Notification or Event)
			var itemsByTarget = batch
				.OrderByDescending(item => item.EffectivePriority)
				.GroupBy(item => item.Target)
				.ToList();
			countOfBatchKinds = itemsByTarget.Count;

			// Process each kind in parallel
			var processingTasks = itemsByTarget.Select(kindItems =>
				this.ProcessKindItemsAsync(kindItems, batchActivity, cancellationToken))
				.ToList();

			// Wait for all processing to complete and aggregate results
			var results = await Task.WhenAll(processingTasks);

			// Sum up success and failure counts
			foreach (var (success, failure) in results) {
				totalSuccessCount += success;
				totalFailureCount += failure;
			}

			// Add canceled event
			if (cancellationToken.IsCancellationRequested) {
				batchActivity?.AddEvent(new ActivityEvent(BatchDeliveryCanceled));
			}

		} catch (OperationCanceledException) {
			batchStopwatch.Stop();
			batchActivity?.AddEvent(new ActivityEvent(BatchDeliveryCanceled));
		} catch (Exception ex) {
			batchStopwatch.Stop();

			// Capture exception
			processErrorTags = new() {
				{ TagNames.BatchErrorType, ex.GetType().Name },
				{ TagNames.BatchErrorMessage, ex.Message }
			};

			// Log the error
			this._logger.BatchProcessingError(ex);

			// Record all remaining items, if any, as failed
			foreach (var item in batch.Where(i => !i.IsCompleted)) {
				this._metricsService.RecordMessageFailed(
				   item.Message.Subject,
				   item.Target,
				   ex.GetType().Name,
				   batchStopwatch.ElapsedMilliseconds);
				totalFailureCount++;
			}

		} finally {
			batchStopwatch.Stop();

			// Add to existing batch metrics
			var standardCount = batch.Count(i => i.EffectivePriority == DistributedMessagePriority.Standard);
			var timeSensitiveCount = batch.Count(i => i.EffectivePriority == DistributedMessagePriority.TimeSensitive);
			var systemCount = batch.Count(i => i.EffectivePriority == DistributedMessagePriority.System);

			// Record batch-level metrics
			this._metricsService.RecordBatchProcessed(
				batch.Capacity,
				batch.Count,
				batchStopwatch.ElapsedMilliseconds,
				totalSuccessCount,
				totalFailureCount,
				standardCount,
				timeSensitiveCount,
				systemCount);

			// Add ending event
			var completionTags = new ActivityTagsCollection {
				{ TagNames.BatchDuration, batchStopwatch.ElapsedMilliseconds },
				{ TagNames.BatchCapacity, batch.Capacity },
				{ TagNames.BatchSize, batch.Count },
				{ TagNames.BatchStandardCount, standardCount },
				{ TagNames.BatchTimeSensitiveCount, timeSensitiveCount },
				{ TagNames.BatchSystemCount, systemCount },
				{ TagNames.BatchDeliveredCount, totalSuccessCount },
				{ TagNames.BatchFailedCount, totalFailureCount },
				{ TagNames.BatchTargetCount, countOfBatchKinds }
			};
			foreach (var errorTag in processErrorTags) {
				completionTags.Add(errorTag);
			}
			if (cancellationToken.IsCancellationRequested) {
				completionTags.Add(new(TagNames.BatchCancelled, bool.TrueString));
			}
			batchActivity?.AddEvent(new ActivityEvent(BatchDeliveryEnded, tags: completionTags));
		}
	}

	/// <summary>
	/// Processes a group of messages of the same kind (Notification or Event).
	/// </summary>
	/// <param name="targetItems">Group of messages of the same type of target</param>
	/// <param name="parentActivity">Parent activity for distributed tracing</param>
	/// <param name="cancellationToken">Token to monitor for cancellation requests</param>
	/// <returns>A tuple containing success and failure counts</returns>
	/// <remarks>
	/// This method is executed in parallel for different message kinds, allowing
	/// notifications and events to be processed simultaneously for better throughput.
	/// </remarks>
	private async Task<(int successCount, int failureCount)> ProcessKindItemsAsync(
		IGrouping<MessageTarget, BatchItem> targetItems,
		Activity? parentActivity,
		CancellationToken cancellationToken) {

		//
		// This method is executed in parallel
		// yet only processes a single kind of
		// batch items at a time. This allows us
		// to send Events to a Queue while at
		// the same time sending Notifications
		// to a Topic
		//
		var target = targetItems.Key; // Notification (Topic) or Event (Queue)

		// Events for this method
		var PublishedBatchStarted = $"PublishBatch{target}Started";
		var PublishedBatchSuccess = $"PublishBatch{target}Success";
		var PublishedBatchError = $"PublishBatch{target}Error";
		var PublishedBatchCanceled = $"PublishBatch{target}Canceled";

		var targetSuccessCount = 0;
		var targetFailureCount = 0;

		var items = targetItems.ToList();
		var batchTarget = $"{target}".ToLowerInvariant();
		var batchTargetStartTimestamp = Stopwatch.GetTimestamp();

		try {

			// See if we've been cancelled
			if (cancellationToken.IsCancellationRequested) {
				return (targetSuccessCount, targetFailureCount);
			}

			// Add batch kind started event
			parentActivity?.AddEvent(new ActivityEvent(PublishedBatchStarted));


			// Send as a single bulk batch
			await this.SendMessagesAsync(items.Select(i => i.Message), target, cancellationToken);
			var completedTimetamp = DateTime.UtcNow;
			var elapsed = Stopwatch.GetElapsedTime(batchTargetStartTimestamp);

			// Calculate estimated individual processing time
			var estimatedItemTime = (int)Math.Round(elapsed.TotalMilliseconds) / items.Count;

			// Record all as success
			foreach (var item in items) {

				// Calculate total end-to-end time
				var itemTotalDuration = (long)completedTimetamp.Subtract(item.QueuedTime).TotalMilliseconds;

				// Record with batch context
				this._metricsService.RecordMessageDeliveredInBatch(
					item.Message.Subject,
					target,
					estimatedItemTime, // Estimated individual time
					itemTotalDuration); // Total time from queue to delivery

				// Complete item
				item.IsCompleted = true;
				targetSuccessCount++;
			}

			// Add success event
			var successTags = new ActivityTagsCollection {
				{ TagNames.BatchTarget, batchTarget},
				{ TagNames.ForTarget(batchTarget, TagNames.Metrics.Duration), estimatedItemTime },
				{ TagNames.ForTarget(batchTarget, TagNames.Metrics.SuccessCount), targetSuccessCount }
			};
			parentActivity?.AddEvent(new ActivityEvent(PublishedBatchSuccess, tags: successTags));

		} catch (OperationCanceledException) {
			var elapsed = Stopwatch.GetElapsedTime(batchTargetStartTimestamp);

			// Add batch target cancelled event
			var canceledTags = new ActivityTagsCollection {
				{ TagNames.BatchTarget, batchTarget},
				{ TagNames.ForTarget(batchTarget, TagNames.Metrics.Duration), elapsed },
				{ TagNames.ForTarget(batchTarget, TagNames.Metrics.SuccessCount), targetSuccessCount },
				{ TagNames.ForTarget(batchTarget, TagNames.Metrics.FailureCount), targetFailureCount }
			};
			parentActivity?.AddEvent(new ActivityEvent(PublishedBatchCanceled));

		} catch (Exception ex) {
			var elapsed = Stopwatch.GetElapsedTime(batchTargetStartTimestamp);

			// Log the error
			this._logger.BatchProcessingError(ex);

			// Record all as failed
			foreach (var item in items) {
				this._metricsService.RecordMessageFailed(
					item.Message.Subject,
					target,
					ex.GetType().Name,
					(int)elapsed.TotalMilliseconds);

				// Complete item
				item.IsCompleted = true;
				targetFailureCount++;
			}

			// Add error event
			var errorTags = new ActivityTagsCollection {
				{ TagNames.ForTarget(batchTarget, TagNames.Metrics.ErrorType), ex.GetType().Name },
				{ TagNames.ForTarget(batchTarget, TagNames.Metrics.ErrorMessage), ex.Message },
				{ TagNames.BatchTarget, batchTarget},
				{ TagNames.ForTarget(batchTarget, TagNames.Metrics.Duration), (int)elapsed.TotalMilliseconds },
				{ TagNames.ForTarget(batchTarget, TagNames.Metrics.SuccessCount), targetSuccessCount },
				{ TagNames.ForTarget(batchTarget, TagNames.Metrics.FailureCount), targetFailureCount }
			};
			parentActivity?.AddEvent(new ActivityEvent(PublishedBatchError, tags: errorTags));
		}

		return (targetSuccessCount, targetFailureCount);
	}

	/// <summary>
	/// Sends a batch of messages to the appropriate destination based on the message kind.
	/// </summary>
	/// <param name="messages">The messages to send</param>
	/// <param name="target">The target of the messages (Topic or Queue)</param>
	/// <param name="cancellationToken">Token to monitor for cancellation requests</param>
	/// <returns>A task representing the asynchronous operation</returns>
	/// <remarks>
	/// Handles circuit breaking for fault tolerance. Notifications are sent to topics,
	/// while events are sent to queues.
	/// </remarks>
	private async Task SendMessagesAsync(
		IEnumerable<OutboundMessage> messages,
		MessageTarget target,
		CancellationToken cancellationToken) {

		try {

			if (target == MessageTarget.Topic) {
				await this._messagingClient
					.UseTopic(this._topicName)
					.BroadcastMessagesAsync(messages, cancellationToken: cancellationToken);
			} else if (target == MessageTarget.Queue) {
				await this._messagingClient
					.UseQueueSender(this._queueName)
					.PublishMessagesAsync(messages, cancellationToken: cancellationToken);
			}

			// Success - reset failure counter
			this._circuitBreaker.Reset();

		} catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
			// Don't count cancellation as a failure - this is normal operation
			throw;
		} catch (Exception ex) {

			this._circuitBreaker.HandleFault(target, this._logger, ex);

			// Rethrow the original exception
			throw;
		}
	}

	/// <summary>
	/// Background task that periodically records the current queue depth metrics.
	/// </summary>
	/// <param name="cancellationToken">Token to monitor for cancellation requests</param>
	/// <returns>A task representing the asynchronous operation</returns>
	private async Task RecordQueueDepth(
		CancellationToken cancellationToken) {
		if (this._reportingTimer is not null) {
			try {
				while (await this._reportingTimer.WaitForNextTickAsync(cancellationToken)) {
					await this._metricsService.RecordQueueDepth(this._channel.Reader.Count).ConfigureAwait(false);
				}
			} catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
				// Normal cancellation, no logging needed
			} catch (Exception ex) {
				// We swallow the error, we're simply logging
				// the queue count as a convenience to the 
				// sys admin/developer
				this._logger.QueueDepthError(ex);
			}
		}
	}

	/// <summary>
	/// Releases resources used by the DefaultBatchProcessor.
	/// </summary>
	/// <remarks>
	/// Gracefully shuts down background tasks, waiting for in-flight operations to complete
	/// with a timeout to avoid indefinite blocking during application shutdown.
	/// </remarks>
	public void Dispose() {
		if (this._disposed) {
			return;
		}

		this._disposed = true;

		try {
			// Use the same shutdown logic
			this.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
		} catch (Exception ex) {
			// Log but don't rethrow from Dispose
			this._logger.DisposalError(ex);
		} finally {
			// Dispose resources
			this._messagePrioritizer.Dispose();
			this._reportingTimer?.Dispose();
			this._cts?.Dispose();
		}
	}
}