namespace Cirreum.Runtime.Messaging.Metrics;

/// <summary>
/// Configuration options for metrics collection and reporting.
/// </summary>
public class MetricsOptions {

	/// <summary>
	/// The key name of the section.
	/// </summary>
	public const string ConfigurationName = "Metrics";

	/// <summary>
	/// Gets or sets whether to enable periodic reporting of metrics.
	/// </summary>
	public bool EnablePeriodicReporting { get; set; } = true;

	/// <summary>
	/// Gets or sets the interval for periodic metrics reporting.
	/// </summary>
	public TimeSpan ReportingInterval { get; set; } = TimeSpan.FromMinutes(5);

	/// <summary>
	/// Gets or sets whether to include detailed metrics in periodic reports.
	/// </summary>
	public bool IncludeDetailedReporting { get; set; } = true;

	/// <summary>
	/// Gets or sets whether to log detailed metrics for each operation.
	/// </summary>
	public bool LogDetailedMetrics { get; set; } = false;

	/// <summary>
	/// Gets or sets the queue depth threshold for warnings.
	/// </summary>
	public int QueueDepthWarningThreshold { get; set; } = 500;

	/// <summary>
	/// Gets or sets the queue depth threshold for critical levels.
	/// </summary>
	public int QueueDepthCriticalThreshold { get; set; } = 1000;

}