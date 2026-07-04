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

### Honor `ReceiverOptions.PrefetchCount` and `MaxAutoLockRenewalDuration`

- **SemVer:** Minor
- **Trigger:** `Cirreum.Messaging` receiver-creation API grows an options parameter
- **Noted:** 2026-07-04

**Why:** The shipped `ReceiverOptions` (from `Cirreum.Messaging.Distributed`)
carries `PrefetchCount` and `MaxAutoLockRenewalDuration`, but
`IMessagingClient.UseQueueReceiver(string)` / `UseSubscription(string, string)`
take no tuning parameters, so `DistributedMessageReceiver` cannot pass them to
the broker — the two knobs are currently inert. Fixing this properly means
extending the `Cirreum.Messaging` client contract (e.g., an optional
receiver-options parameter or a configure callback) and flowing the values
through `Cirreum.Messaging.Azure` to `ServiceBusReceiverOptions` /
`ServiceBusProcessorOptions`. Cross-repo change; not worth a bespoke
Azure-only workaround here.

### Framework-supplied `TimeOfDayBatchingPolicy` + `UseTimeOfDayBatching(...)`

- **SemVer:** Minor
- **Trigger:** First app that wants time-of-day scaling back (the 1.x profile feature had no known consumers)
- **Noted:** 2026-07-04

**Why:** The `IBatchingPolicy` documentation in `Cirreum.Messaging.Distributed`
describes three usage levels; level 2 is a framework-supplied
`TimeOfDayBatchingPolicy` configured via a `UseTimeOfDayBatching(...)` fluent
builder. Level 1 (pass-through default) and level 3 (custom policy via
`AddMessaging(m => m.UseBatchingPolicy<T>())`) exist; level 2 does not. If
demand returns for the deleted 1.x time-profile behavior, implement it as a
proper policy: the policy class likely belongs in `Cirreum.Messaging.Distributed`
(no extra deps), with the `UseTimeOfDayBatching` verb added to
`IMessagingBuilder` here. Port the day-of-week / hour-window / scaling-factor
shape from the deleted `BatchScheduler`/`TimeBatchingProfile` (git history,
pre-2.0).

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

1. **Full framework path** — `[MessageVersion]` (+ optional `[DistributedMessageTarget]`) + `DistributedMessage` + `IConductor.PublishAsync()`. Routes through the framework's single configured queue/topic; inbound dispatches via Conductor. Right for framework cross-head state convergence, registry sync, kill switches, and "one event, many handlers may react" semantics.
2. **App-routed, framework-formatted** — `[MessageVersion]` + `DistributedMessage` for the wire contract, but bypass Conductor and publish via `IMessagingClient.UseQueueSender(...).PublishMessageAsync(...)` directly with a manually-built `DistributedMessageEnvelope` (constructed via `DistributedMessageEnvelope.Create(...)`). Apps keep Cirreum's envelope conventions (stable identifier + version, producer ID, publish-time, type resolution) and the broker-filterable application properties, but choose their own queues, run their own consumer loops, and tune per-workflow. The sweet spot for serious business workflows.
3. **Fully bespoke** — Raw `IMessagingClient` end-to-end with ad-hoc message classes. App owns everything. Right for legacy integration, external-broker-convention compatibility, or extreme performance-tuning cases.

The mental model worth conveying: `[MessageVersion]` +
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
