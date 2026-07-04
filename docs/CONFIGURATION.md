# Messaging Configuration Guide

All distributed-messaging configuration lives under `Cirreum:Messaging`. The `Distributed` section configures the channel (binding `DistributedMessagingOptions` from `Cirreum.Messaging.Distributed`); the `Providers` section configures the transport instances the channel references by key.

A complete example:

```json
"Cirreum": {
	"Messaging": {
		"Distributed": {
			"InstanceKey": "default", // references a messaging provider instance
			"QueueName": "app.events.v1",
			"TopicName": "app.notifications.v1",
			"BackgroundDelivery": {
				"UseBackgroundDeliveryByDefault": true,
				"QueueCapacity": 1000,
				"BatchCapacity": 20,
				"BatchFillWaitTime": "00:00:00.050",
				"PriorityMessageRateLimit": 100,
				"PriorityAgePromotionThreshold": 60,
				"CircuitBreakerThreshold": 5,
				"CircuitResetTimeout": "00:01:00"
			},
			"Receiver": {
				"InstanceKey": "default",
				"TopicName": "app.notifications.v1",
				"SubscriptionName": "api-head",
				"MaxConcurrency": 1,
				"GracefulShutdownTimeout": "00:00:30"
			},
			"Metrics": {
				"EnablePeriodicReporting": true,
				"ReportingInterval": "00:05:00",
				"IncludeDetailedReporting": true,
				"LogDetailedMetrics": false,
				"QueueDepthWarningThreshold": 500,
				"QueueDepthCriticalThreshold": 1000
			}
		},
		"Providers": {
			"Azure": {
				"Tracing": true,
				"Instances": {
					"default": { // the key becomes the keyed-DI instance key (lowercase)
						"Name": "app-messaging-servicebus", // connection looked up by name (e.g., Key Vault)
						"HealthChecks": true,
						"HealthOptions": {
							"IncludeInReadinessCheck": true,
							"CachedResultTimeout": 60, // seconds; null or 0 disables caching
							"FailureStatus": 1,
							"DefaultMessageTtl": "00:05:00",
							"Queues": [
								{
									"QueueName": "app.events.v1",
									"MessageTtl": "00:00:30",
									"ValidateSend": true,
									"ValidateReceive": true,
									"CheckMessageContent": "distributed-health-event"
								}
							],
							"Topics": [
								{
									"TopicName": "app.notifications.v1",
									"MessageTtl": "00:01:00",
									"ValidateSend": true,
									"CheckMessageContent": "distributed-health-notification"
								}
							]
						}
					}
				}
			}
		}
	}
}
```

## Channel Settings (`Distributed`)

| Setting | Purpose |
|---|---|
| `InstanceKey` | The provider instance key the channel publishes through. **Required** — the delivery engine, batch processor, and outbound bridge only register when it's present. |
| `QueueName` | Destination for messages decorated `[DistributedMessageTarget(MessageTarget.Queue)]`. |
| `TopicName` | Destination for topic-routed messages (the default when no target attribute is present). |

## Background Delivery (`Distributed:BackgroundDelivery`)

Controls batched (asynchronous) delivery. Every setting has a working default — configure only what you need to tune. The section applies even when `UseBackgroundDeliveryByDefault` is `false`, because individual messages may still opt in via `DistributedMessage.UseBackgroundDelivery`.

| Setting | Default | Purpose |
|---|---|---|
| `UseBackgroundDeliveryByDefault` | `false` | Delivery mode when a message doesn't specify a preference. `true` = queue and batch in the background; `false` = send synchronously (stronger consistency). |
| `QueueCapacity` | `1000` | Maximum buffered messages awaiting send. Higher absorbs traffic spikes at the cost of memory; writers wait when full. |
| `BatchCapacity` | `10` | Base maximum messages per outbound batch. Higher improves throughput, may increase latency. |
| `BatchFillWaitTime` | `50ms` | Base maximum wait for a batch to fill before sending it incomplete. Lower reduces latency, may decrease throughput. |
| `PriorityMessageRateLimit` | `100` | Max `TimeSensitive`/`SystemHealth` messages per minute before excess downgrades to `Standard` (prevents priority inflation). |
| `PriorityAgePromotionThreshold` | `60` | Seconds a buffered message waits before its effective priority promotes one level (prevents starvation). `0` disables. |
| `CircuitBreakerThreshold` | `5` | Consecutive send failures before the circuit opens and the dispatcher pauses. |
| `CircuitResetTimeout` | `1m` | How long the circuit stays open before retrying. |

