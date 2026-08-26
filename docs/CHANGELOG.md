# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [2.3.3] - 2026-08-26

### Updated

- Updated NuGet packages.

## [2.3.2] - 2026-08-25

### Updated

- Updated NuGet packages.

## [2.3.1] - 2026-08-20

### Updated

- Updated NuGet packages.

## [2.3.0] - 2026-08-04

### Added

- **`ReceiverOptions.PrefetchCount` reaches the broker.** The receiver passes it through the
  tuned `UseQueueReceiver` / `UseSubscription` overloads (`Cirreum.Messaging` 1.1.1 →
  `Cirreum.Messaging.Azure` 1.2.0), completing the three-repo receiver-tuning chain. ⚠️ The
  documented default of `10` now actually applies where the broker SDK's own default
  (no prefetch) silently did before — operators who want prefetch off set `PrefetchCount: 0`.
  (From the backlog.)
- **`ReceiverOptions.MaxAutoLockRenewalDuration` is honored** — implemented where lock
  renewal belongs: the receive loop renews the message's broker-side lock on a fixed 15-second
  cadence while handlers run, capped at the configured duration (default 5 minutes; zero
  disables), stopping renewal before any ack. Long-running handlers no longer lose their lock
  mid-processing and trigger duplicate redelivery. A renewal failure logs a warning
  (event 2012) and stops renewing — broker redelivery applies if the lock then expires.

### Updated

- Re-pinned `Cirreum.Messaging` `1.0.109` → `1.1.1` (the `ReceiverTuning` contract),
  `Cirreum.Messaging.Azure` `1.1.0` → `1.2.0` (the Service Bus prefetch mapping), and
  `Cirreum.Domain` `4.0.1` → `4.2.0` (Cirreum spine 4.2.0 wave).

## [2.2.3] - 2026-07-31

### Updated

- Updated NuGet packages (Cirreum spine 4.0.1 wave: records-only grant semantics via `Cirreum.Domain` 4.0.1 / `Cirreum.Contracts` 4.0.1; Infrastructure and Runtime repins).

## [2.2.2] - 2026-07-30

### Updated

- Updated NuGet packages — picks up the `Cirreum.Domain` 3.0.0 authorization-enforcement wave
  (fail-open operation-authorization fix + `IPolicyAuthorizer` rename) through the re-pinned
  lower-layer packages; see Cirreum.Domain `MIGRATION-v3.md`.

## [2.2.1] - 2026-07-29

### Updated

- Updated NuGet packages.

## [2.2.0] - 2026-07-27

### Changed

- **Conductor's publish/subscribe markers are renamed** — `INotification` → `IDomainEvent`,
  `INotificationHandler<T>` → `IDomainEventHandler<T>` — following `Cirreum.Kernel` 2.0.0.
  Cirreum used "notification" for two unrelated concepts: in-application publish/subscribe, and
  the human-facing state family a client binds to in order to show a person something.
  `IDomainEvent` names the first for what it is; "notification" now refers only to the second.

  **`INotificationState` and `IScopedNotificationState` keep their names** — they are the
  human-facing concept, and preserving that separation is the point of the rename. A project-wide
  find/replace of "Notification" will destroy it.

  **Distributed-message consumers need a one-line interface change each** —
  `INotificationHandler<DistributedMessageReceived<T>>` becomes
  `IDomainEventHandler<DistributedMessageReceived<T>>`. That combination names this package's
  `DistributedMessageReceived<T>` alongside a marker owned by `Cirreum.Kernel`, so it appears in no
  other package's migration guide; the walkthrough is in `RELEASE-NOTES-v2.2.0.md`. Applications
  that only publish need a re-pin.

  **This is a minor, not a major.** Every reference to the renamed markers in this package is inside
  an `internal sealed` type or an XML doc comment — `DistributedMessage`, `[MessageVersion]`,
  `IPublisher.PublishAsync`, `AddMessaging()` and the batching seam are untouched. The break
  consumers hit belongs to `Cirreum.Kernel` / `Cirreum.Contracts`, both of which went 2.0.0 to
  signal it, and this release cannot be taken without them.

  Wire compatibility is unaffected in both directions, so a 2.1.x publisher and a 2.2.0 consumer can
  run side by side during a rolling deployment.

## [2.1.4] - 2026-07-24

### Fixed

- **The tracing `ActivitySource` leaked and was duplicated.** `DefaultBatchProcessor` and
  `DistributedMessageDeliveryEngine` each constructed their own `ActivitySource` for the same
  `Cirreum.Messaging` name — two listener registrations for one logical source — and the batch
  processor's was never disposed. Both now use a single shared, process-lifetime
  `MessagingTelemetry.ActivitySource`, which is the correct shape for a source: it is a shared
  listener registration, not a per-instance resource. The engine no longer disposes it either —
  doing so had ended tracing for the batch processor and for any engine constructed afterwards.
