namespace Cirreum.Runtime.Messaging.Metrics;

using Cirreum.Conductor;
using Cirreum.Diagnostics;
using Cirreum.Messaging;
using Cirreum.Messaging.Metrics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Default implementation of <see cref="IMessagingMetricsService"/> that collects metrics
/// and reports them to both logs and a metrics system.
/// </summary>
public class DefaultMessagingMetricsService : IMessagingMetricsService {

	private static readonly TimeSpan _criticalAlertThrottleInterval = TimeSpan.FromSeconds(30);
	private static readonly TimeSpan _warningAlertThrottleInterval = TimeSpan.FromMinutes(2); // Longer interval for warnings
	private static readonly TimeSpan _normalAlertThrottleInterval = TimeSpan.FromMinutes(5);
	private DateTime _lastCriticalAlertTime = DateTime.MinValue;
	private DateTime _lastWarningAlertTime = DateTime.MinValue;
	private DateTime _lastNormalAlertTime = DateTime.MinValue;
	private readonly ILogger<DefaultMessagingMetricsService> _logger;
	private readonly MetricsOptions _options;
	private readonly Meter _meter;
	private readonly PeriodicTimer? _reportingTimer;
	private readonly CancellationTokenSource? _cts;
	private readonly Task? _reportingTask;
	private readonly IConductor _conductor;

	// Metric name constants. Instruments live under cirreum.messaging.* — the bare messaging.*
	// namespace belongs to the OpenTelemetry messaging semantic conventions.
	private const string MESSAGES_RECEIVED = "cirreum.messaging.messages.received";
	private const string MESSAGES_RECEIVED_DESC = "Number of messages received for delivery";

	private const string MESSAGES_QUEUED = "cirreum.messaging.messages.queued";
	private const string MESSAGES_QUEUED_DESC = "Number of messages queued for delivery";

	private const string MESSAGES_QUEUE_TIME = "cirreum.messaging.enqueue.duration";
	private const string MESSAGES_QUEUE_TIME_DESC = "Time taken to queue a message";

	private const string MESSAGES_QUEUE_WAIT_TIME = "cirreum.messaging.queue_wait.duration";
	private const string MESSAGES_QUEUE_WAIT_TIME_DESC = "Time a message waits in the queue to be processed";

	private const string MESSAGES_DELIVERED = "cirreum.messaging.messages.delivered";
	private const string MESSAGES_DELIVERED_DESC = "Number of messages successfully delivered";

	private const string MESSAGES_FAILED = "cirreum.messaging.messages.failed";
	private const string MESSAGES_FAILED_DESC = "Number of messages failed to be delivered";

	private const string MESSAGE_PUBLISH_TIME = "cirreum.messaging.publish.duration";
	private const string MESSAGE_PUBLISH_TIME_DESC = "Time taken to publish a message to the external broker";

	private const string BATCHES_PARTIAL_TOTAL = "cirreum.messaging.batches.partial";
	private const string BATCHES_PARTIAL_TOTAL_DESC = "Number of partial batches";

	private const string BATCHES_PARTIAL_SIZE = "cirreum.messaging.batches.partial.size";
	private const string BATCHES_PARTIAL_SIZE_DESC = "Size of partial batches";

	private const string BATCHES_PARTIAL_CAPACITY_PERCENTAGE = "cirreum.messaging.batches.partial.percentage";
	private const string BATCHES_PARTIAL_CAPACITY_PERCENTAGE_DESC = "Percent filled of partial batches";

	private const string BATCH_FULLNESS = "cirreum.messaging.batch.fullness";
	private const string BATCH_FULLNESS_DESC = "Percentage of batch fullness";

	private const string BATCHES_PROCESSED = "cirreum.messaging.batches.processed";
	private const string BATCHES_PROCESSED_DESC = "Number of batches processed";

	private const string BATCH_PROCESSING_TIME = "cirreum.messaging.batch.processing.duration";
	private const string BATCH_PROCESSING_TIME_DESC = "Time taken to process a batch of messages";

