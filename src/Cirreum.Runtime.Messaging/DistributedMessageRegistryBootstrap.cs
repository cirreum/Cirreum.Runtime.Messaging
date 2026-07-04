namespace Cirreum.Runtime.Messaging;

using Cirreum.Messaging;
using Cirreum.Startup;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// System initializer that runs the <see cref="DistributedMessageRegistry"/> assembly scan
/// during host startup, so definition and routing-target lookups are populated before the
/// first message is published or received.
/// </summary>
/// <remarks>
/// Discovered and registered by the <see cref="ISystemInitializer"/> startup scan whenever
/// this assembly is loaded; resolves the registry lazily and no-ops when messaging
/// registration has not run.
/// </remarks>
internal sealed class DistributedMessageRegistryBootstrap : ISystemInitializer {

	/// <inheritdoc/>
	public ValueTask RunAsync(IServiceProvider serviceProvider) =>
		serviceProvider.GetService<DistributedMessageRegistry>()?.InitializeAsync()
			?? ValueTask.CompletedTask;

}