- **Spans allocated even when nothing was listening.** Both classes used
  `CreateActivity(...)` followed by `Start()`, which materializes an `Activity` regardless of
  whether a listener is attached — once per published message and once per batch. Both now call
  `StartActivity(...)`, which returns `null` when no listener is registered.
- **Batch spans invented a parent that never existed.** `ProcessBatchAsync` passed an explicit
  `ActivityContext` built from a random trace id and a random *parent* span id, producing traces
  whose root referenced a non-existent span, and forced `ActivityTraceFlags.Recorded` past
  whatever sampler the host had configured. The batch span now roots itself when nothing is
  ambient and links to a real caller when there is one, and respects the configured sampler.
- **The source carried no version.** Both sources were created without one; the source now
  reports this package's assembly version, matching the meter in
  `DefaultMessagingMetricsService`.

## [2.1.3] - 2026-07-24

### Updated

- Updated NuGet packages.

## [2.1.2] - 2026-07-20

### Updated

- Updated NuGet packages.

## [2.1.1] - 2026-07-19

### Updated

- Updated NuGet packages.

## [2.1.0] - 2026-07-09

### Changed

- **Inbound type resolution is now registry-by-identity.** `DistributedMessageReceiver` resolves the incoming type through the channel registry's `ResolveType(MessageIdentifier, MessageVersion)` — selecting only from this process's own vetted scan set — instead of reflecting over the envelope's CLR type hint. The envelope's `MessageType` is now diagnostic metadata only (logging / dead-letter triage), never a resolution input. Payloads deserialize via `DeserializeMessage(Type)` against the resolved type.
- **Per-source disposition for an unregistered inbound identity.** A **queue** message whose identity resolves to no local type is now **dead-lettered** for operator triage (a queue is addressed to this consumer, so an unknown identity signals a missing assembly or a producer running ahead). A **topic subscription** message with an unregistered identity is **completed and logged** — a fan-out subscription normally delivers family members a given consumer need not handle, so this is normal weather, never a redelivery loop. Previously both sources completed-and-logged.
- **Internal delivery types renamed** (no public API): the delivery engine `DefaultTransportPublisher` → `DistributedMessageDeliveryEngine`, and the outbound Conductor bridge `OutboundDistributedMessageHandler<T>` → `DistributedMessageSender<T>`. The engine no longer implements a transport-publisher interface — it is the outbound seam, injected directly by the bridge; the redundant interface registration is gone. Apps that publish via `IPublisher.PublishAsync(...)` and handle via `INotificationHandler<DistributedMessageReceived<T>>` are unaffected.
- Hot-path timing (per-publish in the delivery engine, per-batch in the batch processor) now uses allocation-free `Stopwatch.GetTimestamp()` / `Stopwatch.GetElapsedTime(...)` instead of allocating a `Stopwatch` instance. No behavioral change.
- Inbound receiver dispatch now runs through a per-type delegate closed over the concrete message type (cached in the existing dispatcher cache), replacing the per-message `Activator.CreateInstance` + `MethodInfo.Invoke` + `object[]` argument allocation with a direct call. No behavioral change.

### Updated

- Updated NuGet packages as part of the lower-layer changes.

## [2.0.1] - 2026-07-04

### Updated

- Updated NuGet packages — picks up `Cirreum.Messaging.Azure 1.0.19` (corrected package metadata and documentation).

## [2.0.0] - 2026-07-04

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
- **`AddMessaging(Action<IMessagingBuilder>?)` composition callback** — fluent configuration of the messaging stack, completing the fluent surface the `IBatchingPolicy` model documentation describes: `UseBatchingPolicy<TPolicy>()` for custom policies and `UseTimeOfDayBatching(schedule => ...)` for the framework-supplied `TimeOfDayBatchingPolicy` (from `Cirreum.Messaging.Distributed 1.1.0`; validates the schedule at composition time). The callback applies on every call — even after the stack is registered — via `Replace` semantics, so it wins over the framework's pass-through default regardless of call order. `IMessagingBuilder.Services` escape-hatches to the raw service collection.
- **First test project** — `tests/Cirreum.Runtime.Messaging.Tests` (25 tests): `AddMessaging` composition (conditional registrations, callback ordering, bridge dedup, receiver config sniffing), the delivery engine (direct vs background vs envelope-default, cross-broker property stamping), receiver ack semantics (self-echo complete, dead-letter, unknown-type complete, success complete, failed-handler abandon), and the batch processor (policy-driven flush, circuit-breaker open behavior).
- **"Choosing a Dispatch Path" README guidance** (from the backlog) — the three valid dispatch patterns (full framework / app-routed-framework-formatted / fully bespoke), the wire-contract-vs-transport mental model, and a pattern-2 example routing a framework-formatted envelope to an app-owned queue.

### Changed

- **Receiver type resolution** — inbound type resolution now uses `DistributedMessageEnvelope.ResolveMessageType()` (from `Cirreum.Messaging.Distributed 1.1.0`) instead of raw `Type.GetType`, so app-defined message types resolve correctly across assemblies (both the new assembly-hinted envelope format and legacy bare full names).
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

