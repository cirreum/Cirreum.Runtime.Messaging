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

### README: "Where handlers live — contracts in the Domain, handlers in the app"

- **SemVer:** Unspecified (docs)
- **Trigger:** The ADR-0029 Runtime.Messaging release (folds into that README pass)
- **Noted:** 2026-07-07

**Why:** A shared-Domain, multi-deployable shop (one Domain referenced by an API + an
ACA Job + a Function app) will hit a footgun: Conductor discovers handlers by assembly
scan, so a handler's *project* is its *deployment scope*. A handler placed in the shared
Domain is registered — and latent — in **every** deployable. Document the principle:
**the event type (`: DistributedMessage`) is the shared contract and lives in the
Domain; handlers live in the app that should run them.** Cover the `INotificationHandler<T>`
(local, fires at publish — "comes home") vs `INotificationHandler<DistributedMessageReceived<T>>`
(remote, fires on receipt) distinction as the way to express "process only remotely,"
and note the one deliberate exception (a genuinely cross-cutting local reaction every
deployment must perform may live in the Domain as a raw-`T` handler). Include the
project-layout sketch.

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