	private const string MESSAGES_QUEUE_DEPTH = "cirreum.messaging.queue.depth";
	private const string MESSAGES_QUEUE_DEPTH_DESC = "Current depth of the pending messages queue";

	// Units (UCUM; curly-brace annotations name what is counted)
	private const string UNIT_MESSAGE = "{message}";
	private const string UNIT_BATCH = "{batch}";
	private const string UNIT_PERCENT = "%";

	// Tag keys
	private const string TAG_TARGET = "target";
	private const string TAG_PRIORITY = "priority";
	private const string TAG_DELIVERY_MODE = "delivery_mode";
	private const string TAG_METRIC_TYPE = "metric_type";
	private const string TAG_ERROR_TYPE = "error.type";
	private const string TAG_BATCH_STATE = "batch_state";

	// Counters
	private readonly Counter<long> _messagesReceivedCounter;
	private readonly Counter<long> _messagesQueuedCounter;
	private readonly Histogram<double> _messagesQueueTimeHistogram;
	private readonly Histogram<double> _messagesQueueWaitTimeHistogram;
	private readonly Counter<long> _messagesDeliveredCounter;
	private readonly Counter<long> _messagesFailedCounter;
	private readonly Histogram<double> _messagePublishTimeHistogram;
	private readonly Histogram<double> _batchFullnessHistogram;
	private readonly Counter<long> _partialBatchCounter;
	private readonly Histogram<double> _partialBatchSizeHistogram;
	private readonly Histogram<double> _partialBatchCapacityPercentageHistogram;
	private readonly Counter<long> _batchesProcessedCounter;
	private readonly Histogram<double> _batchProcessingTimeHistogram;
	private readonly Gauge<int> _queueDepthGauge;

	// In-memory metrics storage for periodic reporting
	private readonly ConcurrentDictionary<string, long> _messageTypeCounters = new();
	private readonly ConcurrentDictionary<string, List<long>> _processingTimes = new();
	private int _currentQueueDepth;
	private long _totalMessagesQueued;
	private long _totalMessagesDelivered;
	private long _totalMessagesFailed;

