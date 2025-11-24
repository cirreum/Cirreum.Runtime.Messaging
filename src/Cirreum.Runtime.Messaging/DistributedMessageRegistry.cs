namespace Cirreum.Runtime.Messaging;

using Cirreum.Messaging;
using Cirreum.Startup;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;

internal sealed class DistributedMessageRegistry(
	ILogger<DistributedMessageRegistry> logger
) : MessageRegistryBase(logger)
  , IAutoInitialize {

	/// <inheritdoc/>
	public ValueTask InitializeAsync() {
		return this.DefaultInitializationAsync();
	}

}