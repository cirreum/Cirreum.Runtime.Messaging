namespace Cirreum.Runtime.Messaging;

/// <summary>
/// Provides centralized string constants for the distributed messaging system.
/// </summary>
/// <remarks>
/// Centralizing string constants improves maintainability and reduces the risk of
/// typographical errors when referencing event names, namespaces, and other
/// string literals throughout the codebase.
/// </remarks>
internal static class DistributeMessagingStrings {

	/// <summary>
	/// The namespace used for messaging telemetry and distributed tracing.
	/// </summary>
	public const string MessagingNamespace = "Cirreum.Messaging";

	/// <summary>
	/// Configuration key for messaging settings in configuration providers.
	/// </summary>
	public const string ConfigurationKey = "Cirreum:Messaging";

	// Event names
	/// <summary>
	/// Event name for when a message is sent directly without batching.
	/// </summary>
	public const string Event_MessageSentDirectly = "MessageSentDirectly";

	/// <summary>
	/// Event name for when a message is queued for later delivery by the batch processor.
	/// </summary>
	public const string Event_MessageQueueForDelivery = "MessageQueueForDelivery";

	// Activity names
	/// <summary>
	/// Activity name for the publish message operation.
	/// </summary>
	public const string Activity_PublishMessageAsync = "PublishMessageAsync";

	// Tag names for distributed tracing
	/// <summary>
	/// Tag name for the message subject in activity events.
	/// </summary>
	public const string Tag_Subject = "subject";

	/// <summary>
	/// Tag name for the message priority in activity events.
	/// </summary>
	public const string Tag_MessagePriority = "message_priority";

	/// <summary>
	/// Tag name for the queued priority in activity events.
	/// </summary>
	public const string Tag_QueuedPriority = "queued_priority";

	// Exception messages
	/// <summary>
	/// Error message for when the instance key is not provided in configuration.
	/// </summary>
	public const string Error_InstanceKeyRequired = "Messaging client instance key must be specified";
}