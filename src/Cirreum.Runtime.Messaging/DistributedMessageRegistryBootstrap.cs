namespace Cirreum.Runtime.Messaging;

using Cirreum.Messaging;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Startup bootstrap that runs the <see cref="DistributedMessageRegistry"/> assembly scan
/// during host initialization, so definition and routing-target lookups are populated
/// before the first message is published or received.
/// </summary>
/// <param name="serviceProvider">Service provider used to locate the registry.</param>
/// <remarks>
/// Resolves the registry lazily and no-ops when messaging registration has not run —
/// the <see cref="Cirreum.Startup.IAutoInitialize"/> discovery scan picks this type up
/// whenever the assembly is loaded, configured or not.
/// </remarks>
internal sealed class DistributedMessageRegistryBootstrap(
	IServiceProvider serviceProvider
) : IDistributedMessageRegistryBootstrap {

	/// <inheritdoc/>
	public ValueTask InitializeAsync() =>
		serviceProvider.GetService<DistributedMessageRegistry>()?.InitializeAsync()
			?? ValueTask.CompletedTask;

}