namespace Cirreum.Runtime.Messaging.Batching;

using Cirreum.Messaging;

/// <summary>
/// Represents an outbound message item queued for background processing
/// </summary>
/// <remarks>
/// Tracks metadata about queued messages including their original and effective priorities,
/// processing state, and timing information
/// </remarks>
/// <param name="Message">The outbound message to be processed</param>
/// <param name="Target">The target of the message being processed</param>
/// <param name="QueuedTime">The timestamp when the message was originally queued</param>
/// <param name="QueuedPriority">The original priority assigned to the message when queued</param>
internal record BatchItem(
		OutboundMessage Message,
		MessageTarget Target,
		DateTime QueuedTime,
		DistributedMessagePriority QueuedPriority) {
	/// <summary>
	/// Indicates whether the message has been successfully processed
	/// </summary>
	public bool IsCompleted { get; set; }

	/// <summary>
	/// The current priority of the message, which may differ from the original queued priority
	/// based on processing rules or dynamic priority adjustments
	/// </summary>
	public DistributedMessagePriority EffectivePriority { get; set; }
}