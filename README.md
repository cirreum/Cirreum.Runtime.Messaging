# Cirreum.Runtime.Messaging

[![NuGet Version](https://img.shields.io/nuget/v/Cirreum.Runtime.Messaging.svg?style=flat-square&labelColor=1F1F1F&color=003D8F)](https://www.nuget.org/packages/Cirreum.Runtime.Messaging/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/Cirreum.Runtime.Messaging.svg?style=flat-square&labelColor=1F1F1F&color=003D8F)](https://www.nuget.org/packages/Cirreum.Runtime.Messaging/)
[![GitHub Release](https://img.shields.io/github/v/release/cirreum/Cirreum.Runtime.Messaging?style=flat-square&labelColor=1F1F1F&color=FF3B2E)](https://github.com/cirreum/Cirreum.Runtime.Messaging/releases)
[![License](https://img.shields.io/badge/license-MIT-F2F2F2?style=flat-square&labelColor=1F1F1F)](https://github.com/cirreum/Cirreum.Runtime.Messaging/blob/main/LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-003D8F?style=flat-square&labelColor=1F1F1F)](https://dotnet.microsoft.com/)

**High-performance distributed messaging with policy-driven batching and observability for .NET applications**

## Overview

**Cirreum.Runtime.Messaging** is the runtime delivery engine for Cirreum's distributed-messaging channel. The messaging *model* — `DistributedMessage`, the wire envelope, the registry, and the `IBatchingPolicy` strategy — ships in `Cirreum.Messaging.Distributed`; this package provides the moving parts: the outbound Conductor bridge, the batching processor, the transport publisher, the inbound receiver, and OpenTelemetry metrics. It offers both synchronous and asynchronous message delivery patterns with built-in resilience and monitoring capabilities.

## Key Features

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
- **Health checks**: Integration with ASP.NET Core health monitoring
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

`InstanceKey` names the keyed `IMessagingClient` registration from the transport provider's `Cirreum:Messaging:Providers` configuration.

### Custom Batching Policy

The default `IBatchingPolicy` passes the configured base values through unchanged. Apps that want dynamic batching (traffic-aware, queue-depth-aware, time-of-day) register their own policy before `AddMessaging()`:

```csharp
builder.Services.AddSingleton<IBatchingPolicy, TrafficAwareBatchingPolicy>();
builder.AddMessaging();
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
| `MessageType` | The full .NET type name of the payload |
| `ProducerId` | Head/app identity that published — useful for audit |
| `PublishedAt` | UTC timestamp captured at envelope creation — useful for latency metrics or replay detection (nullable for envelopes from older senders) |

See [`docs/RELEASE-NOTES-v1.1.0.md`](docs/RELEASE-NOTES-v1.1.0.md) for the routing convention, multi-head topology, and operational notes.

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