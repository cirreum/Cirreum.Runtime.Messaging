# Cirreum.Runtime.Messaging 2.0.0 — Rebuilt on the reset foundation

## Why this release exists

Version 2.0 completes this package's move off the legacy `Cirreum.Core 5.x` monolith onto the reset foundation. The distributed-messaging *model* — `DistributedMessage`, the wire envelope, the registry, the options types, the `IBatchingPolicy` strategy, and the metrics contract — now ships in `Cirreum.Messaging.Distributed`; this package is the runtime that brings it to life: the messaging services composition (`AddMessaging()`), the outbound Conductor bridge, the policy-driven batch processor, the transport publisher, the inbound receiver, and OpenTelemetry metrics.

If you publish via `IPublisher.PublishAsync(...)` and handle via `INotificationHandler<DistributedMessageReceived<T>>`, your code is unchanged — the breaking changes are message attribution, the configuration shape, and the batching-profile configuration. See [`MIGRATION-v2.md`](MIGRATION-v2.md) for the find/replace table and walkthrough.

## What's new

### The `AddMessaging` composition callback

```csharp
builder.AddMessaging(m => m.UseBatchingPolicy<TrafficAwareBatchingPolicy>());
```

`IMessagingBuilder` completes the fluent surface the `IBatchingPolicy` documentation describes: `UseBatchingPolicy<TPolicy>()` for custom policies, `UseTimeOfDayBatching(schedule => ...)` for the framework-supplied day-of-week / time-of-day scaler, and a `Services` escape hatch. The callback applies on every call via `Replace` semantics, so registration order doesn't matter.

### Policy-driven batching with real observables

The batch processor consults the channel's `IBatchingPolicy` per batch, passing the configured base values plus live signals — current queue depth, rolling send rate, rolling error rate — and logs every decision change including the policy's `Reason`. The old time-profile appsettings machinery is gone; dynamic batching is a code concern. Circuit breaking and priority rate-limiting/promotion are unchanged.

### Outbound bridge + correct cross-assembly type handling

The outbound Conductor bridge (formerly in `Cirreum.Core`) now lives here, and the inbound receiver resolves message types via `DistributedMessageEnvelope.ResolveMessageType()` — fixing a latent defect where app-defined message types could not be resolved from the wire.

## Breaking changes (summary)

- Configuration root `Cirreum:Messaging:Distribution` → `Cirreum:Messaging:Distributed`, `Sender` wrapper flattened.
- `[MessageDefinition(id, ver, target)]` → `[MessageVersion(id, ver)]` + `[DistributedMessageTarget(target)]`.
- `DistributedMessagePriority.System` → `SystemHealth`.
- `TimeBatchingProfiles` / `ActiveTimeBatchingProfile` configuration removed → `IBatchingPolicy` in code.
- The transport publisher implements `IDistributedTransportPublisher<DistributedMessage>` (envelope-level channel contract).

Full details and the migration walkthrough: [`MIGRATION-v2.md`](MIGRATION-v2.md).

## Compatibility

Requires `Cirreum.Messaging.Distributed 1.1.0` (release that first). Wire compatibility: outbound envelopes now stamp assembly-hinted type names; the receiver accepts both the new format and legacy bare full names.

## See also

- [`MIGRATION-v2.md`](MIGRATION-v2.md) — breaking-changes find/replace table and walkthrough.
- [`CONFIGURATION.md`](CONFIGURATION.md) — every setting with defaults.
- `Cirreum.Messaging.Distributed` 1.1.0 release notes — the model-side changes this release consumes.
