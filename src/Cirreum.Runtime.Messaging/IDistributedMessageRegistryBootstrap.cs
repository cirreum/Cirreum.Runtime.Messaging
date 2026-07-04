namespace Cirreum.Runtime.Messaging;

using Cirreum.Startup;

/// <summary>
/// Marker contract for the startup bootstrap that initializes the
/// <see cref="Cirreum.Messaging.DistributedMessageRegistry"/> during host startup.
/// </summary>
/// <remarks>
/// Exists so the <see cref="IAutoInitialize"/> discovery scan registers and resolves the
/// bootstrap by a properly-named primary interface even when messaging registration has
/// not run.
/// </remarks>
internal interface IDistributedMessageRegistryBootstrap : IAutoInitialize;