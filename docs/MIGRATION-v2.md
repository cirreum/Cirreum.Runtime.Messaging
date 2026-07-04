# Migrating Cirreum.Runtime.Messaging 1.x → 2.0

## Why v2

Version 2.0 rebuilds the package on the reset Cirreum foundation. The distributed-messaging *model* — `DistributedMessage`, `DistributedMessageEnvelope`, the registry, the options types, the `IBatchingPolicy` batching strategy, and the metrics contract — moved down into the `Cirreum.Messaging.Distributed` package (with the versioned-message primitives in `Cirreum.Kernel`). This package is now purely the runtime **delivery engine** over those shared abstractions: the outbound Conductor bridge, the batching processor, the transport publisher, the inbound receiver, and the OpenTelemetry metrics implementation.

If you only publish messages via `IPublisher.PublishAsync(...)` and handle them via `INotificationHandler<DistributedMessageReceived<T>>`, your handler and publish code is unchanged — the breaking changes are in message attribution, configuration shape, and the batching-profile configuration.

## Breaking Changes — Find/Replace Table

| v1.x | v2.0 |
|---|---|
| `<PackageReference Include="Cirreum.Core" ...>` (transitive model source) | `Cirreum.Messaging.Distributed` (model) — referenced transitively by this package |
| `[MessageDefinition("id", "1.0", MessageTarget.Queue)]` | `[MessageVersion("id", "1.0")]` + `[DistributedMessageTarget(MessageTarget.Queue)]` |
| `DistributedMessagePriority.System` | `DistributedMessagePriority.SystemHealth` |
| `Cirreum:Messaging:Distribution` (config root) | `Cirreum:Messaging:Distributed` |
| `Cirreum:Messaging:Distribution:Sender:InstanceKey` | `Cirreum:Messaging:Distributed:InstanceKey` |
| `Cirreum:Messaging:Distribution:Sender:QueueName` / `TopicName` | `Cirreum:Messaging:Distributed:QueueName` / `TopicName` |
| `Cirreum:Messaging:Distribution:Sender:BackgroundDelivery:*` | `Cirreum:Messaging:Distributed:BackgroundDelivery:*` |
| `...:BackgroundDelivery:TimeBatchingProfiles` + `ActiveTimeBatchingProfile` | Removed — register an `IBatchingPolicy` in code (see below) |
| `Cirreum:Messaging:Distribution:Receiver:*` | `Cirreum:Messaging:Distributed:Receiver:*` |
| `Cirreum:Messaging:Distribution:Metrics:*` | `Cirreum:Messaging:Distributed:Metrics:*` |
| `IDistributedTransportPublisher` (non-generic) | `IDistributedTransportPublisher<DistributedMessage>` (envelope-level channel contract) |

## New Capabilities

- **Pluggable batch sizing** — the processor consults `IBatchingPolicy.Evaluate(BatchingContext)` per batch, feeding it the configured base values plus live observables (queue depth, rolling send rate, rolling error rate). The default policy is a no-op (base values pass through). Register a custom policy *before* `AddMessaging()`:

  ```csharp
  builder.Services.AddSingleton<IBatchingPolicy, MyTrafficAwareBatchingPolicy>();
  builder.AddMessaging();
  ```

- **Channel-model alignment** — the engine implements the per-channel `IDistributedTransportPublisher<TBase>` contract, so the `DistributedMessage` channel behaves like any other channel in the reset model (e.g., future auth-event channels), and the framework's `EmptyTransportPublisher<TBase>` no-op remains the default when no transport is configured.

## Migration Walkthrough

1. Update the package reference to `Cirreum.Runtime.Messaging 2.0.0`. Remove any direct `Cirreum.Core` reference.
2. On every distributed message type, replace `[MessageDefinition(id, version, target)]` with `[MessageVersion(id, version)]` and, for queue-routed messages, add `[DistributedMessageTarget(MessageTarget.Queue)]` (topic is the default when the attribute is omitted).
3. Rename the configuration root `Distribution` → `Distributed` and pull the `Sender` children (`InstanceKey`, `QueueName`, `TopicName`, `BackgroundDelivery`) up one level.
4. Delete any `TimeBatchingProfiles` / `ActiveTimeBatchingProfile` configuration. If you used a profile, port its intent to an `IBatchingPolicy` implementation and register it before `AddMessaging()`.
5. Replace `DistributedMessagePriority.System` with `DistributedMessagePriority.SystemHealth`.
6. If you resolved `IDistributedTransportPublisher` directly (rare), resolve `IDistributedTransportPublisher<DistributedMessage>` and publish `DistributedMessageEnvelope` instances; typed publishing continues to flow through Conductor.

## What Didn't Change

- Publishing via `IPublisher.PublishAsync(myMessage)` — the outbound bridge still intercepts any `DistributedMessage` notification and delivers it externally.
- Inbound handling via `INotificationHandler<DistributedMessageReceived<T>>` and the receiver's queue/subscription loops, self-echo skip, and dead-letter semantics.
- Circuit breaking (`CircuitBreakerThreshold`, `CircuitResetTimeout`) and priority rate-limiting/age-promotion (`PriorityMessageRateLimit`, `PriorityAgePromotionThreshold`) — same options, same behavior.
- The four cross-broker application properties (`cirreum.identifier`, `cirreum.version`, `cirreum.producer`, `cirreum.node`) and the wire envelope shape.
- The OpenTelemetry meter/tracing names (`Cirreum.Messaging`).

## Downstream Package Impact

Consumed by app hosts that call `AddMessaging()`. No other Cirreum package references this one; the shared model in `Cirreum.Messaging.Distributed` is where cross-package contracts live.
