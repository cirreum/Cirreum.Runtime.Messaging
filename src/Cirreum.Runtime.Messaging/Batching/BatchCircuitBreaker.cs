namespace Cirreum.Runtime.Messaging.Batching;

using Cirreum.Messaging;
using Microsoft.Extensions.Logging;

/// <summary>
/// Implements a circuit breaker pattern to prevent repeated attempts to process message batches
/// when consecutive failures occur.
/// </summary>
/// <remarks>
/// The circuit breaker tracks consecutive failures and will "open" (prevent processing) after
/// a configured threshold is reached. Once opened, the circuit remains in that state for a
/// specified timeout period before automatically resetting and allowing attempts again.
/// </remarks>
/// <param name="circuitBreakerThreshold">The number of consecutive failures required to open the circuit</param>
/// <param name="circuitResetTimeout">The time period the circuit remains open before allowing new attempts</param>
internal class BatchCircuitBreaker(int circuitBreakerThreshold, TimeSpan circuitResetTimeout) {

	/// <summary>
	/// Lock object used to ensure thread-safe access to the circuit breaker state
	/// </summary>
	private readonly Lock _circuitBreakerLock = new Lock();

	/// <summary>
	/// Counter tracking the number of consecutive processing failures
	/// </summary>
	private int _consecutiveFailures = 0;

	/// <summary>
	/// Timestamp indicating when the circuit will automatically close if currently open
	/// </summary>
	/// <remarks>Null indicates the circuit is currently closed (allowing processing)</remarks>
	private DateTime? _circuitOpenUntil = null;

	/// <summary>
	/// Attempts to enter the circuit breaker to perform batch processing
	/// </summary>
	/// <param name="logger">Logger instance for recording circuit state changes</param>
	/// <returns>
	/// True if the circuit is closed and processing should proceed;
	/// False if the circuit is open and processing should be skipped
	/// </returns>
	public bool TryEnter(ILogger logger) {

		var circuitIsOpen = false;

		// First check if circuit is open before filling batch
		lock (this._circuitBreakerLock) {
			if (this._circuitOpenUntil.HasValue) {
				// Circuit is still open - check if we should remain open
				if (DateTime.UtcNow < this._circuitOpenUntil.Value) {
					circuitIsOpen = true;
					logger.CircuitStillOpen(
						this._circuitOpenUntil.Value - DateTime.UtcNow);
				} else {
					// Circuit timeout has elapsed, we can try again
					this._circuitOpenUntil = null;
					this._consecutiveFailures = 0;
					logger.CircuitBreakerProcessingReset();
				}
			}
		}

		return !circuitIsOpen;

	}

	/// <summary>
	/// Resets the consecutive failures counter after a successful batch processing operation
	/// </summary>
	public void Reset() {
		// Success - reset failure counter
		lock (this._circuitBreakerLock) {
			this._consecutiveFailures = 0;
		}

	}

	/// <summary>
	/// Handles a processing fault by incrementing the failure counter and potentially opening the circuit
	/// </summary>
	/// <param name="target">The target of the message that caused the failure</param>
	/// <param name="logger">Logger instance for recording circuit state changes</param>
	/// <param name="ex">The exception that occurred during batch processing</param>
	public void HandleFault(MessageTarget target, ILogger logger, Exception ex) {

		// Increment failure counter with thread safety
		var shouldOpenCircuit = false;
		lock (this._circuitBreakerLock) {
			this._consecutiveFailures++;

			// Check if we should open the circuit
			if (this._consecutiveFailures >= circuitBreakerThreshold) {
				this._circuitOpenUntil = DateTime.UtcNow.Add(circuitResetTimeout);
				shouldOpenCircuit = true;
			}
		}

		// Logging outside the lock to minimize lock duration
		if (shouldOpenCircuit) {
			var exType = ex.GetType().Name;
			logger.CircuitBreakerOpened(target, this._consecutiveFailures, exType, circuitResetTimeout);
		}

	}

}