	/// <summary>
	/// Constructor
	/// </summary>
	/// <param name="logger"></param>
	/// <param name="options"></param>
	/// <param name="conductor"></param>
	public DefaultMessagingMetricsService(
		ILogger<DefaultMessagingMetricsService> logger,
		IOptions<MetricsOptions> options,
		IConductor conductor) {

		this._logger = logger;
		this._options = options.Value;
		this._conductor = conductor;

		// Initialize OpenTelemetry meter
		var version = this.GetType().Assembly.GetName()?.Version?.ToString(3) ?? "1.0.0";
		this._meter = new Meter("Cirreum.Messaging", version);

		// Initialize counters and histograms
		this._messagesReceivedCounter = this._meter.CreateCounter<long>(MESSAGES_RECEIVED,
			unit: UNIT_MESSAGE,
			description: MESSAGES_RECEIVED_DESC);

		this._messagesQueuedCounter = this._meter.CreateCounter<long>(MESSAGES_QUEUED,
			unit: UNIT_MESSAGE,
			description: MESSAGES_QUEUED_DESC);

		this._messagesQueueTimeHistogram = this.CreateDurationHistogram(MESSAGES_QUEUE_TIME, MESSAGES_QUEUE_TIME_DESC);

		this._messagesQueueWaitTimeHistogram = this.CreateDurationHistogram(MESSAGES_QUEUE_WAIT_TIME, MESSAGES_QUEUE_WAIT_TIME_DESC);

		this._messagesDeliveredCounter = this._meter.CreateCounter<long>(MESSAGES_DELIVERED,
			unit: UNIT_MESSAGE,
			description: MESSAGES_DELIVERED_DESC);

		this._messagesFailedCounter = this._meter.CreateCounter<long>(MESSAGES_FAILED,
			unit: UNIT_MESSAGE,
			description: MESSAGES_FAILED_DESC);

		this._messagePublishTimeHistogram = this.CreateDurationHistogram(MESSAGE_PUBLISH_TIME, MESSAGE_PUBLISH_TIME_DESC);

		this._partialBatchCounter = this._meter.CreateCounter<long>(BATCHES_PARTIAL_TOTAL,
			unit: UNIT_BATCH,
			description: BATCHES_PARTIAL_TOTAL_DESC);

		this._partialBatchSizeHistogram = this._meter.CreateHistogram<double>(BATCHES_PARTIAL_SIZE,
			unit: UNIT_MESSAGE,
			description: BATCHES_PARTIAL_SIZE_DESC);

		this._partialBatchCapacityPercentageHistogram = this._meter.CreateHistogram<double>(BATCHES_PARTIAL_CAPACITY_PERCENTAGE,
			unit: UNIT_PERCENT,
			description: BATCHES_PARTIAL_CAPACITY_PERCENTAGE_DESC);

		this._batchFullnessHistogram = this._meter.CreateHistogram<double>(BATCH_FULLNESS,
			unit: UNIT_PERCENT,
			description: BATCH_FULLNESS_DESC);

		this._batchesProcessedCounter = this._meter.CreateCounter<long>(BATCHES_PROCESSED,
			unit: UNIT_BATCH,
			description: BATCHES_PROCESSED_DESC);

		this._batchProcessingTimeHistogram = this.CreateDurationHistogram(BATCH_PROCESSING_TIME, BATCH_PROCESSING_TIME_DESC);

		this._queueDepthGauge = this._meter.CreateGauge<int>(MESSAGES_QUEUE_DEPTH,
			unit: UNIT_MESSAGE,
			description: MESSAGES_QUEUE_DEPTH_DESC);

		// Setup periodic reporting
		if (this._options.EnablePeriodicReporting) {
			this._cts = new CancellationTokenSource();
			this._reportingTimer = new PeriodicTimer(this._options.ReportingInterval);
			this._reportingTask = Task.Run(() => this.ReportMetricsPeriodically(this._cts.Token));
		}
	}

	private Histogram<double> CreateDurationHistogram(string name, string description) =>
		this._meter.CreateHistogram<double>(
			name,
			unit: CirreumTelemetry.Histograms.DurationUnit,
			description: description,
			tags: null,
			advice: CirreumTelemetry.Histograms.DurationAdvice);

	// The recording methods take milliseconds; duration histograms record seconds.
	private static double ToSeconds(long milliseconds) => milliseconds / 1000.0;

	public void RecordMessageReceived(string messageType, MessageTarget target) {
		var key = $"{messageType}:{target}";
		this._messageTypeCounters.AddOrUpdate(key, 1, (_, count) => count + 1);

		this._messagesReceivedCounter.Add(1,
			new KeyValuePair<string, object?>(TAG_TARGET, target.ToString()));
	}

	public void RecordMessageDelivered(string messageType, MessageTarget target, long processingTimeMs) {
		Interlocked.Increment(ref this._totalMessagesDelivered);

		// Record processing time
		this._processingTimes.AddOrUpdate($"{messageType}:{target}",
			[processingTimeMs],
			(_, times) => {
				times.Add(processingTimeMs);
				return times;
			});

		this._messagesDeliveredCounter.Add(1,
			new KeyValuePair<string, object?>(TAG_TARGET, target.ToString()),
			new KeyValuePair<string, object?>(TAG_DELIVERY_MODE, "individual"));

		this._messagePublishTimeHistogram.Record(ToSeconds(processingTimeMs),
			new KeyValuePair<string, object?>(TAG_TARGET, target.ToString()),
			new KeyValuePair<string, object?>(TAG_METRIC_TYPE, "individual"));

		if (this._options.LogDetailedMetrics) {
			this._logger.LogMessageDelivered(target, messageType, processingTimeMs);
		}
	}

