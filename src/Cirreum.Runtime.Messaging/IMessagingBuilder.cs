namespace Cirreum.Runtime.Messaging;

using Cirreum.Messaging.Batching;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Fluent composition surface for the messaging stack, supplied to the configure
/// callback of <c>AddMessaging(...)</c>.
/// </summary>
public interface IMessagingBuilder {

	/// <summary>
	/// The underlying service collection, for advanced registrations not covered by
	/// the fluent surface.
	/// </summary>
	IServiceCollection Services { get; }

	/// <summary>
	/// Uses <typeparamref name="TPolicy"/> as the channel's <see cref="IBatchingPolicy"/>,
	/// replacing the framework's pass-through default. The policy is consulted per batch
	/// with the channel's configured base values plus live queue-depth, send-rate, and
	/// error-rate observables.
	/// </summary>
	/// <typeparam name="TPolicy">The batching policy implementation. Registered as a
	/// singleton.</typeparam>
	IMessagingBuilder UseBatchingPolicy<TPolicy>() where TPolicy : class, IBatchingPolicy;

	/// <summary>
	/// Uses the framework-supplied <see cref="TimeOfDayBatchingPolicy"/> as the channel's
	/// <see cref="IBatchingPolicy"/> — simple day-of-week / time-of-day scaling of the
	/// base batch-fill wait time, without writing a custom policy.
	/// </summary>
	/// <param name="configure">Configures the schedule: the time zone, the default
	/// scaling factor, and the day/hour-window rules.</param>
	/// <exception cref="ArgumentException">The configured schedule is invalid (a
	/// non-positive scaling factor, a rule without days, or out-of-range hours).</exception>
	IMessagingBuilder UseTimeOfDayBatching(Action<TimeOfDayBatchingOptions> configure);

}