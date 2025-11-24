namespace Cirreum.Runtime.Messaging.Batching;

using Cirreum.Messaging.Options;

/// <summary>
/// Provides dynamic scheduling for message batch processing based on queue conditions and time profiles.
/// </summary>
/// <remarks>
/// Adjusts batch size and wait times to optimize throughput and resource utilization based on
/// the current load and configured time-based batching profiles.
/// </remarks>
/// <param name="timeBatchingProfile">Optional time-based profile for adjusting batch scheduling based on time of day</param>
internal class BatchScheduler(TimeBatchingProfile? timeBatchingProfile = null) {

	/// <summary>
	/// Tracks the number of consecutive empty batches to implement an exponential backoff strategy
	/// </summary>
	private int _consecutiveEmptyBatches = 0;

	/// <summary>
	/// Calculates the optimal wait time and batch capacity based on current queue conditions
	/// </summary>
	/// <param name="capacity">The base capacity configured for batch processing</param>
	/// <param name="currentCount">The current number of items in the queue</param>
	/// <param name="fillWaitTime">The base wait time for batch filling</param>
	/// <returns>
	/// A tuple containing the adjusted wait time and the optimal batch capacity
	/// based on current conditions
	/// </returns>
	public (TimeSpan waitTime, int capacity) CalculateWaitTimeAndCapacity(int capacity, int currentCount, TimeSpan fillWaitTime) {

		var dynamicWaitTime = this.CalculateFillWaitTime(capacity, currentCount, fillWaitTime);

		// Base capacity from configuration
		var baseCapacity = capacity;

		// Get current queue depth
		var queueDepth = currentCount;

		// Calculate dynamic capacity
		int dynamicCapacity;

		if (queueDepth >= baseCapacity) {
			// Queue is filling up - increase capacity
			// Scale up based on how full the queue is, but cap at some reasonable multiple
			var capacityScaleFactor = Math.Min(3.0, 1.0 + (queueDepth / (double)baseCapacity));
			dynamicCapacity = (int)(baseCapacity * capacityScaleFactor);
		} else if (queueDepth == 0) {
			// Queue is empty - decrease capacity somewhat
			// Don't go too small to avoid inefficiency when messages do arrive
			dynamicCapacity = Math.Max(baseCapacity / 2, 1);
		} else {
			// Partial queue - adjust proportionally
			var fillRatio = queueDepth / (double)baseCapacity;
			// Scale from 50% to 100% of base capacity
			dynamicCapacity = Math.Max((int)(baseCapacity * (0.5 + (0.5 * fillRatio))), 1);
		}

		return (dynamicWaitTime, dynamicCapacity);
	}

	/// <summary>
	/// Calculates the optimal wait time for batch filling based on current queue conditions and time of day.
	/// </summary>
	/// <param name="capacity">The base capacity configured for batch processing</param>
	/// <param name="currentCount">The current number of items in the queue</param>
	/// <param name="fillWaitTime">The base wait time for batch filling</param>
	/// <returns>The time to wait while attempting to fill the current batch</returns>
	/// <remarks>
	/// Uses an adaptive algorithm that:
	/// - Decreases wait times when queue is heavily loaded to process messages faster
	/// - Increases wait times when queue is empty to conserve resources using an exponential backoff
	/// - Scales proportionally for partially filled queues
	/// - Applies time-of-day based scaling factors from the configured time batching profile
	/// </remarks>
	private TimeSpan CalculateFillWaitTime(int capacity, int currentCount, TimeSpan fillWaitTime) {

		// Minimum scaling factor (maximum speed-up) when queue is heavily loaded (75% reduction)
		const double MinimumWaitTimeScalingFactor = 0.25;
		const double MinimumWaitTimeReductionMultiplier = 1.0 - MinimumWaitTimeScalingFactor;

		// Maximum wait time scaling factor (maximum slow-down) when queue is empty
		const double MaximumWaitTimeScalingFactor = 5.0;
		const double WaitTimeIncrementMultiplier = 0.5;

		// Maximum scaling factor for time reduction (aggressive processing for heavy loads)
		const double MaximumWaitTimeReductionFactor = 10.0;

		// Base interval from configuration
		var baseWaitTime = fillWaitTime;

		// Get current queue depth
		var queueDepth = currentCount;

		// Get time-of-day based scaling factor from configuration
		var timeScalingFactor = this.GetTimeBasedScalingFactor();

		// Calculate load-based scaling
		double loadScalingFactor;

		// Case 1: Queue is overloaded - reduce wait time to process faster
		if (queueDepth >= capacity) {

			// Calculate overflow percentage (how much above capacity)
			var overflowRatio = (double)(queueDepth - capacity) / queueDepth;

			// Scale down wait time based on overflow - approaches minimum factor as overflow increases
			loadScalingFactor = Math.Max(
				MinimumWaitTimeScalingFactor,
				1.0 - (MinimumWaitTimeReductionMultiplier * overflowRatio)
			);
		}
		// Case 2: Queue is empty - increase wait time to conserve resources
		else if (queueDepth == 0) {

			// Apply exponential backoff up to maximum
			loadScalingFactor = 1.0 + ((this._consecutiveEmptyBatches + 1) * WaitTimeIncrementMultiplier);

			if (loadScalingFactor < MaximumWaitTimeScalingFactor) {
				this._consecutiveEmptyBatches++;
			} else {
				loadScalingFactor = MaximumWaitTimeScalingFactor;
			}
		}
		// Case 3: Queue has some messages - adjust proportionally
		else {

			// Reset consecutive empty counter
			this._consecutiveEmptyBatches = 0;

			// Adjust based on how full the queue is - more messages = faster processing
			loadScalingFactor = 1.0 + (1.0 - ((double)queueDepth / capacity));
		}

		// Apply all scaling factors to base wait time
		var adjustedInterval = baseWaitTime.TotalMilliseconds * timeScalingFactor * loadScalingFactor;

		// Ensure wait time stays within reasonable bounds
		var minInterval = baseWaitTime.TotalMilliseconds * MinimumWaitTimeScalingFactor;
		var maxInterval = baseWaitTime.TotalMilliseconds * MaximumWaitTimeReductionFactor;

		return TimeSpan.FromMilliseconds(Math.Clamp(adjustedInterval, minInterval, maxInterval));
	}

	/// <summary>
	/// Retrieves the time-based scaling factor from the configured time batching profile
	/// based on the current day and hour.
	/// </summary>
	/// <returns>
	/// The scaling factor to apply based on the current time, or 1.0 if no applicable
	/// rule is found or no profile is configured
	/// </returns>
	private double GetTimeBasedScalingFactor() {

		// Get active profile
		if (timeBatchingProfile is null) {
			// Fall back to default if profile not found
			return 1.0;
		}

		var currentHour = DateTime.Now.Hour;
		var currentDay = (int)DateTime.Now.DayOfWeek;

		// Check each rule in the profile
		foreach (var rule in timeBatchingProfile.Rules) {
			// Check if current day matches rule
			if (rule.Days.Contains((DayOfWeek)currentDay)) {
				// Handle rules that span midnight
				if (rule.StartHour > rule.EndHour) {
					if (currentHour >= rule.StartHour || currentHour < rule.EndHour) {
						return rule.ScalingFactor;
					}
				}
				// Normal time range
				else if (currentHour >= rule.StartHour && currentHour < rule.EndHour) {
					return rule.ScalingFactor;
				}
			}
		}

		// No rules matched, use default
		return timeBatchingProfile.DefaultScalingFactor;
	}

}