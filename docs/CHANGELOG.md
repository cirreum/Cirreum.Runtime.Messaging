# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Breaking

- **Rebuilt on the reset foundation** — the legacy `Cirreum.Core 5.x` monolith reference is replaced by `Cirreum.Domain` + `Cirreum.Messaging` + `Cirreum.Messaging.Distributed`. The distributed-messaging model (message base, envelope, registry, options, batching policy, metrics contract) now comes from `Cirreum.Messaging.Distributed`; this package is the runtime delivery engine over those abstractions.
- **Configuration section renamed and flattened** — `Cirreum:Messaging:Distribution` → `Cirreum:Messaging:Distributed`, and the `Sender` wrapper is gone: `InstanceKey`, `QueueName`, and `TopicName` now sit directly under `Distributed` (binding `DistributedMessagingOptions`). `BackgroundDelivery`, `Receiver`, and `Metrics` keep their relative positions under the renamed root.
- **Time-of-day batching profiles removed** — `TimeBatchingProfiles` / `ActiveTimeBatchingProfile` configuration and the internal `BatchScheduler` are gone. Batch sizing is now decided per batch by the `IBatchingPolicy` strategy from `Cirreum.Messaging.Distributed`: the default policy returns the channel's configured base values (`BatchCapacity`, `BatchFillWaitTime`); apps needing dynamic behavior register their own `IBatchingPolicy` singleton before calling `AddMessaging()`. Circuit breaking and priority rate-limiting/promotion are unchanged and remain driven by `BackgroundDelivery` options.
- **Transport publisher re-typed to the channel contract** — the old non-generic `IDistributedTransportPublisher` implementation is replaced: the engine now implements `IDistributedTransportPublisher<DistributedMessage>` (envelope-level, per the reset's per-channel model) and is registered with `Replace` so it wins over the framework's no-op default. The envelope-level surface applies channel-default delivery semantics; per-message `UseBackgroundDelivery`/`Priority` preferences are honored on the typed path used by the outbound bridge.
- **`DistributedMessagePriority.System` → `SystemHealth`** — follows the shipped enum in `Cirreum.Messaging.Distributed`.
- **Message attribution follows the reset model** — message types are declared with `[MessageVersion(identifier, version)]` (Cirreum.Kernel) plus optional `[DistributedMessageTarget(MessageTarget.Queue|Topic)]`, replacing the legacy `[MessageDefinition(id, version, target)]`.

### Added

- **`OutboundDistributedMessageHandler<TMessage>`** — the outbound Conductor bridge for the `DistributedMessage` channel. The legacy bridge lived in `Cirreum.Core` and was removed in the reset; this package now registers it (open-generic `INotificationHandler<>`, internal) whenever the channel has a configured transport, so `IPublisher.PublishAsync(myMessage)` continues to fan out to in-process handlers and the external transport.
- **Registry bootstrap** — the shipped `DistributedMessageRegistry` (from `Cirreum.Messaging.Distributed`) is registered as the channel's `IDistributedMessageRegistry` and initialized during host startup via a `Cirreum.Startup` `ISystemInitializer` (the first startup phase — framework/infrastructure initialization), replacing the internal registry duplicate this package used to carry.
- **Batching-policy observability** — the batch processor feeds the policy real observables (`CurrentQueueDepth`, `RecentSendRatePerSecond`, `RecentErrorRate` over a rolling 60-second window) and logs each policy decision change, including the policy's optional `Reason`.
- **`AddMessaging(Action<IMessagingBuilder>?)` composition callback** — fluent configuration of the messaging stack, starting with `UseBatchingPolicy<TPolicy>()` (the fluent surface the `IBatchingPolicy` model documentation describes). The callback applies on every call — even after the stack is registered — via `Replace` semantics, so it wins over the framework's pass-through default regardless of call order. `IMessagingBuilder.Services` escape-hatches to the raw service collection.

### Changed

- **Idle efficiency** — the delivery loop now waits for the first buffered message before evaluating the batching policy and opening a fill window, instead of polling on a timer while the queue is empty.
- **Batch capacity metrics** — batch-level metrics and trace tags now report the policy-decided capacity rather than the internal list's allocated capacity.

### Removed

- **Internal `DistributedMessageRegistry` duplicate** — superseded by the shipped, public registry in `Cirreum.Messaging.Distributed` (which also builds the per-type `MessageTarget` map from `[DistributedMessageTarget]`).
- **`BatchScheduler`** and the time-of-day profile machinery (see Breaking).

## [1.1.0] - 2026-05-10

### Added

- **`Cirreum.Runtime.Messaging.Receiving.DistributedMessageReceiver`** — new `IHostedService` that consumes inbound distributed messages from a configured queue and/or topic subscription, deserializes them, and dispatches them through Conductor by publishing `DistributedMessageReceived<TMessage>` notifications. Apps implement standard `INotificationHandler<DistributedMessageReceived<T>>` handlers (auto-discovered by Conductor) to react. The receiver runs concurrent consumer loops per configured source, applies per-source `MaxConcurrency` via `Parallel.ForEachAsync`, skips self-echoes pre-deserialization via the `cirreum.node` application property, and handles unknown types / deserialization failures / handler failures with broker-appropriate semantics (complete / dead-letter / abandon). Registered conditionally based on the presence and completeness of the `Cirreum:Messaging:Distribution:Receiver` configuration section.
- **Default `INodeIdProvider` registration** — `HostingExtensions.AddDistributedMessaging` now calls `TryAddSingleton<INodeIdProvider, DefaultNodeIdProvider>()` so every host running with messaging has a working node identity available. Apps that need bespoke resolution register their own `INodeIdProvider` implementation before invoking `AddMessaging()`.

### Changed

- **`DefaultTransportPublisher` stamps four cross-broker application properties** on every outgoing `OutboundMessage`: `cirreum.identifier`, `cirreum.version`, `cirreum.producer`, and `cirreum.node`. Each broker maps `OutboundMessage.Properties` to its native filterable property bag (Service Bus `ApplicationProperties`, AWS SNS message attributes, Kafka headers, NATS headers). Enables broker-side subscription filtering by message identifier / version / producer, and lets receivers skip self-echoes by comparing `cirreum.node` against the local replica identity. Strictly additive — does not change message body, subject, or transport semantics.
- **`DefaultTransportPublisher` constructor** — now also takes `INodeIdProvider` to source the per-replica node identity for the new `cirreum.node` application property. Hosting extension wires this automatically; no app code change required.
- **`DistributeMessagingStrings`** — added constants for the four outbound application-property keys, the receive activity name, and receive-side event names (`Event_SelfEchoSkipped`, `Event_UnknownMessageType`, `Event_EnvelopeDeserializationFailed`, `Event_MessageDispatched`). Centralized to keep all wire-protocol strings together.

These additions complete the inbound side of the distributed messaging family. The abstractions consumed by this release shipped in `Cirreum.Core 5.2.0`: `INodeIdProvider`, `DefaultNodeIdProvider`, `DistributedMessageReceived<TMessage>`, `ReceiverOptions`, and `DistributedMessageEnvelope.PublishedAt`. See [`docs/RELEASE-NOTES-v1.1.0.md`](RELEASE-NOTES-v1.1.0.md) for the full architectural framing, configuration shape, routing convention, and operational guidance.

## [1.0.39] - 2026-05-10

### Updated

- Updated NuGet packages.

## [1.0.38] - 2026-05-10

### Updated

- Updated NuGet packages.

## [1.0.37] - 2026-05-01

### Updated
- Updated NuGet packages.