	public void RecordMessageFailed(string messageType, MessageTarget target, string errorType, long processingTimeMs) {
		Interlocked.Increment(ref this._totalMessagesFailed);

		this._messagesFailedCounter.Add(1,
			new KeyValuePair<string, object?>(TAG_TARGET, target.ToString()),
			new KeyValuePair<string, object?>(TAG_ERROR_TYPE, errorType));

		this._messagePublishTimeHistogram.Record(ToSeconds(processingTimeMs),
			new KeyValuePair<string, object?>(TAG_TARGET, target.ToString()),
			new KeyValuePair<string, object?>(TAG_ERROR_TYPE, errorType));

		// Always log failures
		this._logger.LogMessageFailed(target, messageType, errorType);
	}

	public void RecordMessageQueued(string messageType, MessageTarget target, long queueTimeMs, DistributedMessagePriority priority) {
		Interlocked.Increment(ref this._totalMessagesQueued);

		this._messagesQueuedCounter.Add(1,
			new KeyValuePair<string, object?>(TAG_TARGET, target.ToString()),
			new KeyValuePair<string, object?>(TAG_PRIORITY, priority.ToString()));

		this._messagesQueueTimeHistogram.Record(ToSeconds(queueTimeMs),
			new KeyValuePair<string, object?>(TAG_TARGET, target.ToString()),
			new KeyValuePair<string, object?>(TAG_PRIORITY, priority.ToString()));

		if (this._options.LogDetailedMetrics) {
			this._logger.LogMessageQueued(target, messageType);
		}
	}

	public void RecordMessageDequeued(string messageType, MessageTarget target, long queueWaitTimeMs, DistributedMessagePriority priority) {
		this._messagesQueueWaitTimeHistogram.Record(ToSeconds(queueWaitTimeMs),
			new KeyValuePair<string, object?>(TAG_TARGET, target.ToString()),
			new KeyValuePair<string, object?>(TAG_PRIORITY, priority.ToString()));

		if (this._options.LogDetailedMetrics) {
			this._logger.LogMessageDequeued(target, messageType, queueWaitTimeMs);
		}
	}

	public void RecordMessageDeliveredInBatch(string messageType, MessageTarget target, long processingTimeMs, long totalTimeMs) {
		Interlocked.Increment(ref this._totalMessagesDelivered);

		// Record the individual metrics with batch context
		this._messagesDeliveredCounter.Add(1,
			new KeyValuePair<string, object?>(TAG_TARGET, target.ToString()),
			new KeyValuePair<string, object?>(TAG_DELIVERY_MODE, "batch"));

		// Record both times
		this._messagePublishTimeHistogram.Record(
			ToSeconds(processingTimeMs),
			new KeyValuePair<string, object?>(TAG_TARGET, target.ToString()),
			new KeyValuePair<string, object?>(TAG_METRIC_TYPE, "batch"));

		// Store processing times for periodic reporting
		this._processingTimes.AddOrUpdate($"{messageType}:{target}",
			[processingTimeMs],
			(_, times) => {
				times.Add(processingTimeMs);
				return times;
			});

		if (this._options.LogDetailedMetrics) {
			this._logger.LogMessageBatchDelivered(target, messageType, processingTimeMs, totalTimeMs);
		}
	}

	public void RecordPartialBatch(int batchCapacity, int batchSize) {
		var tag = new KeyValuePair<string, object?>(TAG_BATCH_STATE,
			batchCapacity == batchSize ? "full"
			: batchSize == 0 ? "empty"
			: batchCapacity > batchSize ? "partial"
			: "overflow");
		var percentageOfCapacity = ((double)batchSize / batchCapacity) * 100.0;

		this._partialBatchCounter.Add(1, tag);
		this._partialBatchSizeHistogram.Record(batchSize, tag);
		this._partialBatchCapacityPercentageHistogram.Record(percentageOfCapacity, tag);

		if (this._options.LogDetailedMetrics) {
			this._logger.LogPartialBatch(batchCapacity, batchSize, percentageOfCapacity);
		}
	}

