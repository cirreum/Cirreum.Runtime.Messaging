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

- **SemVer:** Major
- **Trigger:** `Cirreum.Messaging` receiver-creation API grows an options parameter
- **Noted:** 2026-07-04

> **Tail of a three-repo chain — do not start here.** The head is `Cirreum.Messaging`
> (Common), which owns the contract that must move first; then `Cirreum.Messaging.Azure`
> (Infrastructure) maps the values onto the SDK. Both carry a mirror of this item, and the
> Common one is marked `Minor` so it surfaces early, while acting on it is still cheap.
>
> **SemVer raised from `Minor` to `Major` on 2026-07-27**, matching `Cirreum.Messaging.Azure`.
> The work here is additive, but it cannot start until the upstream contract moves — so
> surfacing it on every minor of this package is noise. It fired at the tail of the Kernel 2.0.0
> wave, after Common and Infrastructure had shipped and closed, when the only way to act on it
> was to reopen two rungs from the bottom.

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

