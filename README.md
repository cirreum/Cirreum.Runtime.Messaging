# Cirreum.Runtime.Messaging

[![NuGet Version](https://img.shields.io/nuget/v/Cirreum.Runtime.Messaging.svg?style=flat-square&labelColor=1F1F1F&color=003D8F)](https://www.nuget.org/packages/Cirreum.Runtime.Messaging/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/Cirreum.Runtime.Messaging.svg?style=flat-square&labelColor=1F1F1F&color=003D8F)](https://www.nuget.org/packages/Cirreum.Runtime.Messaging/)
[![GitHub Release](https://img.shields.io/github/v/release/cirreum/Cirreum.Runtime.Messaging?style=flat-square&labelColor=1F1F1F&color=FF3B2E)](https://github.com/cirreum/Cirreum.Runtime.Messaging/releases)
[![License](https://img.shields.io/badge/license-MIT-F2F2F2?style=flat-square&labelColor=1F1F1F)](https://github.com/cirreum/Cirreum.Runtime.Messaging/blob/main/LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-003D8F?style=flat-square&labelColor=1F1F1F)](https://dotnet.microsoft.com/)

**High-performance distributed messaging with policy-driven batching and observability for .NET applications**

## Overview

**Cirreum.Runtime.Messaging** composes the messaging stack for Cirreum server hosts and implements the Cirreum Distributed Messaging feature. A single `AddMessaging()` call does both:

- **Messaging services composition** — registers the configured messaging providers from `Cirreum:Messaging:Providers`, exposing each instance as a keyed `IMessagingClient` (queues, topics, subscriptions) with health checks and tracing. Apps can consume these clients directly for their own messaging workflows, with or without the distributed-messaging layer.
- **Distributed Messaging** — the runtime delivery engine for the `DistributedMessage` channel. The messaging *model* — `DistributedMessage`, the wire envelope, the registry, and the `IBatchingPolicy` strategy — ships in `Cirreum.Messaging.Distributed`; this package provides the moving parts: the outbound Conductor bridge, the policy-driven batching processor, the transport publisher, the inbound receiver, and OpenTelemetry metrics, offering both synchronous and batched delivery with built-in resilience.

## Distributed Messaging Key Features

### 🚀 Flexible Message Delivery
- **Dual-mode publishing**: Direct (synchronous) and background (batched) delivery, per message or by channel default
- **Transport abstraction**: Pluggable providers (Azure Service Bus included)
- **Message targeting**: Support for both queue-based events and topic-based notifications via `[DistributedMessageTarget]`

### 📦 Policy-Driven Batching System
- **Pluggable batch sizing**: each batch's capacity and fill window come from the channel's `IBatchingPolicy`, fed live queue-depth, send-rate, and error-rate observables; the default policy passes the configured base values through
- **Priority queuing**: High-priority messages with rate limiting and automatic age promotion
- **Circuit breaker**: Built-in fault tolerance for resilient message delivery

### 📊 Comprehensive Observability
- **OpenTelemetry integration**: Distributed tracing and metrics collection
- **Lifecycle tracking**: Monitor messages from receipt to delivery
- **Queue depth alerts**: Configurable thresholds for proactive monitoring
- **Performance metrics**: Detailed timing for queue and delivery operations

### ⚙️ Production-Ready
- **Thread-safe operations**: Designed for high-concurrency scenarios
- **Graceful shutdown**: Proper cleanup of background services
- **Health checks**: Transport-instance health monitoring (queue/topic validation, readiness participation) via the provider configuration
- **Structured logging**: Rich context for troubleshooting

### 📥 Inbound Message Dispatch *(added 1.1.0)*
- **Hosted receiver**: `DistributedMessageReceiver` consumes from queue and/or topic subscription concurrently
- **Conductor dispatch**: Handlers are standard `INotificationHandler<DistributedMessageReceived<T>>` — auto-discovered, scoped, pipeline-aware
- **Self-echo prevention**: `cirreum.node` application property + replica identity (`INodeIdProvider`) skip own publishes pre-deserialization
- **Cross-broker filterable metadata**: Four application properties (`cirreum.identifier`, `cirreum.version`, `cirreum.producer`, `cirreum.node`) stamped on every outbound message for broker-side subscription filtering
- **Multi-head ready**: Per-deployment `SubscriptionName` differentiates heads; same binary, different config; broker fan-outs messages to all heads

## Quick Start

### Installation

```bash
dotnet add package Cirreum.Runtime.Messaging
```

### Basic Setup

```csharp
var builder = DomainApplication.CreateBuilder(args);

// Registers the Service Bus provider, the delivery engine, the outbound
// Conductor bridge, and (when configured) the inbound receiver.
builder.AddMessaging();

var app = builder.Build();
await app.RunAsync();
```

### Direct `IMessagingClient` Usage

`AddMessaging()` registers every instance under `Cirreum:Messaging:Providers` as a **keyed** `IMessagingClient` — usable directly for app-owned queues and topics, with or without the distributed-messaging layer:

```csharp
public sealed class InvoiceQueueService(
	[FromKeyedServices("default")] IMessagingClient client) {

	public Task EnqueueAsync(Invoice invoice, CancellationToken ct) =>
		client.UseQueueSender("invoices.pending.v1")
			.PublishMessageAsync(OutboundMessage.AsJsonContent(invoice), ct);
}
```

The client surface (queue senders/receivers, topics, subscriptions, peek/defer/dead-letter) is defined by [`Cirreum.Messaging`](https://github.com/cirreum/Cirreum.Messaging) and implemented per broker by the provider package (e.g., [`Cirreum.Messaging.Azure`](https://github.com/cirreum/Cirreum.Messaging.Azure)) — see those packages for the full client reference. The [Choosing a Dispatch Path](#choosing-a-dispatch-path) section below covers when to use the client directly versus the distributed channel.

### Defining and Publishing Messages

Messages derive from `DistributedMessage`, declare their wire contract with `[MessageVersion]`, and optionally pick a routing target (topic is the default):

```csharp
[MessageVersion("orders.created", "1.0")]
[DistributedMessageTarget(MessageTarget.Queue)]
public sealed record OrderCreatedEvent(string OrderId) : DistributedMessage;
```

Publish through Conductor — the outbound bridge forwards any published `DistributedMessage` to the configured transport automatically:

```csharp
public sealed class OrderService(IPublisher publisher) {

	public async Task ProcessOrderAsync(Order order) {

		// Delivered per the channel default (direct or batched)
		await publisher.PublishAsync(new OrderCreatedEvent(order.Id));

		// Opt a specific message into batched background delivery
		await publisher.PublishAsync(new OrderCreatedEvent(order.Id) {
			UseBackgroundDelivery = true,
			Priority = DistributedMessagePriority.TimeSensitive
		});
	}
}
```

### Configuration

```json
{
  "Cirreum": {
	"Messaging": {
	  "Distributed": {
		"InstanceKey": "app-primary",
		"QueueName": "app-events",
		"TopicName": "app-notifications",
		"BackgroundDelivery": {
		  "UseBackgroundDeliveryByDefault": true,
		  "QueueCapacity": 1000,
		  "BatchCapacity": 10,
		  "BatchFillWaitTime": "00:00:00.0500000",
		  "CircuitBreakerThreshold": 5,
		  "CircuitResetTimeout": "00:01:00"
		},
		"Metrics": {
		  "QueueDepthWarningThreshold": 500,
		  "QueueDepthCriticalThreshold": 1000
		}
	  }
	}
  }
}
```

`InstanceKey` names the keyed `IMessagingClient` registration from the transport provider's `Cirreum:Messaging:Providers` configuration. See the [Configuration Guide](https://github.com/cirreum/Cirreum.Runtime.Messaging/blob/main/docs/CONFIGURATION.md) for every setting, defaults, and provider-instance configuration.

### Custom Batching Policy

The default `IBatchingPolicy` passes the configured base values through unchanged. Apps that want dynamic batching plug in a policy via the composition callback — either the framework-supplied day-of-week / time-of-day scaler:

```csharp
builder.AddMessaging(m => m.UseTimeOfDayBatching(schedule => {
	schedule.TimeZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
	schedule.Rules.Add(new() {
		Days = [DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday],
		StartHour = 16,
		EndHour = 23,
		ScalingFactor = 0.5, // high volume expected — halve the fill wait, send sooner
		Description = "Weekend evening spike"
	});
}));
```

or a fully custom policy (traffic-aware, queue-depth-aware, business-signal-aware):

```csharp
builder.AddMessaging(m => m.UseBatchingPolicy<TrafficAwareBatchingPolicy>());
```

Each batch, the processor calls `Evaluate(BatchingContext)` with the configured base values plus live observables (current queue depth, rolling send rate, rolling error rate) and applies the returned fill wait time and capacity.

### Consuming Inbound Messages *(added 1.1.0)*

Configure the receiver in appsettings:

```json
{
  "Cirreum": {
	"Messaging": {
	  "Distributed": {
		"Receiver": {
		  "InstanceKey": "app-primary",
		  "TopicName": "app.notifications.v1",
		  "SubscriptionName": "api-head",
		  "MaxConcurrency": 1
		}
	  }
	}
  }
}
```

Implement handlers using the standard Conductor notification pattern — auto-discovered, no registration boilerplate.

The framework wraps every inbound message in `DistributedMessageReceived<TMessage>` which carries both the typed payload (`Message`) and the original wire envelope (`Envelope`) so handlers can inspect wire-level metadata without re-deserializing or threading additional context:

```csharp
using Cirreum.Conductor;
using Cirreum.Messaging;
using Microsoft.Extensions.Logging;

public sealed class EvidenceInstanceChangeHandler(
	IEvidenceInstanceRegistry registry,
	ILogger<EvidenceInstanceChangeHandler> logger
) : INotificationHandler<DistributedMessageReceived<EvidenceInstanceChangedV1>> {
	public Task HandleAsync(
		DistributedMessageReceived<EvidenceInstanceChangedV1> notification,
		CancellationToken ct) {
		
		// The typed payload — strongly typed to the wrapped TMessage.
		var change = notification.Message;

		// The original wire envelope — wire-level metadata for audit, telemetry,
		// latency calculations, or replay detection.
		var envelope = notification.Envelope;

		logger.LogInformation(
			"Evidence instance {Key} changed (op={Operation}). "
			+ "From producer={Producer}, published={PublishedAt}, version={Version}.",
			change.Key,
			change.Operation,
			envelope.ProducerId,
			envelope.PublishedAt,
			envelope.MessageVersion);

		return registry.ApplyRemoteChangeAsync(change.Operation, change.Key, ct);
	}
}
```

The envelope properties available to every handler:

| Property | Purpose |
|---|---|
| `MessageIdentifier` | The stable wire-level identifier (e.g., `"auth.evidence.changed"`) |
| `MessageVersion` | The version string (e.g., `"1"`) |
| `MessageType` | The assembly-hinted .NET type name of the payload — **diagnostic metadata only** (logging / dead-letter triage); inbound resolution goes through the registry by `(MessageIdentifier, MessageVersion)`, never this string |
| `ProducerId` | Head/app identity that published — useful for audit |
| `PublishedAt` | UTC timestamp captured at envelope creation — useful for latency metrics or replay detection (nullable for envelopes from older senders) |

See the [Configuration Guide](https://github.com/cirreum/Cirreum.Runtime.Messaging/blob/main/docs/CONFIGURATION.md) for the receiver's full settings and queue-vs-subscription semantics.

## Choosing a Dispatch Path

The value of the framework path is **distribution with no setup beyond your send config**: mark a type `[MessageVersion]` `: DistributedMessage`, point the `Distributed` section at a transport instance, and `IPublisher.PublishAsync(msg)` fans it out — Cirreum owns the envelope, serialization, routing, and delivery. A **consumer** is just as light: another app references the Domain that defines the message type, adds `Cirreum.Runtime.Messaging`, configures a receiver, and writes an `INotificationHandler<DistributedMessageReceived<T>>` — no shared broker code, no hand-rolled envelopes. And because each type carries a stable `[MessageVersion]` identity (an identifier plus a schema version), the contract can **version over time by attribute alone** — the identifier is the durable wire key, the version rides the envelope.

`[MessageVersion]` + `DistributedMessage` define a *wire contract*; publishing through Conductor is one *transport* for it. Three patterns are valid — and the choice is **queue topology and ownership, not how much load you expect**:

| Pattern | Wire contract | Routing & consumption | Right for |
|---|---|---|---|
| **Full framework** | `[MessageVersion]` + `DistributedMessage` | `IPublisher.PublishAsync()` → the channel's configured queue/topic; inbound via Conductor handlers | Any event you want distributed with **zero transport code** — convergence, registry sync, kill switches, *or* a high-traffic domain event — all sharing the channel's one configured queue/topic |
| **App-routed, framework-formatted** | `[MessageVersion]` + `DistributedMessage` | App-built envelope via `IMessagingClient` to app-chosen queues; app-owned consumer loops | A workflow that wants its **own** queue — independent scaling/concurrency, tuning, and DLQ isolation from other framework eventing (email, payments, document processing) |
| **Fully bespoke** | Ad-hoc message classes | Raw `IMessagingClient` end-to-end | Legacy integration, external broker conventions, extreme tuning |

The framework path funnels everything through the channel's single configured queue/topic — deliberately. It carries whatever volume you put on it; you reach for pattern 2 when a stream wants **isolation and independent tuning** (its own queue, concurrency, and DLQ), not because it's "too big" for the framework. The patterns also aren't exclusive — they're independent stacks: run framework Distributed Messaging on one provider (say Azure Service Bus) *and* stand up your own separate `IMessagingClient` on another (AWS, etc.) for bespoke workflows. When a workflow needs its own queue, keep Cirreum's envelope vocabulary (stable identifier + version, producer id, publish timestamp) and route it yourself (pattern 2):

```csharp
var envelope = DistributedMessageEnvelope.Create(order, definition, producerId);
await messagingClient
	.UseQueueSender("orders.processing.v1")
	.PublishMessageAsync(OutboundMessage.AsJsonContent(envelope).WithSubject("orders.created.v1.0"));
```

A consumer on that queue knows the concrete type it expects, so it re-materializes the payload with `envelope.DeserializeMessage<T>()`, and audit/observability tooling reads the same envelope shape across all three patterns.

> ⚠️ **Built for crossing the app boundary — not for fanning out to your own replicas.** Replicas of a single deployment share one subscription, so they are *competing consumers*: exactly one replica processes each message (work-stealing), and the publishing replica skips its own copy via self-echo. Distinct **heads** (different `SubscriptionName`) each receive a copy; replicas *within* a head do not. So `DistributedMessage` is for **inter-dependent apps** — App A publishes, App B consumes — not for notifying every node of the *same* app. For an in-process or same-node reaction, publish a **plain notification** (a type that is *not* `: DistributedMessage`) and handle it with `INotificationHandler<T>`; reserve `DistributedMessage` for work that must leave the process.

## Where Handlers Live — Contracts in the Domain, Handlers in the App

Conductor discovers handlers by **assembly scan**, so a handler's *project* is its *deployment scope*. In a shared-Domain shop — one Domain referenced by an API, an ACA Job, and a Function app — a handler placed in the shared Domain is registered, and latent, in **every** deployable. That is rarely what you want.

The principle:

- **The event type is the shared contract.** A `[MessageVersion]`-tagged `: DistributedMessage` record is the wire vocabulary every deployable agrees on — it lives in the **Domain**.
- **Handlers live in the app that should run them.** Put a handler in the API project and only the API runs it; put it in the Job and only the Job does.

Two handler shapes express *when* a reaction runs:

| Handler | Fires | Use for |
|---|---|---|
| `INotificationHandler<TEvent>` | Locally, **at publish** — the reaction "comes home" in the same process that published | A side effect the publishing app itself must perform |
| `INotificationHandler<DistributedMessageReceived<TEvent>>` | **On receipt** from the wire, in a consuming replica | "Process this only remotely" — the receiving app reacts, the publisher does not |

```
Solution/
├─ MyApp.Domain/                 # shared — referenced by every deployable
│   └─ Events/OrderPlaced.cs     #   the [MessageVersion] : DistributedMessage contract ONLY
├─ MyApp.Api/
│   └─ Handlers/PlaceOrderHandler.cs        # INotificationHandler<DistributedMessageReceived<OrderPlaced>> — reacts on receipt
└─ MyApp.FulfillmentJob/
	└─ Handlers/ReserveStockHandler.cs      # its own handler, its own deployment scope
```

**The one deliberate exception:** a genuinely cross-cutting local reaction that *every* deployment must perform (say, an audit write on receipt) may live in the Domain as a raw-`TEvent` handler — precisely because you *want* it registered everywhere. Reach for it knowingly, not by accident.

## Documentation

- [Configuration Guide](https://github.com/cirreum/Cirreum.Runtime.Messaging/blob/main/docs/CONFIGURATION.md) — every channel, background-delivery, receiver, metrics, and provider setting with defaults
- [Migration Guide (v1 → v2)](https://github.com/cirreum/Cirreum.Runtime.Messaging/blob/main/docs/MIGRATION-v2.md) — breaking changes and the find/replace table for the 2.0 foundation-reset release
- [Changelog](https://github.com/cirreum/Cirreum.Runtime.Messaging/blob/main/docs/CHANGELOG.md)

## Contribution Guidelines

1. **Be conservative with new abstractions**  
   The API surface must remain stable and meaningful.

2. **Limit dependency expansion**  
   Only add foundational, version-stable dependencies.

3. **Favor additive, non-breaking changes**  
   Breaking changes ripple through the entire ecosystem.

4. **Include thorough unit tests**  
   All primitives and patterns should be independently testable.

5. **Document architectural decisions**  
   Context and reasoning should be clear for future maintainers.

6. **Follow .NET conventions**  
   Use established patterns from Microsoft.Extensions.* libraries.

## Versioning

Cirreum.Runtime.Messaging follows [Semantic Versioning](https://semver.org/):

- **Major** - Breaking API changes
- **Minor** - New features, backward compatible
- **Patch** - Bug fixes, backward compatible

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

---

**Cirreum Foundation Framework**  
*Layered simplicity for modern .NET*