# Cirreum.Runtime.Messaging 2.3.0 — the receiver-tuning chain closes

## Why this release exists

`ReceiverOptions.PrefetchCount` and `MaxAutoLockRenewalDuration` have been bindable,
documented, defaulted configuration since they shipped — and neither has ever done anything.
This is the tail of the three-repo chain that fixes both: `Cirreum.Messaging` 1.1.0/1.1.1 grew
the broker contract, `Cirreum.Messaging.Azure` 1.2.0 mapped it onto Service Bus, and this
release flows the configured values through — each knob at the layer that can actually honor
it.

## What's new

**Prefetch flows to the broker.** The receive loops pass `PrefetchCount` through the tuned
receiver-creation overloads, so the broker client buffers ahead of processing and throughput
tuning finally has an effect.

> ⚠️ **One deliberate behavior change:** the documented default of `10` now genuinely applies.
> Until now the broker SDK's own default (no prefetch) silently won, so upgraded receivers
> gain prefetch-10 semantics — higher throughput, slightly longer lock exposure for buffered
> messages. Operators who want the old behavior set `Receiver:PrefetchCount` to `0`.

**Lock renewal is real, and lives where it belongs.** Auto-renewal is not a receiver-creation
option on a pull-based client — it is processing-time behavior owned by whoever runs the
handler. The receive loop now renews the message's lock on a fixed 15-second cadence while
handlers run, capped at `MaxAutoLockRenewalDuration` (default 5 minutes; zero disables), and
renewal is guaranteed stopped before any complete/abandon. Long-running handlers no longer
silently lose their lock mid-processing and reappear as duplicate deliveries. The 15-second
cadence is conservative for the shortest lock durations brokers use in practice (Azure Service
Bus defaults to 60 seconds), and fast handlers never renew at all — the first renewal only
happens when processing outlives the first interval. A renewal failure logs a warning (event
`2012`) and stops renewing; the broker's redelivery applies if the lock then expires.

## Compatibility

Additive surface; two knobs that were inert now work, which is the point. The prefetch default
becoming effective is the only behavior change and is flagged above.

## See also

- `Cirreum.Messaging` 1.1.0 / 1.1.1 and `Cirreum.Messaging.Azure` 1.2.0 release notes — the
  chain's head and middle
- `docs/CHANGELOG.md` — the enumerated changes
