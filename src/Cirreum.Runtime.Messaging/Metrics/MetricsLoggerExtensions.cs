namespace Cirreum.Runtime.Messaging.Metrics;

using Cirreum.Messaging;
using Microsoft.Extensions.Logging;

/// <summary>
/// Contains source-generated extension methods for logging messaging metrics.
/// </summary>
internal static partial class MetricsLoggerExtensions {

	// Message constants from DefaultMessagingMetricsService
	private const string LOG_MESSAGE_QUEUED = "Queued: [{Kind}] {MessageType}";
	private const string LOG_MESSAGE_DEQUEUED = "Dequeued: [{Kind}] {MessageType} after waiting in queue for {queueWaitTimeMs}ms";
	private const string LOG_MESSAGE_DELIVERED = "Delivered: [{Kind}] {MessageType} in {ProcessingTimeMs}ms";
	private const string LOG_MESSAGE_BATCH_DELIVERED = "Batch Delivered: [{Kind}] {MessageType} in {ProcessingTimeMs}ms, Total end-to-end time (queued to delivered): {TotalTimeMs}ms";
	private const string LOG_MESSAGE_FAILED = "Failed: [{Kind}] {MessageType}, Error: {ErrorType}";
	private const string LOG_PARTIAL_BATCH = "Partial Batch: Capacity: {Capacity}, Size: {Size}, Percentage of Capacity: {Percentage}";
	private const string LOG_BATCH_PROCESSED = "Batch Processed: {BatchSize} messages in {ProcessingTimeMs}ms with {SuccessCount} successful message and {FailureCount} failed messages.";
	private const string LOG_QUEUE_DEPTH_CRITICAL = "Queue depth is critical: {QueueDepth}";
	private const string LOG_QUEUE_DEPTH_HIGH = "Queue depth is high: {QueueDepth}";
	private const string LOG_QUEUE_DEPTH_NORMAL = "Current queue depth: {QueueDepth}";
	private const string LOG_PERIOD_METRICS = "Delivery metrics: Queue-Depth: {QueueDepth}, Queued-Messages: {Queued}, Messages-Delivered: {Delivered}, Messages-Failed: {Failed}";
	private const string LOG_MESSAGE_TYPE_METRICS = "Type Metrics: [{Kind}] {MessageType}, Count: {Count}";
	private const string LOG_PROCESSING_TIMES = "Processing time: {MessageType} [{Kind}], Avg: {AvgTime:0.##}ms, Max: {MaxTime:0.##}ms";
	private const string LOG_METRICS_ERROR = "Error in metrics reporting task";

	// LogMessageQueued - Debug
	[LoggerMessage(
		EventId = 1,
		Level = LogLevel.Debug,
		Message = LOG_MESSAGE_QUEUED)]
	public static partial void LogMessageQueued(
		this ILogger logger,
		MessageTarget kind,
		string messageType);

	// LogMessageDequeued - Debug
	[LoggerMessage(
		EventId = 2,
		Level = LogLevel.Debug,
		Message = LOG_MESSAGE_DEQUEUED)]
	public static partial void LogMessageDequeued(
		this ILogger logger,
		MessageTarget kind,
		string messageType,
		long queueWaitTimeMs);

	// LogMessageDelivered - Debug
	[LoggerMessage(
		EventId = 3,
		Level = LogLevel.Debug,
		Message = LOG_MESSAGE_DELIVERED)]
	public static partial void LogMessageDelivered(
		this ILogger logger,
		MessageTarget kind,
		string messageType,
		long processingTimeMs);

	// LogMessageBatchDelivered - Debug
	[LoggerMessage(
		EventId = 4,
		Level = LogLevel.Debug,
		Message = LOG_MESSAGE_BATCH_DELIVERED)]
	public static partial void LogMessageBatchDelivered(
		this ILogger logger,
		MessageTarget kind,
		string messageType,
		long processingTimeMs,
		long totalTimeMs);

	// LogMessageFailed - Warning
	[LoggerMessage(
		EventId = 5,
		Level = LogLevel.Warning,
		Message = LOG_MESSAGE_FAILED)]
	public static partial void LogMessageFailed(
		this ILogger logger,
		MessageTarget kind,
		string messageType,
		string errorType);

	// LogPartialBatch - Debug
	[LoggerMessage(
		EventId = 6,
		Level = LogLevel.Debug,
		Message = LOG_PARTIAL_BATCH)]
	public static partial void LogPartialBatch(
		this ILogger logger,
		int capacity,
		int size,
		double percentage);

	// LogBatchProcessed - Information
	[LoggerMessage(
		EventId = 7,
		Level = LogLevel.Information,
		Message = LOG_BATCH_PROCESSED)]
	public static partial void LogBatchProcessed(
		this ILogger logger,
		int batchSize,
		long processingTimeMs,
		int successCount,
		int failureCount);

	// LogQueueDepthCritical - Warning
	[LoggerMessage(
		EventId = 8,
		Level = LogLevel.Warning,
		Message = LOG_QUEUE_DEPTH_CRITICAL)]
	public static partial void LogQueueDepthCritical(
		this ILogger logger,
		int queueDepth);

	// LogQueueDepthHigh - Warning
	[LoggerMessage(
		EventId = 9,
		Level = LogLevel.Warning,
		Message = LOG_QUEUE_DEPTH_HIGH)]
	public static partial void LogQueueDepthHigh(
		this ILogger logger,
		int queueDepth);

	// LogQueueDepthNormal - Debug
	[LoggerMessage(
		EventId = 10,
		Level = LogLevel.Debug,
		Message = LOG_QUEUE_DEPTH_NORMAL)]
	public static partial void LogQueueDepthNormal(
		this ILogger logger,
		int queueDepth);

	// LogPeriodMetrics - Information
	[LoggerMessage(
		EventId = 11,
		Level = LogLevel.Information,
		Message = LOG_PERIOD_METRICS)]
	public static partial void LogPeriodMetrics(
		this ILogger logger,
		int queueDepth,
		long queued,
		long delivered,
		long failed);

	// LogMessageTypeMetrics - Information
	[LoggerMessage(
		EventId = 12,
		Level = LogLevel.Information,
		Message = LOG_MESSAGE_TYPE_METRICS)]
	public static partial void LogMessageTypeMetrics(
		this ILogger logger,
		string kind,
		string messageType,
		long count);

	// LogProcessingTimes - Information
	[LoggerMessage(
		EventId = 13,
		Level = LogLevel.Information,
		Message = LOG_PROCESSING_TIMES)]
	public static partial void LogProcessingTimes(
		this ILogger logger,
		string messageType,
		string kind,
		double avgTime,
		double maxTime);

	// LogMetricsError - Error
	[LoggerMessage(
		EventId = 14,
		Level = LogLevel.Error,
		Message = LOG_METRICS_ERROR)]
	public static partial void LogMetricsError(
		this ILogger logger,
		Exception exception);

	// BatchPriorityDistribution - Information
	[LoggerMessage(
		EventId = 15,
		Level = LogLevel.Information,
		Message = "Batch Priority Distribution: Standard: {StandardCount}, TimeSensitive: {TimeSensitiveCount}, System: {SystemCount}")]
	public static partial void BatchPriorityDistribution(
		this ILogger logger,
		int standardCount,
		int timeSensitiveCount,
		int systemCount);
}