	public void RecordBatchProcessed(
		int batchCapacity,
		int batchSize,
		long processingTimeMs,
		int successCount,
		int failureCount,
		int standardCount,
		int timeSensitiveCount,
		int systemCount) {

		// Calculate and Record fullness percentage
		var fullnessPercentage = ((double)batchSize / batchCapacity) * 100.0; // Cast to double for accurate division
		this._batchFullnessHistogram.Record(fullnessPercentage);

		// Count batches processed
		this._batchesProcessedCounter.Add(1);

		// Record batch processing time
		this._batchProcessingTimeHistogram.Record(ToSeconds(processingTimeMs));

		if (this._options.LogDetailedMetrics) {
			this._logger.LogBatchProcessed(batchSize, processingTimeMs, successCount, failureCount);
			this._logger.BatchPriorityDistribution(standardCount, timeSensitiveCount, systemCount);
		}
	}

	public async Task RecordQueueDepth(int queueDepth) {

		// Update the counter by the delta
		Interlocked.Exchange(ref this._currentQueueDepth, queueDepth);
		this._queueDepthGauge.Record(queueDepth);

		// Log Depth
		var currentTime = DateTime.UtcNow;
		if (queueDepth > this._options.QueueDepthCriticalThreshold) {
			// Check if enough time has passed since last critical alert
			if (currentTime - this._lastCriticalAlertTime >= _criticalAlertThrottleInterval) {
				// Use the same messaging infrastructure to send an alert
				var alert = new QueueDepthAlertMessage(queueDepth, this._options.QueueDepthCriticalThreshold);
				await this._conductor.PublishAsync(alert);
				this._logger.LogQueueDepthCritical(queueDepth);
				this._lastCriticalAlertTime = currentTime;
			}
		} else if (queueDepth > this._options.QueueDepthWarningThreshold) {
			// Check if enough time has passed since last warning alert
			if (currentTime - this._lastWarningAlertTime >= _warningAlertThrottleInterval) {
				this._logger.LogQueueDepthHigh(queueDepth);
				this._lastWarningAlertTime = currentTime;
			}
		} else if (this._options.LogDetailedMetrics) {
			if (currentTime - this._lastNormalAlertTime >= _normalAlertThrottleInterval) {
				// Always log normal conditions when detailed metrics are enabled
				this._logger.LogQueueDepthNormal(queueDepth);
				this._lastNormalAlertTime = currentTime;
			}
		}
	}

	private async Task ReportMetricsPeriodically(CancellationToken cancellationToken) {
		if (this._reportingTimer is not null) {
			try {
				while (await this._reportingTimer.WaitForNextTickAsync(cancellationToken)) {
					this.LogPeriodMetrics();
				}
			} catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
				// Normal cancellation, no logging needed
			} catch (Exception ex) {
				this._logger.LogMetricsError(ex);
			}
		}
	}

	private void LogPeriodMetrics() {
		this._logger.LogPeriodMetrics(
			this._currentQueueDepth,
			this._totalMessagesQueued,
			this._totalMessagesDelivered,
			this._totalMessagesFailed);

		if (this._options.IncludeDetailedReporting) {
			// Report per-message-type statistics
			foreach (var entry in this._messageTypeCounters) {
				var parts = entry.Key.Split(':');
				var messageType = parts[0];
				var target = parts[1];

				this._logger.LogMessageTypeMetrics(target, messageType, entry.Value);

				// Try to get processing times for this message type
				if (this._processingTimes.TryGetValue(entry.Key, out var times) && times.Count > 0) {
					var avgTime = times.Average();
					var maxTime = times.Max();

					this._logger.LogProcessingTimes(messageType, target, avgTime, maxTime);

					// Clear the list after reporting to avoid memory growth
					times.Clear();
				}
			}
		}
	}

	public void Dispose() {
		GC.SuppressFinalize(this);
		if (this._options.EnablePeriodicReporting) {
			this._cts?.Cancel();
			this._reportingTask?.Wait(TimeSpan.FromSeconds(1));
			this._cts?.Dispose();
			this._reportingTimer?.Dispose();
		}
		this._meter?.Dispose();
	}

}