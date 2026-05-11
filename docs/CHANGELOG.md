# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- **`Cirreum.Runtime.Messaging.Receiving.DistributedMessageReceiver`** — new `IHostedService` that consumes inbound distributed messages from a configured queue and/or topic subscription, deserializes them, and dispatches them through Conductor by publishing `DistributedMessageReceived<TMessage>` notifications. Apps implement standard `INotificationHandler<DistributedMessageReceived<T>>` handlers (auto-discovered by Conductor) to react. The receiver runs concurrent consumer loops per configured source, applies per-source `MaxConcurrency` via `Parallel.ForEachAsync`, skips self-echoes pre-deserialization via the `cirreum.node` application property, and handles unknown types / deserialization failures / handler failures with broker-appropriate semantics (complete / dead-letter / abandon). Registered conditionally based on the presence and completeness of the `Cirreum:Messaging:Distribution:Receiver` configuration section.
- **Default `INodeIdProvider` registration** — `HostingExtensions.AddDistributedMessaging` now calls `TryAddSingleton<INodeIdProvider, DefaultNodeIdProvider>()` so every host running with messaging has a working node identity available. Apps that need bespoke resolution register their own `INodeIdProvider` implementation before invoking `AddMessaging()`.

### Changed

- **`DefaultTransportPublisher` stamps four cross-broker application properties** on every outgoing `OutboundMessage`: `cirreum.identifier`, `cirreum.version`, `cirreum.producer`, and `cirreum.node`. Each broker maps `OutboundMessage.Properties` to its native filterable property bag (Service Bus `ApplicationProperties`, AWS SNS message attributes, Kafka headers, NATS headers). Enables broker-side subscription filtering by message identifier / version / producer, and lets receivers skip self-echoes by comparing `cirreum.node` against the local replica identity. Strictly additive — does not change message body, subject, or transport semantics.
- **`DefaultTransportPublisher` constructor** — now also takes `INodeIdProvider` to source the per-replica node identity for the new `cirreum.node` application property. Hosting extension wires this automatically; no app code change required.
- **`DistributeMessagingStrings`** — added constants for the four outbound application-property keys, the receive activity name, and receive-side event names (`Event_SelfEchoSkipped`, `Event_UnknownMessageType`, `Event_EnvelopeDeserializationFailed`, `Event_MessageDispatched`). Centralized to keep all wire-protocol strings together.

These additions complete the inbound side of the distributed messaging family. The L3 abstractions consumed by this release shipped in `Cirreum.Core 5.2.0`: `INodeIdProvider`, `DefaultNodeIdProvider`, `DistributedMessageReceived<TMessage>`, `ReceiverOptions`, and `DistributedMessageEnvelope.PublishedAt`. See [`docs/RELEASE-NOTES-v1.1.0.md`](RELEASE-NOTES-v1.1.0.md) for the full architectural framing, configuration shape, routing convention, and operational guidance.

## [1.0.39] - 2026-05-10

### Updated

- Updated NuGet packages.

## [1.0.38] - 2026-05-10

### Updated

- Updated NuGet packages.

## [1.0.37] - 2026-05-01

### Updated
- Updated NuGet packages.

