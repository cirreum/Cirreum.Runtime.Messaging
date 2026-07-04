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

}