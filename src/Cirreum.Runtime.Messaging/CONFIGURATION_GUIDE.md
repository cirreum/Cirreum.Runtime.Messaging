# Messaging Configuration Guide

```json
"Cirreum": {
	"Messaging": {
		"DistributedMessaging": {
			"Sender": {
				"InstanceKey": "default", // reference a messaging provider instance
				"QueueName": "app.events.v1",
				"TopicName": "app.notifications.v1",
				"BackgroundDelivery": {
					"UseBackgroundDeliveryByDefault": true,
					"QueueCapacity": 1000,
					"PriorityMessageRateLimit": 100,
					"PriorityAgePromotionThreshold": 60,
					"CircuitBreakerThreshold": 5,
					"CircuitResetTimeout": "00:01:00",
					"BatchCapacity": 20,
					"BatchFillWaitTime": "00:00:10",
					"ActiveTimeBatchingProfile": "EventWeekend",
					"TimeBatchingProfiles": {
						"EventWeekend": {
							"Name": "Event Weekend",
							"DefaultScalingFactor": 1.5,
							"Rules": [
								{
									"Days": [ 5, 6, 0 ], // Friday, Saturday and Sunday
									"StartHour": 0,
									"EndHour": 24,
									"ScalingFactor": 0.5,
									"Description": "Weekend scaling - high volume expected"
								},
								{
									"Days": [ 1, 2, 3, 4 ], // Monday through Thursday
									"StartHour": 16,
									"EndHour": 22,
									"ScalingFactor": 0.8,
									"Description": "Evening registration spike (4 PM - 10 PM)"
								}
							]
						}
					}
				}
			},
			"Metrics": {
				"EnablePeriodicReporting": true,
				"ReportingInterval": "00:05:00",
				"IncludeDetailedReporting": true,
				"LogDetailedMetrics": true,
				"QueueDepthWarningThreshold": 500,
				"QueueDepthCriticalThreshold": 1000
			}
		},
		"Providers": {
			"Azure": {
				"Tracing": true,
				"Instances": {
					"default": { // 'default', or Name, Key used as the service key in DI (should be lowercase)
						"Name": "corr-messaging-servicebus", // use look up connection setting in Azure Key Vault
						"HealthChecks": true,
						"HealthOptions": {
							"IncludeInReadinessCheck": true,
							"CachedResultTimeout": 60, // in seconds, default 60 - null or 0 disables caching
							"FailureStatus": 1,
							"DefaultMessageTtl": "00:05:00",
							"Queues": [
								{
									"QueueName": "corr.events.v1",
									"MessageTtl": "00:00:30",
									"ValidateSend": true,
									"ValidateReceive": true,
									"TestMessageContent": "distributed-health-event"
								}
							],
							"Topics": [
								{
									"TopicName": "corr.notifications.v1",
									"MessageTtl": "00:01:00",
									"ValidateSend": true,
									"TestMessageContent": "distributed-health-notification"
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

## Background Delivery Configuration

The `BackgroundDelivery` section controls how messages are delivered to the messaging infrastructure when the message is delivered in the background:

```json
"BackgroundDelivery": {
  "UseBackgroundDeliveryByDefault": true,
  "QueueCapacity": 1000,
  "BatchCapacity": 10,
  "BatchingInterval": "00:00:00.050"
}
```

## Important Notes:

- All BackgroundDelivery settings must be configured, even if you set UseBackgroundDeliveryByDefault to false. This is because individual messages may still opt into background delivery.
- UseBackgroundDeliveryByDefault: Controls the default delivery mode when messages don't specify a preference.

	- true: Messages are queued and processed in the background by default
	- false: Messages are processed synchronously by default (stronger consistency)


- QueueCapacity: Maximum number of messages that can be queued (default: 1000)

	- Higher values handle traffic spikes better but require more memory


- BatchCapacity: Maximum messages to process in a batch (default: 10)

	- Higher values improve throughput but may increase latency


- BatchingInterval: Maximum time to wait for a batch to be filled before sending (default: 50ms)

	- Lower values reduce latency but may decrease throughput

## Common Configurations:

- High-throughput configuration:

```json
{
  "BackgroundDelivery": {
	"UseBackgroundDeliveryByDefault": true,
	"QueueCapacity": 5000,
	"BatchCapacity": 50,
	"BatchingInterval": "00:00:00.100"
  }
}
```

- Low Latency

```json
{
  "BackgroundDelivery": {
	"UseBackgroundDeliveryByDefault": false,
	"QueueCapacity": 1000,
	"BatchCapacity": 5,
	"BatchingInterval": "00:00:00.020"
  }
}
```

- Large Batches

```json
{
  "BackgroundDelivery": {
	"UseBackgroundDeliveryByDefault": true,
	"QueueCapacity": 2000,
	"BatchCapacity": 200,
	"BatchingInterval": "00:00:00.500"
  }
}
```

## Metrics Configuration

```json
"Metrics": {
	"EnablePeriodicReporting": true,
	"ReportingInterval": "00:05:00",
	"IncludeDetailedReporting": false,
	"LogDetailedMetrics": false,
	"QueueDepthWarningThreshold": 500,
	"QueueDepthCriticalThreshold": 1000
}
```