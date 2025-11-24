namespace Cirreum.Runtime.Messaging;

using Cirreum.Messaging;
using Microsoft.Extensions.Logging;
using System;

/// <summary>
/// Logging class for DefaultTransportPublisher using LoggerMessage source generation
/// </summary>
internal static partial class MessagingLoggerExtensions {

	[LoggerMessage(
		EventId = 1001,
		Level = LogLevel.Information,
		Message = "Publisher {publisher}: initialized with a background delivery queue capacity of: {capacity}")]
	public static partial void PublisherInitialized(this ILogger logger, string publisher, int capacity);

	[LoggerMessage(
		EventId = 1002,
		Level = LogLevel.Information,
		Message = "Publisher {publisher}: ActiveTimeBatchingProfile: {activeProfile}")]
	public static partial void ActiveTimeBatchingProfile(this ILogger logger, string publisher, string activeProfile);

	[LoggerMessage(
		EventId = 1003,
		Level = LogLevel.Information,
		Message = "Publisher {publisher}: ActiveTimeBatchingProfile Profile: {profile}")]
	public static partial void ActiveTimeBatchingProfileDetails(this ILogger logger, string publisher, string profile);

	[LoggerMessage(
		EventId = 1004,
		Level = LogLevel.Information,
		Message = "Publisher {publisher}: Background processor started with BatchCapacity: {batchCapacity} and BatchFillWaitTime: {batchFillWaitTime}")]
	public static partial void BackgroundProcessorStarted(this ILogger logger, string publisher, int batchCapacity, TimeSpan batchFillWaitTime);

	[LoggerMessage(
		EventId = 1005,
		Level = LogLevel.Information,
		Message = "Background message processor stopped due to cancellation")]
	public static partial void ProcessorCancelled(this ILogger logger);

	[LoggerMessage(
		EventId = 1006,
		Level = LogLevel.Error,
		Message = "Error in background message processor while processing {count} items")]
	public static partial void BackgroundProcessorError(this ILogger logger, Exception ex, int count);

	[LoggerMessage(
		EventId = 1007,
		Level = LogLevel.Debug,
		Message = "Dynamic batch fill waitTime: {fillWaitTime} and capacity: {capacity}")]
	public static partial void DynamicBatchParameters(this ILogger logger, TimeSpan fillWaitTime, int capacity);

	[LoggerMessage(
		EventId = 1008,
		Level = LogLevel.Debug,
		Message = "Processing batch of {count} messages")]
	public static partial void ProcessingBatch(this ILogger logger, int count);

	[LoggerMessage(
		EventId = 1009,
		Level = LogLevel.Error,
		Message = "Error processing batch of messages")]
	public static partial void BatchProcessingError(this ILogger logger, Exception ex);

	[LoggerMessage(
		EventId = 1010,
		Level = LogLevel.Error,
		Message = "Error with reporting queue depth task.")]
	public static partial void QueueDepthError(this ILogger logger, Exception ex);

	[LoggerMessage(
		EventId = 1011,
		Level = LogLevel.Information,
		Message = "Stopping background message processor")]
	public static partial void StoppingBackgroundProcessor(this ILogger logger);

	[LoggerMessage(
		EventId = 1012,
		Level = LogLevel.Information,
		Message = "Shutting down background message processor")]
	public static partial void ShuttingDown(this ILogger logger);

	[LoggerMessage(
		EventId = 1013,
		Level = LogLevel.Warning,
		Message = "Background processor did not shut down cleanly within timeout")]
	public static partial void ShutdownTimeout(this ILogger logger);

	[LoggerMessage(
		EventId = 1014,
		Level = LogLevel.Warning,
		Message = "Background reporting did not shut down cleanly within timeout")]
	public static partial void ReportingShutdownTimeout(this ILogger logger);

	[LoggerMessage(
		EventId = 1015,
		Level = LogLevel.Error,
		Message = "Error during shutdown of background message processor")]
	public static partial void ShutdownError(this ILogger logger, Exception ex);

	[LoggerMessage(
		EventId = 1016,
		Level = LogLevel.Error,
		Message = "Error during disposal trying to stop the processor.")]
	public static partial void DisposalError(this ILogger logger, Exception ex);


	//
	// Queue Priority
	//

	[LoggerMessage(
		EventId = 6001,
		Level = LogLevel.Warning,
		Message = "High priority message rate limit exceeded for {MessageType}. Rate limit: {RateLimit} per minute. Message priority downgraded to Standard.")]
	public static partial void HighPriorityRateLimitExceeded(
		this ILogger logger,
		string MessageType,
		int RateLimit);

	[LoggerMessage(
		EventId = 6002,
		Level = LogLevel.Error,
		Message = "Error resetting high priority counter.")]
	public static partial void HighPriorityResetError(
		this ILogger logger,
		Exception ex);

	[LoggerMessage(
		EventId = 6003,
		Level = LogLevel.Warning,
		Message = "Priority reset task shutdown timed out.")]
	public static partial void PriorityResetShutdownTimeout(
		this ILogger logger);

	[LoggerMessage(
		EventId = 6004,
		Level = LogLevel.Debug,
		Message = "Priority promotion: {Subject} from {OriginalPriority} to {EffectivePriority} after {WaitTimeSeconds}s.")]
	public static partial void MessagePriorityPromoted(
		this ILogger logger,
		string Subject,
		DistributedMessagePriority OriginalPriority,
		DistributedMessagePriority EffectivePriority,
		double WaitTimeSeconds);


	//
	// Circuit Breaker
	//

	[LoggerMessage(
		EventId = 7001,
		Level = LogLevel.Warning,
		Message = "Circuit breaker for {Target} opened after {FailureCount} consecutive failures. Last error: {ErrorType}. Will try again after {ResetTimeout}")]
	public static partial void CircuitBreakerOpened(
			this ILogger logger,
			MessageTarget target,
			int failureCount,
			string errorType,
			TimeSpan resetTimeout);

	[LoggerMessage(
		EventId = 7002,
		Level = LogLevel.Information,
		Message = "Circuit breaker for {MessageTarget} reset after timeout")]
	public static partial void CircuitBreakerReset(
		this ILogger logger,
		MessageTarget messageTarget);

	[LoggerMessage(
		EventId = 7003,
		Level = LogLevel.Information,
		Message = "Circuit breaker processing reset after timeout")]
	public static partial void CircuitBreakerProcessingReset(
		this ILogger logger);

	[LoggerMessage(
		EventId = 7004,
		Level = LogLevel.Debug,
		Message = "Circuit breaker still open. Skipping batch processing for {RemainingTime}")]
	public static partial void CircuitStillOpen(
		this ILogger logger,
		TimeSpan remainingTime);

}