# Cirreum.Runtime.Messaging v2 → v3 Migration

## Why v3

`Cirreum.Kernel` 2.0.0 renames Conductor's publish/subscribe markers — `INotification` →
`IDomainEvent`, `INotificationHandler<T>` → `IDomainEventHandler<T>` — because Cirreum used
"notification" for two unrelated concepts: in-application publish/subscribe, and the human-facing
state family a client binds to in order to show a person something.

This package's outbound bridge and its documented consumer pattern are both expressed in those
markers, so it follows. Nothing about delivery changes: no wire-format change, no configuration
change, no behavioral change.

## Breaking Changes — Find/Replace Table

| Before | After |
|---|---|
| `INotificationHandler<DistributedMessageReceived<T>>` | `IDomainEventHandler<DistributedMessageReceived<T>>` |
| `INotificationHandler<TEvent>` (local reaction) | `IDomainEventHandler<TEvent>` |
| `HandleAsync(notification, …)` | `HandleAsync(domainEvent, …)` |

## Migration Walkthrough

Only consumers change, and only their interface:

```csharp
// Before
public sealed class EvidenceChangedConsumer
	: INotificationHandler<DistributedMessageReceived<EvidenceInstanceChangedV1>> {

	public Task HandleAsync(
		DistributedMessageReceived<EvidenceInstanceChangedV1> notification,
		CancellationToken cancellationToken) {
		var change = notification.Message;
		var envelope = notification.Envelope;
	}
}

// After
public sealed class EvidenceChangedConsumer
	: IDomainEventHandler<DistributedMessageReceived<EvidenceInstanceChangedV1>> {

	public Task HandleAsync(
		DistributedMessageReceived<EvidenceInstanceChangedV1> domainEvent,
		CancellationToken cancellationToken) {
		var change = domainEvent.Message;
		var envelope = domainEvent.Envelope;
	}
}
```

Handlers are still auto-discovered with no registration boilerplate. The publish side —
`[MessageVersion]`, `: DistributedMessage`, `IPublisher.PublishAsync(msg)` — is unchanged.

The two-handler distinction the README documents still holds, with the vocabulary updated:

| Handler | Runs |
|---|---|
| `IDomainEventHandler<TEvent>` | locally, at publish — the reaction comes home in the publishing process |
| `IDomainEventHandler<DistributedMessageReceived<TEvent>>` | on receipt from the wire, in a consuming replica |

## What Didn't Change

- The wire format, envelope, self-echo prevention, and `cirreum.node` stamping
- Every `Cirreum:Messaging:*` configuration key, including `Distributed`, `BackgroundDelivery`,
  `Receiver`, and `Metrics`
- Batching, prioritization, circuit breaking, and `IBatchingPolicy`
- The messaging metric names and the `Cirreum.Messaging` `ActivitySource`
- `AddMessaging()` and its fluent composition

**Rolling upgrades are safe.** A v2 publisher and a v3 consumer are wire-compatible in both
directions — this release changes no bytes on the broker, so the two can run side by side during a
deployment.

## Downstream Package Impact

Applications with distributed-message consumers need the one-line interface change per handler.
Applications that only publish need a re-pin.
