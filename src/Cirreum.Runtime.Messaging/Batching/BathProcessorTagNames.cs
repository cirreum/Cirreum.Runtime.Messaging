namespace Cirreum.Runtime.Messaging.Batching;

internal static class BathProcessorTagNames {

	// Base prefixes
	private const string BatchBase = "batch";
	private const string ProcessingBase = BatchBase + ".processing";

	// Processing tags
	public const string BatchDuration = $"{ProcessingBase}.{Metrics.Duration}";
	public const string BatchErrorType = $"{ProcessingBase}.{Metrics.ErrorType}";
	public const string BatchErrorMessage = $"{ProcessingBase}.{Metrics.ErrorMessage}";
	public const string BatchCancelled = $"{ProcessingBase}.cancelled";
	public const string BatchCapacity = $"{ProcessingBase}.batch_capacity";
	public const string BatchSize = $"{ProcessingBase}.batch_size";
	public const string BatchDeliveredCount = $"{ProcessingBase}.batch_delivered_count";
	public const string BatchStandardCount = $"{ProcessingBase}.batch_standard_count";
	public const string BatchTimeSensitiveCount = $"{ProcessingBase}.batch_time_senstive_count";
	public const string BatchSystemCount = $"{ProcessingBase}.batch_system_count";
	public const string BatchFailedCount = $"{ProcessingBase}.batch_failed_count";
	public const string BatchTargetCount = $"{ProcessingBase}.batch_target_count";
	public const string BatchTarget = $"{ProcessingBase}.batch_target";

	// Dynamic tag generation
	public static string ForTarget(string target, string metric) => $"{BatchBase}.{target.ToLowerInvariant()}.{metric}";

	// Common kind-specific metrics
	public static class Metrics {
		public const string Duration = "duration_ms";
		public const string SuccessCount = "success_count";
		public const string FailureCount = "failure_count";
		public const string ErrorType = "error_type";
		public const string ErrorMessage = "error_message";
	}

}