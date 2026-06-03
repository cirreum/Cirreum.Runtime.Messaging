# Cirreum.Runtime.Messaging 1.1.0 — Inbound distributed message dispatch

Completes the inbound side of the Cirreum distributed messaging family. The hosted receiver consumes messages from a configured queue and/or topic subscription, deserializes them, and dispatches them through Conductor — handlers are ordinary `INotificationHandler<DistributedMessageReceived<T>>` implementations, auto-discovered the same way every other Cirreum notification handler is.

The abstractions consumed by this release shipped in `Cirreum.Core 5.2.0`: `INodeIdProvider`, `DefaultNodeIdProvider`, `DistributedMessageReceived<TMessage>`, `ReceiverOptions`, and `DistributedMessageEnvelope.PublishedAt`. This package wires those into a runtime that apps can opt into via appsettings, with sender-side application-property enrichment that makes broker-side routing and self-echo filtering possible.

Strictly additive on the send side. New receiver registration is opt-in via configuration.

---

## Why this release exists

`Cirreum.Runtime.Messaging` has shipped a rich send pipeline since `1.0.x` — direct vs background delivery, priority queuing with promotion, circuit breaker, time-of-day batching profiles, OpenTelemetry traces and metrics. The receive side, by contrast, was a bring-your-own-loop story: apps wrote their own subscription consumers, deserialized envelopes manually, and routed via `if (envelope.MessageIdentifier == "...") { ... }` ladders. That works for one or two message types in one head; it doesn't scale to multi-head deployments where each of API / Email / IVA needs to consume head-specific messages plus broadcast messages, and where multi-replica deployments need to converge on dynamic state without each replica processing its own publishes.

This release adds the symmetric receive infrastructure:

- A single `DistributedMessageReceiver` hosted service that handles the subscribe loop, envelope deserialization, type resolution, Conductor dispatch, and broker acknowledgment semantics.
- Self-echo prevention via a new `cirreum.node` application property on outbound messages and a pre-deserialization check on inbound.
- Three additional application properties (`cirreum.identifier`, `cirreum.version`, `cirreum.producer`) that enable broker-side subscription filtering without inventing a Cirreum-specific routing concept.

Apps writing receivers no longer write any subscribe / deserialize / dispatch code. They write per-(message-type + version) handler classes and drop them into the DI container; Conductor finds them; the receiver invokes them.

---

## What's new

### `DistributedMessageReceiver` (hosted service)

Single `IHostedService` per process. At startup it inspects the bound `ReceiverOptions` and spawns one consumer loop per configured source:

- **Queue loop** — when `ReceiverOptions.QueueName` is set. Competing-consumer semantics; appropriate for work distribution (emails to send, payments to process, IVA call tasks).
- **Subscription loop** — when `ReceiverOptions.TopicName` + `ReceiverOptions.SubscriptionName` are set. Each head's subscription receives a copy of every published message; appropriate for broadcast (registry sync, kill switches, cross-head config changes).

Both loops may run in the same process. A worker head typically configures both (a queue for its work, a topic subscription for broadcast events); a read-only head typically configures only a topic subscription. Per-source concurrency is bounded by `MaxConcurrency` via `Parallel.ForEachAsync`; default `1` preserves FIFO within a source, which is the correct default for convergence workloads where ordering matters.

Per-message flow inside each loop:

