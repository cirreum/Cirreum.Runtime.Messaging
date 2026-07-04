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

}