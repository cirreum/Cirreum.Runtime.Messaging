namespace Cirreum.Runtime.Messaging;

using Cirreum.Messaging.Batching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

/// <summary>
/// Default <see cref="IMessagingBuilder"/> implementation over the host's service
/// collection.
/// </summary>
/// <param name="services">The service collection to compose into.</param>
internal sealed class MessagingBuilder(IServiceCollection services) : IMessagingBuilder {

	/// <inheritdoc/>
	public IServiceCollection Services { get; } = services;

	/// <inheritdoc/>
	public IMessagingBuilder UseBatchingPolicy<TPolicy>() where TPolicy : class, IBatchingPolicy {
		this.Services.Replace(ServiceDescriptor.Singleton<IBatchingPolicy, TPolicy>());
		return this;
	}

	/// <inheritdoc/>
	public IMessagingBuilder UseTimeOfDayBatching(Action<TimeOfDayBatchingOptions> configure) {
		ArgumentNullException.ThrowIfNull(configure);
		var options = new TimeOfDayBatchingOptions();
		configure(options);
		// The policy constructor validates the schedule, failing composition fast on a
		// bad configuration rather than at first-batch time.
		this.Services.Replace(ServiceDescriptor.Singleton<IBatchingPolicy>(new TimeOfDayBatchingPolicy(options)));
		return this;
	}

}