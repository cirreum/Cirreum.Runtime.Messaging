# Cirreum.Runtime.Messaging 2.1.0 — Registry-resolved inbound dispatch, and the transport seam dissolves

Inbound type resolution now runs through the channel **registry by wire identity** rather than reflecting over the envelope's CLR type hint, and unresolved identities are disposed **per source** — a queue dead-letters for triage, a topic subscription completes as normal fan-out. Alongside it, the outbound transport-publisher abstraction is gone: the delivery engine *is* the outbound seam, injected directly by the Conductor bridge. All type renames are internal; apps that publish via `IPublisher.PublishAsync(...)` and handle via `INotificationHandler<DistributedMessageReceived<T>>` are unaffected.

This release pairs with `Cirreum.Messaging.Distributed 1.2.0`, which made the envelope identity-only (deleting envelope-side type resolution) and dissolved the transport-publisher contract.

---

## Why this release exists

The inbound receiver used to resolve a message's .NET type by reflecting over a CLR type name carried on the envelope (`Type.GetType(envelope.MessageType)`). That coupled delivery to assembly-qualified names on the wire and made the envelope responsible for type resolution — a job it is poorly placed to do safely, since a wire string is untrusted input and `Type.GetType` will happily load whatever it can find.

The channel model now treats the envelope as **identity-only**: a stable `(identifier, version)` pair plus an opaque payload. Resolution belongs to the **registry**, which only ever selects from the concrete `[MessageVersion]` types this process actually scanned — a vetted set, not an open `Type.GetType` lookup. `MessageType` stays on the envelope as **diagnostic metadata** (logging, dead-letter triage), never a resolution input.

The second half of the release is structural cleanup. Since the distributed channel no longer exposes a pluggable transport-publisher interface, the engine that used to implement it is now just the concrete delivery engine the outbound bridge calls directly. Removing the vestigial abstraction also let the internal type names say what they are.

---

## What's new

### Inbound type resolution by registry identity

`DistributedMessageReceiver` resolves the incoming type via `IDistributedMessageRegistry.ResolveType(envelope.MessageIdentifier, envelope.MessageVersion)`, then deserializes the payload against the resolved type with `envelope.DeserializeMessage(Type)`. The envelope performs no type resolution of its own. A resolved type is conforming by construction — it entered the registry through the concrete-`DistributedMessage` scan.

### Per-source disposition for an unregistered identity

An identity that resolves to no local type is now disposed by where it came from:

| Source | Disposition | Rationale |
|---|---|---|
| **Queue** | **Dead-letter** | A queue message is addressed to this consumer; an unknown identity means a missing assembly or a producer running ahead — worth operator triage, not a silent drop |
| **Topic subscription** | **Complete + log** | A fan-out subscription normally delivers family members a given consumer need not handle — normal weather, never a redelivery loop |

Previously *both* sources completed-and-logged. Queue consumers now surface genuinely unknown identities to the dead-letter queue instead of acking them away.

### The transport seam dissolves — internal renames

The distributed channel no longer exposes a per-channel transport-publisher interface (that contract was removed upstream). Consequently:

- `DefaultTransportPublisher` → **`DistributedMessageDeliveryEngine`**. It no longer implements a transport-publisher interface — it is the outbound seam, injected directly by the bridge, resolved by no abstraction. The now-redundant interface registration is removed from the hosting extension.
- `OutboundDistributedMessageHandler<T>` → **`DistributedMessageSender<T>`** (the open-generic Conductor bridge).

Both types are `internal`; there is no public API change.

### Hot-path performance

- **Allocation-free timing.** Per-publish timing in the delivery engine and per-batch timing in the batch processor use `Stopwatch.GetTimestamp()` / `Stopwatch.GetElapsedTime(...)` instead of allocating a `Stopwatch` per message/batch.
- **Reflection-free inbound dispatch.** The receiver dispatches through a per-type delegate closed over the concrete message type (built once, cached), replacing the per-message `Activator.CreateInstance` + `MethodInfo.Invoke` + `object[]` argument allocation with a direct call. The inbound path is a hot path — a single high-traffic message type is a high-load stream — so this matters.

Both are behavior-preserving; recorded durations and dispatched notifications are unchanged.

### Consumer guidance in the README

A new **"Where Handlers Live — Contracts in the Domain, Handlers in the App"** section documents the deployment-scope footgun: Conductor discovers handlers by assembly scan, so a handler's *project* is its *deployment scope*. The message type (`: DistributedMessage`) is the shared contract and belongs in the Domain; handlers belong in the app that should run them. The dispatch-path guidance was also clarified — the framework path is chosen by **queue topology and ownership**, not by expected load, and it carries whatever volume you put on it.

---

## Compatibility

- **Source-compatible for app code.** The renamed types are `internal`. Apps publish with `IPublisher.PublishAsync(myMessage)` and handle with `INotificationHandler<DistributedMessageReceived<T>>` — neither surface changed.
- **Wire-compatible.** The envelope shape is unchanged. The internal `QueueDepthAlertMessage` keeps its original `[MessageVersion]` identity as a frozen literal, so its wire identifier is stable across the engine rename — no rolling-upgrade mismatch.
- **One behavioral change to know about** (see Operational notes): on a **queue**, a message whose identity is unregistered now **dead-letters** instead of being completed.
- **Requires `Cirreum.Messaging.Distributed 1.2.0`.** This release consumes the identity-only envelope (`ResolveType(...)`, `DeserializeMessage(Type)`) and the dissolved transport seam introduced there.

---

## Operational notes

**Unknown identity on a queue → dead-letter (changed).** Prior versions completed-and-logged an inbound message whose type couldn't be resolved, regardless of source. A queue consumer now dead-letters an unregistered identity so the mismatch is visible for triage — the usual cause is a consumer missing the message type's assembly, or a producer deployed ahead of a consumer. Topic-subscription consumers are unchanged (complete + log), because fan-out legitimately delivers family members a subscriber doesn't handle.

**Resolution is now closed-set.** Because resolution goes through the registry's scanned `[MessageVersion]` set rather than an open `Type.GetType`, a type must be a public, `[MessageVersion]`-tagged `DistributedMessage` discovered at startup to be resolvable inbound — which is already the requirement for publishing it with a proper envelope identity.

**Diagnostic `MessageType`.** The envelope still carries the assembly-hinted CLR type name, now purely for logging and dead-letter triage. Operators reading a dead-letter still get the producer's best hint about what was sent.

---

## See also

- `CHANGELOG.md` — condensed change list for `2.1.0`.
- `README.md` — the reworked "Choosing a Dispatch Path" and "Where Handlers Live" sections.
- `Cirreum.Messaging.Distributed 1.2.0` release notes — the identity-only envelope and dissolved transport seam this release builds on.
