# Cirreum.Runtime.Messaging 2.2.0 — Conductor Marker Rename

`Cirreum.Kernel` 2.0.0 renames Conductor's publish/subscribe markers — `INotification` →
`IDomainEvent`, `INotificationHandler<T>` → `IDomainEventHandler<T>`. Cirreum used "notification"
for two unrelated concepts: in-application publish/subscribe, and the human-facing state family a
client binds to in order to show a person something. `IDomainEvent` names the first for what it is;
"notification" now refers only to the second.

This package follows. **Nothing about delivery changes:** no wire-format change, no configuration
change, no behavioral change.

## Why this is a minor, not a major

This package's own public surface is unchanged. Every reference to the renamed markers here is
inside an `internal sealed` type or in an XML doc comment — `DistributedMessage`,
`[MessageVersion]`, `IPublisher.PublishAsync`, `AddMessaging()` and the batching seam are all
untouched.

The break your consumers hit belongs to `Cirreum.Kernel` / `Cirreum.Contracts`, which went major
2.0.0 to signal exactly that. You cannot take this release without those, and their majors are the
honest warning.

**That said, your consumer classes do need a one-line edit each** — because they name this package's
`DistributedMessageReceived<T>` alongside the renamed marker, that combination appears in no other
package's migration guide. It is written out below.

## What you change

Only consumers, and only their interface:

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

| Before | After |
|---|---|
| `INotificationHandler<DistributedMessageReceived<T>>` | `IDomainEventHandler<DistributedMessageReceived<T>>` |
| `INotificationHandler<TEvent>` (local reaction) | `IDomainEventHandler<TEvent>` |
| `HandleAsync(notification, …)` | `HandleAsync(domainEvent, …)` |

Handlers are still auto-discovered with no registration boilerplate. The publish side —
`[MessageVersion]`, `: DistributedMessage`, `IPublisher.PublishAsync(msg)` — is unchanged.

The two-handler distinction the README documents still holds, with the vocabulary updated:

| Handler | Runs |
|---|---|
| `IDomainEventHandler<TEvent>` | locally, at publish — the reaction comes home in the publishing process |
| `IDomainEventHandler<DistributedMessageReceived<TEvent>>` | on receipt from the wire, in a consuming replica |

> **Do not run a project-wide find/replace of "Notification".** `INotificationState` and
> `IScopedNotificationState` **keep their names** — they are the human-facing concept, and
> preserving that separation is the entire point of the rename.

## What didn't change

- The wire format, envelope, self-echo prevention, and `cirreum.node` stamping
- Every `Cirreum:Messaging:*` configuration key, including `Distributed`, `BackgroundDelivery`,
  `Receiver`, and `Metrics`
- Batching, prioritization, circuit breaking, and `IBatchingPolicy`
- The messaging metric names and the `Cirreum.Messaging` `ActivitySource`
- `AddMessaging()` and its fluent composition

**Rolling upgrades are safe.** A 2.1.x publisher and a 2.2.0 consumer are wire-compatible in both
directions — this release changes no bytes on the broker, so the two can run side by side during a
deployment.

## Downstream package impact

Applications with distributed-message consumers need the one-line interface change per handler.
Applications that only publish need a re-pin.

## See also

- [`CHANGELOG.md`](CHANGELOG.md)
