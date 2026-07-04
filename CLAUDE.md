# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

### Build Commands
```bash
# Build the project
dotnet build

# Build in Release mode
dotnet build -c Release

# Clean and rebuild
dotnet clean && dotnet build

# Pack NuGet package
dotnet pack -c Release
```

### Development Commands
```bash
# Restore dependencies
dotnet restore

# Watch mode for development
dotnet watch build
```

## High-Level Architecture

### Core Purpose
Cirreum.Runtime.Messaging has two roles, both wired by the single `AddMessaging()` entry point:

1. **Messaging services composition** — registers the configured messaging providers (Azure Service Bus, via `RegisterServiceProvider<AzureServiceBusRegistrar, ...>` over `Cirreum:Messaging:Providers`), exposing each instance as a keyed `IMessagingClient` with health checks and tracing. This runs unconditionally — apps may consume the clients directly without the distributed-messaging layer.
2. **The Distributed Messaging delivery engine** — the runtime implementation of Cirreum's distributed-messaging channel. The messaging *model* lives in `Cirreum.Messaging.Distributed` (L2 Common): `DistributedMessage`, `DistributedMessageEnvelope`, `IDistributedMessageRegistry`, `DistributedMessagingOptions`/`BackgroundDeliveryOptions`/`ReceiverOptions`, `IBatchingPolicy`, and the `IMessagingMetricsService` contract. This package provides:
   - The outbound Conductor bridge and transport publisher (direct + batched delivery)
   - The policy-driven batching system with prioritization and circuit breaking
   - The inbound hosted receiver (queue and/or topic-subscription consumer loops)
   - OpenTelemetry metrics/tracing implementation

### Key Architectural Components

#### 1. Outbound Path
- **OutboundDistributedMessageHandler&lt;TMessage&gt;** (src/Cirreum.Runtime.Messaging/OutboundDistributedMessageHandler.cs) — open-generic `INotificationHandler<>` bridge; intercepts any published `DistributedMessage` and forwards it to the engine. Registered explicitly by the hosting extension (internal type, so Conductor's public-type assembly scan can't double-register it).
- **DefaultTransportPublisher** (src/Cirreum.Runtime.Messaging/DefaultTransportPublisher.cs) — the delivery engine. Implements the channel contract `IDistributedTransportPublisher<DistributedMessage>` (envelope-level, channel-default semantics) and exposes the typed `PublishMessageAsync<T>` path (per-message `UseBackgroundDelivery`/`Priority` honored). Registered with `Replace` so it wins over the framework's `EmptyTransportPublisher<>` no-op.
- Message definitions and queue/topic routing come from the shipped `DistributedMessageRegistry`, initialized at startup via `DistributedMessageRegistryBootstrap` (a `Cirreum.Startup` `ISystemInitializer`, discovered by the startup assembly scan — no hosting registration needed).

#### 2. Background Batching System
The batching system (src/Cirreum.Runtime.Messaging/Batching/) provides batched message delivery:
- **DefaultBatchProcessor** — background service managing batched delivery; consults the channel's `IBatchingPolicy` per batch (passing base values + live queue-depth/send-rate/error-rate observables) for fill wait time and capacity
- **BatchCircuitBreaker** — resilience pattern for handling failures (driven by `CircuitBreakerThreshold`/`CircuitResetTimeout`)
- **MessagePrioritizer** — priority rate limiting and age-based promotion (driven by `PriorityMessageRateLimit`/`PriorityAgePromotionThreshold`)

There is no time-of-day profile machinery — that was removed in the foundation reset. Dynamic batching behavior belongs in an app-registered `IBatchingPolicy` (registered before `AddMessaging()`; the default policy is a pass-through).

#### 3. Inbound Path
- **DistributedMessageReceiver** (src/Cirreum.Runtime.Messaging/Receiving/) — hosted service consuming from a queue and/or topic subscription; self-echo skip via the `cirreum.node` property, envelope deserialization, and Conductor dispatch wrapped in `DistributedMessageReceived<TMessage>`.

#### 4. Observability Infrastructure
- **DefaultMessagingMetricsService** (src/Cirreum.Runtime.Messaging/Metrics/) — OpenTelemetry implementation of the shipped `IMessagingMetricsService`
- Tracks message lifecycle: received, queued, delivered, failed
- Queue depth monitoring with configurable alerting (`QueueDepthAlertMessage` published back through the channel)
- Distributed tracing support via ActivitySource (`Cirreum.Messaging`)

### Configuration Architecture
Hierarchical configuration under `Cirreum:Messaging:Distributed` (binds `DistributedMessagingOptions` from `Cirreum.Messaging.Distributed`):
- `InstanceKey` / `QueueName` / `TopicName` — flat channel transport settings (no `Sender` wrapper)
- `Cirreum:Messaging:Distributed:BackgroundDelivery` — batching/priority/circuit tuning
- `Cirreum:Messaging:Distributed:Receiver` — inbound sources; presence triggers receiver registration
- `Cirreum:Messaging:Distributed:Metrics` — local `MetricsOptions`

### Service Registration Pattern
One entry point in src/Cirreum.Runtime.Messaging/Extensions/Hosting/:
```csharp
builder.AddMessaging()
```
registers the Azure Service Bus provider (via `RegisterServiceProvider<AzureServiceBusRegistrar, ...>`), the metrics service, the node-id provider, the registry, and — when the `Distributed` section carries an `InstanceKey` — the batching policy default, batch processor, delivery engine, and outbound bridge; the receiver registers when its config section is complete. The registry's startup scan runs via the `ISystemInitializer` discovery in `Cirreum.Startup`.

### Key Design Patterns
1. **Options Pattern** — strongly-typed options from the shared model package
2. **Background Service Pattern** — `IHostedService` for the processor and receiver
3. **Strategy Pattern** — `IBatchingPolicy` decides batch parameters; the engine owns the loop
4. **Circuit Breaker Pattern** — fault tolerance for message delivery

## Important Dependencies
- Targets .NET 10.0 with latest C# features and nullable reference types
- Cirreum ecosystem: `Cirreum.Domain`, `Cirreum.Messaging` (broker abstraction), `Cirreum.Messaging.Distributed` (channel model), `Cirreum.Messaging.Azure` (Service Bus provider), `Cirreum.Runtime.ServiceProvider`, `Cirreum.Startup`
- Uses Microsoft.Extensions.* for hosting, DI, and configuration
- OpenTelemetry for observability
