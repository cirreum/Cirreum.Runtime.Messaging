# Backlog

Deferred work for **Cirreum.Runtime.Messaging**. Items here are tracked but
not yet ready to ship — either because the cost outweighs the benefit in
isolation, or because they're waiting on a forcing function (a related
change, a consumer upgrade, a coordinated multi-repo rollout).

## How this file works

- Each item is a `###` heading so it can be linked to and parsed.
- Each item declares **`SemVer:`** (`Patch` | `Minor` | `Major` | `Unspecified`),
  **`Trigger:`** (the human-readable condition that will make it ready), and
  **`Noted:`** (the date the item was added).
- The Cirreum DevOps release scripts (`PatchRelease`, `MinorRelease`,
  `MajorRelease`) surface items at-or-below the requested bump level so the
  operator can decide whether to fold them in before tagging.
- Items that ship: move from this file to `docs/CHANGELOG.md` under
  `[Unreleased]`. Items that grow into design discussions: promote to an ADR.

## Queued

### Document "Choosing a Dispatch Path" guidance

- **SemVer:** Patch
- **Trigger:** Next Cirreum.Runtime.Messaging patch release (no specific blocker)
- **Noted:** 2026-05-10

**Why:** The 1.1.0 README documents the framework-managed receiver path
(opt-in via the `Receiver` config section, dispatch via Conductor through
`DistributedMessageReceived<T>`) but doesn't articulate when apps should
*not* reach for it. App teams building high-volume operational workflows
(email, payments, IVA, document processing) will hit the single-queue
constraint and either work around it badly (one big queue, all message
types funnelled together) or duplicate framework code (custom publishers,
hand-rolled consumer loops).

The clean guidance is three patterns, all valid:

1. **Full framework path** — `MessageDefinitionAttribute` + `DistributedMessage` + `IConductor.PublishAsync()`. Routes through the framework's single configured queue/topic; inbound dispatches via Conductor. Right for framework cross-head state convergence, registry sync, kill switches, and "one event, many handlers may react" semantics.
2. **App-routed, framework-formatted** — `MessageDefinitionAttribute` + `DistributedMessage` for the wire contract, but bypass Conductor and publish via `IMessagingClient.UseQueueSender(...).PublishMessageAsync(...)` directly with a manually-built `DistributedMessageEnvelope` (constructed via `DistributedMessageEnvelope.Create(...)`). Apps keep Cirreum's envelope conventions (stable identifier + version, producer ID, publish-time, type resolution) and the broker-filterable application properties, but choose their own queues, run their own consumer loops, and tune per-workflow. The sweet spot for serious business workflows.
3. **Fully bespoke** — Raw `IMessagingClient` end-to-end with ad-hoc message classes. App owns everything. Right for legacy integration, external-broker-convention compatibility, or extreme performance-tuning cases.

The mental model worth conveying: `MessageDefinitionAttribute` +
`DistributedMessage` define a *wire contract*. `IConductor.PublishAsync()`
is one *transport*, not the only one. Apps that internalize the separation
can reuse the framework's envelope vocabulary for audit/observability
tooling while keeping full freedom on routing and dispatch when their
workflow needs it.

**Suggested home:** New "Choosing a Dispatch Path" section in `README.md`,
placed between the existing "Consuming Inbound Messages" section and
"Documentation". Should include a brief comparison table and a one-paragraph
example of Pattern 2 (showing `DistributedMessageEnvelope.Create(...)` +
direct `IMessagingClient.UseQueueSender(...)` publish), without bloating the
README into a tutorial.

Patch-eligible: docs-only change, no API surface or behavior change.
Foldable into any future Runtime.Messaging patch; no specific blocking
trigger.
