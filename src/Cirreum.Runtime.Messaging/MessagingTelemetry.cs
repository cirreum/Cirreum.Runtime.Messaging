namespace Cirreum.Runtime.Messaging;

using System.Diagnostics;

/// <summary>
/// The distributed-tracing source for the messaging channel, shared by every component that
/// emits spans under <see cref="DistributeMessagingStrings.MessagingNamespace"/>.
/// </summary>
/// <remarks>
/// <para>
/// Static and process-lifetime by design, matching every other telemetry surface in the
/// framework. An <see cref="System.Diagnostics.ActivitySource"/> is a shared listener registration,
/// not a per-instance resource: creating one per component multiplies registrations for a single
/// logical source, and disposing one from a component's <c>Dispose</c> silently ends tracing for
/// every other component using the same name — including any instance created afterwards.
/// Consumers therefore never dispose this.
/// </para>
/// <para>
/// The version matches the meter in <c>DefaultMessagingMetricsService</c>: this package's own
/// assembly version, since the version on a source identifies the instrumenting library.
/// </para>
/// </remarks>
internal static class MessagingTelemetry {

	private static readonly string Version =
		typeof(MessagingTelemetry).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

	/// <summary>
	/// The messaging channel's <see cref="System.Diagnostics.ActivitySource"/>. Start spans with
	/// <c>StartActivity(...)</c> — never <c>CreateActivity(...)</c> followed by <c>Start()</c>,
	/// which allocates an <see cref="Activity"/> even when no listener is attached.
	/// </summary>
	internal static readonly ActivitySource ActivitySource =
		new(DistributeMessagingStrings.MessagingNamespace, Version);

}