`BatchCapacity` and `BatchFillWaitTime` are *base* values: each batch, the channel's `IBatchingPolicy` receives them along with live observables (current queue depth, rolling send rate, rolling error rate) and returns the values actually used. The default policy passes the base values through unchanged; apps wanting dynamic behavior (traffic-aware, queue-depth-aware, time-of-day) register their own `IBatchingPolicy` singleton **before** calling `AddMessaging()` — dynamic batching is a code concern, not an appsettings one.

### Common Configurations

High-throughput:

```json
"BackgroundDelivery": {
	"UseBackgroundDeliveryByDefault": true,
	"QueueCapacity": 5000,
	"BatchCapacity": 50,
	"BatchFillWaitTime": "00:00:00.100"
}
```

Low latency (direct by default; opted-in messages still batch quickly):

```json
"BackgroundDelivery": {
	"UseBackgroundDeliveryByDefault": false,
	"QueueCapacity": 1000,
	"BatchCapacity": 5,
	"BatchFillWaitTime": "00:00:00.020"
}
```

Large batches:

```json
"BackgroundDelivery": {
	"UseBackgroundDeliveryByDefault": true,
	"QueueCapacity": 2000,
	"BatchCapacity": 200,
	"BatchFillWaitTime": "00:00:00.500"
}
```

## Receiver (`Distributed:Receiver`)

Optional — presence of a valid section registers the inbound `DistributedMessageReceiver` hosted service; absence means the process is send-only on this channel. Requires `InstanceKey` plus at least one source: a `QueueName` (competing consumers — one consumer processes each message) and/or a `TopicName` + `SubscriptionName` pair (broadcast — each subscription receives a copy; use a unique `SubscriptionName` per deployed head).

| Setting | Default | Purpose |
|---|---|---|
| `InstanceKey` | — | The provider instance to consume from. Required. |
| `QueueName` | — | Queue source (work distribution). |
| `TopicName` + `SubscriptionName` | — | Subscription source (cross-head event reactions). |
| `MaxConcurrency` | `1` | Concurrent in-flight handler invocations per source. `1` preserves FIFO ordering. |
| `GracefulShutdownTimeout` | `30s` | Wait for in-flight handlers on shutdown before cancelling. |

## Metrics (`Distributed:Metrics`)

| Setting | Default | Purpose |
|---|---|---|
| `EnablePeriodicReporting` | `true` | Periodic summary logging of totals. |
| `ReportingInterval` | `5m` | Period between summary reports. |
| `IncludeDetailedReporting` | `true` | Per-message-type statistics in periodic reports. |
| `LogDetailedMetrics` | `false` | Per-operation debug logging (queued/dequeued/delivered). |
| `QueueDepthWarningThreshold` | `500` | Buffered-queue depth that logs a warning. |
| `QueueDepthCriticalThreshold` | `1000` | Depth that logs critical and publishes a `QueueDepthAlertMessage` through the channel. |

OpenTelemetry metrics and traces are always emitted under the `Cirreum.Messaging` meter/activity source; these options only control the additional log-based reporting.

## Providers (`Providers:Azure`)

Standard Cirreum service-provider configuration (from `Cirreum.Messaging.Azure`): `Tracing` toggles ActivitySource registration; each entry under `Instances` becomes a keyed `IMessagingClient` registration whose key is the instance name (referenced by the channel's and receiver's `InstanceKey`). Per instance: `Name` identifies the connection (resolved via configuration/Key Vault), `HealthChecks` opts into health monitoring, and `HealthOptions` tunes queue/topic/subscription validation (`Queues`, `Topics`, and `Subscriptions` arrays), result caching, and readiness participation.
