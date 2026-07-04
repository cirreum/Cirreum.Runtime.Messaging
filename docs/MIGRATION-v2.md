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
| `...:BackgroundDelivery:TimeBatchingProfiles` + `ActiveTimeBatchingProfile` | Removed — `AddMessaging(m => m.UseTimeOfDayBatching(schedule => ...))` in code (see below) |
| `Cirreum:Messaging:Distribution:Receiver:*` | `Cirreum:Messaging:Distributed:Receiver:*` |
| `Cirreum:Messaging:Distribution:Metrics:*` | `Cirreum:Messaging:Distributed:Metrics:*` |
| `IDistributedTransportPublisher` (non-generic) | `IDistributedTransportPublisher<DistributedMessage>` (envelope-level channel contract) |

## New Capabilities

- **The `AddMessaging` composition callback** — `AddMessaging(Action<IMessagingBuilder>?)` provides the fluent composition surface for the messaging stack. It applies on every call via `Replace` semantics, so registration order doesn't matter, and `IMessagingBuilder.Services` escape-hatches to the raw service collection.

- **Pluggable batch sizing** — the processor consults `IBatchingPolicy.Evaluate(BatchingContext)` per batch, feeding it the configured base values plus live observables (queue depth, rolling send rate, rolling error rate). The default policy is a no-op (base values pass through). Plug in a custom policy via the callback:

  ```csharp
  builder.AddMessaging(m => m.UseBatchingPolicy<MyTrafficAwareBatchingPolicy>());
  ```

- **Time-of-day batching, ported to a policy** — `UseTimeOfDayBatching(schedule => ...)` wires the framework-supplied `TimeOfDayBatchingPolicy` (from `Cirreum.Messaging.Distributed 1.1.0`). The old `TimeBatchingProfiles` shape maps almost one-to-one: each profile rule's days / start hour / end hour / scaling factor becomes a `TimeOfDayScalingRule`, and the schedule adds an explicit `TimeZoneInfo` (the 1.x scheduler silently used server-local time):

  ```csharp
  builder.AddMessaging(m => m.UseTimeOfDayBatching(schedule => {
      schedule.TimeZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
      schedule.Rules.Add(new() {
          Days = [DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday],
          StartHour = 0,
          EndHour = 24,
          ScalingFactor = 0.5,
          Description = "Weekend scaling - high volume expected"
      });
  }));
  ```

- **Channel-model alignment** — the engine implements the per-channel `IDistributedTransportPublisher<TBase>` contract, so the `DistributedMessage` channel behaves like any other channel in the reset model (e.g., future auth-event channels), and the framework's `EmptyTransportPublisher<TBase>` no-op remains the default when no transport is configured.

## Migration Walkthrough

1. Update the package reference to `Cirreum.Runtime.Messaging 2.0.0`. Remove any direct `Cirreum.Core` reference.
2. On every distributed message type, replace `[MessageDefinition(id, version, target)]` with `[MessageVersion(id, version)]` and, for queue-routed messages, add `[DistributedMessageTarget(MessageTarget.Queue)]` (topic is the default when the attribute is omitted).
3. Rename the configuration root `Distribution` → `Distributed` and pull the `Sender` children (`InstanceKey`, `QueueName`, `TopicName`, `BackgroundDelivery`) up one level.
4. Delete any `TimeBatchingProfiles` / `ActiveTimeBatchingProfile` configuration. If you used a profile, port its rules to `AddMessaging(m => m.UseTimeOfDayBatching(schedule => ...))` (near one-to-one — see New Capabilities); for other dynamic behavior, implement `IBatchingPolicy` and plug it in with `UseBatchingPolicy<T>()`.
5. Replace `DistributedMessagePriority.System` with `DistributedMessagePriority.SystemHealth`.
6. If you resolved `IDistributedTransportPublisher` directly (rare), resolve `IDistributedTransportPublisher<DistributedMessage>` and publish `DistributedMessageEnvelope` instances; typed publishing continues to flow through Conductor.

## What Didn't Change

- Publishing via `IPublisher.PublishAsync(myMessage)` — the outbound bridge still intercepts any `DistributedMessage` notification and delivers it externally.
- Inbound handling via `INotificationHandler<DistributedMessageReceived<T>>` and the receiver's queue/subscription loops, self-echo skip, and dead-letter semantics.
- Circuit breaking (`CircuitBreakerThreshold`, `CircuitResetTimeout`) and priority rate-limiting/age-promotion (`PriorityMessageRateLimit`, `PriorityAgePromotionThreshold`) — same options, same behavior.
- The four cross-broker application properties (`cirreum.identifier`, `cirreum.version`, `cirreum.producer`, `cirreum.node`) and the wire envelope shape. (One value inside the envelope improved: `MessageType` now carries an assembly hint so receivers can resolve app-defined types — the receiver accepts both the new format and legacy bare full names, so mixed producer/consumer versions interoperate.)
- The OpenTelemetry meter/tracing names (`Cirreum.Messaging`).

## Downstream Package Impact

Consumed by app hosts that call `AddMessaging()`. No other Cirreum package references this one; the shared model in `Cirreum.Messaging.Distributed` is where cross-package contracts live.
