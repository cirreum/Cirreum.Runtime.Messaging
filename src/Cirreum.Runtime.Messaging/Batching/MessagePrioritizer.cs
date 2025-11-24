namespace Cirreum.Runtime.Messaging.Batching;

using Cirreum.Messaging;
using Microsoft.Extensions.Logging;

/// <summary>
/// Manages message prioritization with rate limiting and age-based priority promotion.
/// </summary>
/// <remarks>
/// Handles dynamic message prioritization by:
/// 1. Enforcing rate limits on high-priority messages
/// 2. Promoting message priorities based on wait time
/// 3. Periodically resetting high-priority message counters
/// </remarks>
internal class MessagePrioritizer : IDisposable {

	/// <summary>
	/// Task that periodically resets the high-priority message counter
	/// </summary>
	private readonly Task? _priorityResetTask;

	/// <summary>
	/// Timer that triggers the periodic reset of the high-priority message counter
	/// </summary>
	private readonly PeriodicTimer _highPriorityResetTimer;

	/// <summary>
	/// Maximum number of high-priority messages allowed within a reset interval
	/// </summary>
	private readonly int _priorityMessageRateLimit = 100;

	/// <summary>
	/// Time threshold after which a message's priority can be increased based on age
	/// </summary>
	private readonly TimeSpan _priorityAgePromotionThreshold;

	/// <summary>
	/// Logger for recording prioritization events and errors
	/// </summary>
	private readonly ILogger _logger;

	/// <summary>
	/// Cancellation token source for the priority reset task
	/// </summary>
	private readonly CancellationTokenSource? _cts;

	/// <summary>
	/// Counter for tracking the number of high-priority messages processed since the last reset
	/// </summary>
	private int _highPriorityMessageCount = 0;

	/// <summary>
	/// Flag indicating whether the instance has been disposed
	/// </summary>
	private bool _disposed;

	/// <summary>
	/// Initializes a new instance of the MessagePrioritizer class.
	/// </summary>
	/// <param name="logger">Logger for recording prioritization events and errors</param>
	/// <param name="rateLimit">Maximum number of high-priority messages allowed per reset interval</param>
	/// <param name="priorityAgePromotionThreshold">
	/// Number of seconds a message must wait before its priority can be promoted
	/// </param>
	public MessagePrioritizer(ILogger logger, int rateLimit, int priorityAgePromotionThreshold) {

		this._logger = logger;
		this._priorityMessageRateLimit = rateLimit;
		this._priorityAgePromotionThreshold = TimeSpan.FromSeconds(priorityAgePromotionThreshold);
		this._highPriorityResetTimer = new PeriodicTimer(TimeSpan.FromMinutes(1));

		this._cts = new CancellationTokenSource();
		this._priorityResetTask = this.ResetHighPriorityCounterAsync(this._cts.Token);

	}

	/// <summary>
	/// Determines the appropriate queue priority for a message based on its original priority
	/// and the current rate limit status.
	/// </summary>
	/// <param name="priority">The original priority of the message</param>
	/// <param name="messageType">The type of message being prioritized</param>
	/// <returns>
	/// The queue priority to assign, which may be downgraded if the high-priority rate limit 
	/// has been exceeded
	/// </returns>
	public DistributedMessagePriority GetQueuePriority(DistributedMessagePriority priority, string messageType) {

		// Get the message priority
		var queuedPriority = priority;

		// Apply rate limiting for high-priority messages
		if (priority > DistributedMessagePriority.Standard) {

			// Simple way to check for too many priority messages
			// and to downgrade after X number of them have been
			// processed. This value is reset in an evaluation
			// task every minute.
			var highPriorityCount = Interlocked.Increment(ref _highPriorityMessageCount);
			if (highPriorityCount > this._priorityMessageRateLimit) {
				// Downgrade to standard priority if limit exceeded
				queuedPriority = DistributedMessagePriority.Standard;
				this._logger.HighPriorityRateLimitExceeded(
					messageType,
					this._priorityMessageRateLimit);
			}
		}

		return queuedPriority;

	}

	/// <summary>
	/// Applies effective priority to a background item based on its age and original priority.
	/// </summary>
	/// <param name="item">The background item to apply effective priority to</param>
	/// <remarks>
	/// May promote a message's priority if it has been waiting longer than the configured
	/// age promotion threshold.
	/// </remarks>
	public void ApplyEffectivePriority(BatchItem item) {

		item.EffectivePriority = CalculateEffectivePriority(item, this._priorityAgePromotionThreshold);
		if (item.EffectivePriority != item.QueuedPriority) {
			var duration = DateTime.UtcNow.Subtract(item.QueuedTime).TotalSeconds;
			this._logger.MessagePriorityPromoted(
				item.Message.Subject,
				item.QueuedPriority,
				item.EffectivePriority,
				duration);
		}

	}

	/// <summary>
	/// Calculates the effective priority for a message based on its original priority and age.
	/// </summary>
	/// <param name="item">The background item containing the message and its metadata</param>
	/// <param name="agePromotionThreshold">
	/// The time threshold after which a message's priority can be increased
	/// </param>
	/// <returns>
	/// The calculated effective priority, which may be higher than the original priority
	/// if the message has been waiting for a long time
	/// </returns>
	private static DistributedMessagePriority CalculateEffectivePriority(BatchItem item, TimeSpan agePromotionThreshold) {

		if (agePromotionThreshold == TimeSpan.Zero) {
			return item.QueuedPriority; // No age-based promotion
		}

		var waitTime = DateTime.UtcNow - item.QueuedTime;
		var priorityIncrease = (int)(waitTime.TotalSeconds / agePromotionThreshold.TotalSeconds);

		// Cap at the highest priority level
		return (DistributedMessagePriority)Math.Min(
			(int)DistributedMessagePriority.System,
			(int)item.QueuedPriority + priorityIncrease);

	}

	/// <summary>
	/// Asynchronously resets the high-priority message counter at regular intervals.
	/// </summary>
	/// <param name="cancellationToken">Token to monitor for cancellation requests</param>
	/// <returns>A task representing the asynchronous operation</returns>
	private async Task ResetHighPriorityCounterAsync(CancellationToken cancellationToken) {
		try {
			while (await _highPriorityResetTimer.WaitForNextTickAsync(cancellationToken)) {
				Interlocked.Exchange(ref _highPriorityMessageCount, 0);
			}
		} catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
			// Normal shutdown, no need to log
		} catch (Exception ex) {
			this._logger.HighPriorityResetError(ex);
		}
	}

	/// <summary>
	/// Releases resources used by the MessagePrioritizer instance.
	/// </summary>
	/// <remarks>
	/// Cancels the priority reset task and waits for it to complete before disposing
	/// the timer and cancellation token source.
	/// </remarks>
	public void Dispose() {

		if (this._disposed) {
			return;
		}

		this._disposed = true;

		if (this._cts != null) {

			this._cts.Cancel();

			// Add waiting for the priority reset task
			if (this._priorityResetTask?.Wait(TimeSpan.FromSeconds(5)) == false) {
				this._logger.PriorityResetShutdownTimeout();
			}

			this._highPriorityResetTimer?.Dispose();

			this._cts.Dispose();

		}
	}
}