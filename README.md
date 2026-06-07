# Cirreum.Runtime.Messaging

[![NuGet Version](https://img.shields.io/nuget/v/Cirreum.Runtime.Messaging.svg?style=flat-square&labelColor=1F1F1F&color=003D8F)](https://www.nuget.org/packages/Cirreum.Runtime.Messaging/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/Cirreum.Runtime.Messaging.svg?style=flat-square&labelColor=1F1F1F&color=003D8F)](https://www.nuget.org/packages/Cirreum.Runtime.Messaging/)
[![GitHub Release](https://img.shields.io/github/v/release/cirreum/Cirreum.Runtime.Messaging?style=flat-square&labelColor=1F1F1F&color=FF3B2E)](https://github.com/cirreum/Cirreum.Runtime.Messaging/releases)
[![License](https://img.shields.io/github/license/cirreum/Cirreum.Runtime.Messaging?style=flat-square&labelColor=1F1F1F&color=F2F2F2)](https://github.com/cirreum/Cirreum.Runtime.Messaging/blob/main/LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-003D8F?style=flat-square&labelColor=1F1F1F)](https://dotnet.microsoft.com/)

**High-performance distributed messaging with advanced batching and observability for .NET applications**

## Overview

**Cirreum.Runtime.Messaging** provides a sophisticated distributed messaging infrastructure for .NET applications, featuring intelligent batching, priority-based delivery, and comprehensive observability. Built on the Cirreum Foundation Framework, it offers both synchronous and asynchronous message delivery patterns with built-in resilience and monitoring capabilities.

## Key Features

### 🚀 Flexible Message Delivery
- **Dual-mode publishing**: Direct (synchronous) and background (asynchronous) delivery
- **Transport abstraction**: Pluggable providers (Azure Service Bus included)
- **Message targeting**: Support for both queue-based events and topic-based notifications

### 📦 Advanced Batching System
- **Dynamic batch sizing**: Automatically adjusts based on load and time profiles
- **Priority queuing**: High-priority messages with automatic promotion
- **Circuit breaker**: Built-in fault tolerance for resilient message delivery
- **Configurable profiles**: Peak and off-peak batching strategies

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
var builder = Host.CreateApplicationBuilder(args);

// Add distributed messaging with metrics
builder.AddDistributedMessaging()
	   .AddDistributedMessagingMetrics();

// Add Azure Service Bus as the transport provider
builder.AddAzureServiceBusProvider();

var host = builder.Build();
await host.RunAsync();
```

### Publishing Messages

```csharp
public class OrderService
{
	private readonly IDistributedMessagePublisher _publisher;

	public OrderService(IDistributedMessagePublisher publisher)
	{
		_publisher = publisher;
	}

	public async Task ProcessOrderAsync(Order order)
	{
		// Publish directly (synchronous)
		await _publisher.PublishAsync(new OrderCreatedEvent(order.Id));

		// Publish in background (batched)
		await _publisher.PublishInBackgroundAsync(
			new OrderNotification(order.Id), 
			DistributedMessagePriority.Normal);
	}
}
```

### Configuration

```json
{
  "DistributedMessaging": {
	"BackgroundDelivery": {
	  "Enabled": true,
	  "MaxBatchSize": 100,
	  "MaxQueueSize": 10000,
	  "DeliveryTimeout": "00:00:30",
	  "CircuitBreaker": {
		"FailureThreshold": 5,
		"BreakDuration": "00:01:00"
	  }
	},
	"Metrics": {
	  "Enabled": true,
	  "QueueDepthAlertThreshold": 1000,
	  "AlertSuppressionPeriod": "00:05:00"
	}
  }
}
```

### Consuming Inbound Messages *(added 1.1.0)*

Configure the receiver in appsettings:

```json
{
  "Cirreum": {
	"Messaging": {
	  "Distribution": {
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
| `PublishedAt` | UTC timestamp captured at envelope creation — useful for latency metrics or replay detection (nullable for envelopes from senders predating `Cirreum.Core 5.2.0`) |

See [`docs/RELEASE-NOTES-v1.1.0.md`](docs/RELEASE-NOTES-v1.1.0.md) for the routing convention, multi-head topology, and operational notes.

## Documentation

- [Configuration Guide](CONFIGURATION_GUIDE.md) - Detailed configuration options and examples
- [API Documentation](https://docs.cirreum.com/runtime/messaging) - Complete API reference
- [Architecture Overview](https://docs.cirreum.com/runtime/messaging/architecture) - Design decisions and patterns

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