1. **Self-echo skip.** Read the `cirreum.node` application property; if it matches this replica's `INodeIdProvider.NodeId`, complete the message without further work. Cheap — no envelope deserialization, no handler dispatch.
2. **Envelope deserialization.** Parse the JSON body into `DistributedMessageEnvelope`. Failure → dead-letter with `EnvelopeDeserializationFailed` reason.
3. **.NET type resolution.** `Type.GetType(envelope.MessageType)`. Failure → complete with `UnknownMessageType` warning (acknowledge so the broker doesn't redeliver forever waiting for code that may never arrive).
4. **Payload deserialization.** Typed `DistributedMessage` instance via `envelope.DeserializeMessage()`. Failure → dead-letter.
5. **Wrap + publish via Conductor.** Build `DistributedMessageReceived<TMessage>` via cached reflection (per closed generic), resolve `IPublisher` from a fresh DI scope, publish. Result `IsSuccess == false` → abandon (broker retry, eventually DLQ at max delivery count). Exception → log + abandon. Success → complete.

### Sender enrichment — four application properties

`DefaultTransportPublisher.PublishMessageAsync<T>` now stamps four cross-broker filterable properties on every `OutboundMessage`:

| Property | Source | Purpose |
|---|---|---|
| `cirreum.identifier` | `MessageDefinitionAttribute.Identifier` | Subscription filter by message identifier / hierarchical prefix |
| `cirreum.version` | `MessageDefinitionAttribute.Version` | Version-aware filtering during rollovers |
| `cirreum.producer` | `{RuntimeType}:{EntryAssemblyName}` (existing producer id) | Head-level audit and routing |
| `cirreum.node` | `INodeIdProvider.NodeId` | Replica-level identity for self-echo prevention |

The properties live in `OutboundMessage.Properties` (the cross-provider abstraction); each broker maps them to its native filterable property bag — Service Bus `ApplicationProperties`, AWS SNS message attributes, Kafka headers, NATS headers. The data on the wire is identical across brokers; only the filter expression syntax differs, and that lives in infrastructure-as-code per deployment.

Strictly additive — message body, subject, content type, and transport semantics are unchanged. Existing receivers (including non-Cirreum consumers and pre-`1.1.0` Cirreum consumers) ignore the new properties.

### `INodeIdProvider` wired automatically

`HostingExtensions.AddDistributedMessaging` now registers `INodeIdProvider` via `TryAddSingleton<INodeIdProvider, DefaultNodeIdProvider>()`. The default implementation resolves replica identity via an environment-based chain (Container Apps replica name → App Service instance ID → container hostname → machine name + PID → generated GUID). Apps that need bespoke resolution (custom infrastructure metadata service, deterministic test IDs) register their own `INodeIdProvider` *before* invoking `AddMessaging()`; the framework's `TryAdd` then no-ops, leaving the app's implementation in place. Same swap-via-DI customization pattern used everywhere else in the framework.

### Receiver registration — opt-in via configuration

`HostingExtensions.AddDistributedMessaging` now also inspects a `Receiver` subsection under `Cirreum:Messaging:Distribution`:

```json
{
  "Cirreum": {
    "Messaging": {
      "Distribution": {
        "Sender": { /* unchanged */ },
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

Registration gates on the section's existence, a non-empty `InstanceKey`, and at least one of: non-empty `QueueName` *or* both non-empty `TopicName` and `SubscriptionName`. Sender-only deployments — the default for apps not opting into receive — see no behavioral change.

---

## App-facing usage

```csharp
using Cirreum.Conductor;
using Cirreum.Messaging;

public sealed class EvidenceInstanceChangeHandler
    : INotificationHandler<DistributedMessageReceived<EvidenceInstanceChangedV1>>
{
    private readonly IEvidenceInstanceRegistry _registry;

    public EvidenceInstanceChangeHandler(IEvidenceInstanceRegistry registry) {
        _registry = registry;
    }

    public Task HandleAsync(
        DistributedMessageReceived<EvidenceInstanceChangedV1> notification,
        CancellationToken ct)
    {
        var change = notification.Message;
        // notification.Envelope carries identifier, version, producer, publish time, etc.
        return _registry.ApplyRemoteChangeAsync(change.Operation, change.Key, ct);
    }
}
```

That's the entire app-side surface — one class per (message type, version), implementing the standard Conductor `INotificationHandler<>`. Conductor's existing assembly-scan auto-discovery picks it up; the receiver invokes it via Conductor's standard dispatch (DI scope, pipeline behaviors, exception propagation).

---

## Routing convention

Filter rules live in infrastructure-as-code, not in framework code. The framework guarantees the metadata; the deployment defines the rules. Common pattern: **hierarchical message identifiers** are the implicit audience signal.

| Head | Subscription filter (Service Bus SQL syntax) |
|---|---|
| API head | `cirreum.identifier LIKE 'api.%' OR cirreum.identifier LIKE 'auth.%' OR cirreum.identifier LIKE 'broadcast.%'` |
| Email head | `cirreum.identifier LIKE 'email.%' OR cirreum.identifier LIKE 'auth.%' OR cirreum.identifier LIKE 'broadcast.%'` |
| IVA head | `cirreum.identifier LIKE 'iva.%' OR cirreum.identifier LIKE 'auth.%' OR cirreum.identifier LIKE 'broadcast.%'` |

No new attribute properties, no new framework concepts — message identifiers already carry the semantic name, and broker filtering keys off them. AWS SNS uses the same data with filter-policy JSON syntax; Kafka uses partitioning; NATS uses subject patterns. The data is universal; the filter language is broker-specific and lives outside Cirreum.

---

## Multi-head topology

```
                  Admin Portal (any head)
                       │
                       ▼
            Publishes EvidenceInstanceChangedV1
                       │
                       ▼
          ┌──── Service Bus Topic ────┐
          │                           │
          ▼            ▼              ▼
   ┌──────────┐  ┌──────────┐   ┌──────────┐
   │ api-head │  │email-head│   │ iva-head │
   │ (N reps) │  │ (M reps) │   │ (K reps) │
   └──────────┘  └──────────┘   └──────────┘
   Framework      Framework      Framework
   receiver       receiver       receiver
   updates        updates        updates
   registry       registry       registry
```

Every replica of every head receives every broadcast message published to the topic. Each replica's receiver:

- Skips its own publishes via `cirreum.node` self-echo check (cheap, pre-deserialization)
- Processes messages from other replicas of the same head (different `cirreum.node`, same `cirreum.producer`) — necessary for convergence
- Processes messages from other heads (different `cirreum.producer`) — necessary for cross-head reactions

The result: every node converges on registry state within seconds of an admin change, without manual subscribe-loop code anywhere in the app.

---

## Telemetry

Receiver activity sources and metrics align with the existing send-side telemetry conventions:

- **Activity source:** `Cirreum.Messaging` (shared with the publisher)
- **Activity name:** `ReceiveMessageAsync` (matches the existing `PublishMessageAsync` naming)
- **Logger event IDs:** `2001`–`2010` for the receiver (publisher / batching uses `1xxx`, priority `6xxx`, circuit breaker `7xxx`)

Structured logging covers receiver lifecycle (`ReceiverStarting`, `ReceiverStopping`, `ReceiverShutdownTimeout`), consumer-loop health (`ConsumerLoopFailed`), per-message edge cases (`SelfEchoSkipped`, `EnvelopeDeserializationFailed`, `UnknownMessageType`, `PayloadDeserializationFailed`), and handler outcomes (`HandlerFailure`, `HandlerException`).

---

## Operational notes

**Sender-only deployments are unaffected.** Apps that don't add a `Receiver` section in configuration get no receiver and no behavior change — only the sender's four new application properties on outbound messages (which existing consumers ignore).

**Receiver requires `Cirreum.Core 5.2.0+`.** The abstractions consumed (`INodeIdProvider`, `DefaultNodeIdProvider`, `DistributedMessageReceived<TMessage>`, `ReceiverOptions`, `DistributedMessageEnvelope.PublishedAt`) are introduced in that release. `Cirreum.Runtime.Messaging` 1.1.0's `<PackageReference>` pins `Cirreum.Core 5.2.0`.

**Per-replica subscription rules referencing `cirreum.node`.** Service Bus subscription rules can reference `cirreum.node` for static filtering (e.g., excluding a specific known-bad replica), but typically don't — in-process self-echo filtering is cheap and dynamic. Use broker rules for routing by identifier / producer; use in-process filtering for node-level self-echo.

**Handler failure → broker retry, eventually DLQ.** If a handler throws or returns a failed `Result`, the receiver abandons the message. The broker redelivers (with its own retry / backoff policy) up to its max-delivery-count, then dead-letters. Operations monitor the DLQ via standard tooling.

**Unknown message types → acknowledged.** If a message arrives whose `MessageType` can't be resolved (`Type.GetType` returns null), the receiver completes the message with a warning log instead of dead-lettering. Rationale: redelivery won't make the type appear, and these typically result from temporary code-deploy lags rather than poison data.

**Dispatch strategy is pinned to `Sequential`.** The receiver passes `PublisherStrategy.Sequential` explicitly when publishing the wrapper notification through Conductor, independent of the host app's global Conductor `PublisherStrategy` setting. Apps can't usefully attribute the framework-owned `DistributedMessageReceived<T>` wrapper type, and inbound dispatch ordering shouldn't shift when an app changes its Conductor default for unrelated reasons. Sequential also runs every registered handler (`stopOnFailure: false`) so audit / observer co-handlers still fire when a primary handler throws.

---

## Architectural principle

> **Inbound dispatch is a Conductor concern; the framework owns the subscribe loop, envelope, and routing mechanics; apps own per-message-type handlers.**

The receiver does the mechanical work — receive, deserialize, wrap, publish, ack. Conductor does the semantic work — handler discovery, DI scoping, pipeline behaviors, exception propagation. Apps do the domain work — react to specific (message type, version) pairs through ordinary notification handlers. No new patterns, no new abstractions in the app surface.

---

## Compatibility

- **Source-compatible with `1.0.x`** for app code. The `DefaultTransportPublisher` constructor now takes an additional `INodeIdProvider` parameter, but that's wired by the hosting extension — apps don't construct it directly.
- **Wire-compatible with `1.0.x`** for messages. New application properties are additive; existing consumers ignore unknown properties. `cirreum.node` self-echo filtering is opt-in (only applies when receiver runs in `1.1.0+`).
- **Forward-compatible** with future broker plugins. The four application properties live in the cross-provider `OutboundMessage.Properties` bag and are mapped by each provider into its native filterable bag.
- **Requires `Cirreum.Core 5.2.0+`.** Floor-version bump from 5.1.x.

---

## See also

- `CHANGELOG.md` — condensed change list for `1.1.0`.
- `Cirreum.Core 5.2.0` release notes — full architectural framing of the abstractions this release consumes.
- `CONFIGURATION_GUIDE.md` — full sender + receiver configuration reference.
