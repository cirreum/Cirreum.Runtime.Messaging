namespace Cirreum.Runtime.Messaging.Batching;

using Cirreum.Messaging;

/// <summary>
/// Defines the contract for a component that processes outbound messages in batches.
/// </summary>
/// <remarks>
/// Implementations of this interface handle the queueing, prioritization, and batch processing
/// of outbound messages to optimize throughput and resource utilization.
/// </remarks>
public interface IBatchProcessor : IDisposable {
	/// <summary>
	/// Start the background processing.
	/// </summary>
	/// <remarks>
	/// <para>
	/// If the background processor is already started, then simply returns.
	/// </para>
	/// </remarks>
	void Start();
	/// <summary>
	/// Asynchronously submits a message for batch processing.
	/// </summary>
	/// <param name="message">The outbound message to be processed</param>
	/// <param name="target">The target of the message being submitted</param>
	/// <param name="priority">The priority to assign to the message</param>
	/// <param name="token">A cancellation token to observe while waiting for the operation to complete</param>
	/// <returns>A task representing the asynchronous operation and the priority the message was assigned in the queue.</returns>
	/// <remarks>
	/// The message is queued for processing and will be included in a batch based on
	/// its priority and the current batching strategy.
	/// </remarks>
	/// <exception cref="InvalidOperationException">The background processor has not been started.</exception>
	Task<DistributedMessagePriority> SubmitMessageAsync(
		OutboundMessage message,
		MessageTarget target,
		DistributedMessagePriority priority,
		CancellationToken token);
}