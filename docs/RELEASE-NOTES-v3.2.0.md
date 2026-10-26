# Cirreum.Runtime.Messaging 3.2.0 — Received messages have a subject

## Why this release exists

`DistributedMessageReceiver` created a service scope per received message and dispatched
through the conductor, but it never set an invocation context. Every distributed message
handler ran with `IInvocationContextAccessor.Current == null`: the anonymous caller, `Guest`
on every audit stamp, nobody as far as any authorizer could tell. The gap was hidden only
because no handler had yet needed a subject.

With host invocations in `Cirreum.Contracts` 6.2.0 and a subject slot on the envelope in
`Cirreum.Messaging.Distributed` 2.1.0, both halves can be closed.

## What's new

### Dispatch runs inside a host invocation

When the host composes an `IHostInvocationFactory` (the server package registers one), each
inbound dispatch runs inside `factory.Begin(new() { Source = InvocationSources.Queue, ... })`.
Handlers see an authenticated subject, `SubjectKind.Machine`, `InvocationSource == "queue"`,
and the receiving application's name on anything they write. The message's cancellation token
is linked into the invocation's `Aborted`.

Without a factory — a host that composes messaging without the server spine — the receiver
keeps its plain per-dispatch scope, unchanged.

### Envelopes carry the publishing subject

The delivery engine fills `DistributedMessageEnvelope.Subject` from the current invocation's
principal and effective scheme when one is current and authenticated: id, name, scheme, and
roles as `IsInRole` would answer them. With no current invocation, or an anonymous one, the
envelope carries none.

### `TrustEnvelopeSubject`

```json
"Cirreum:Messaging:Distributed:Receiver": {
  "TrustEnvelopeSubject": true
}
```

Off by default. When set, and the envelope carries a subject with a scheme, the receiver runs
the dispatch as that subject: a principal minted from its id and name on its scheme, the
scheme as `OriginScheme`, and its roles minted for the dispatch. Subject kind, boundary and
application user then resolve for the subject rather than for the host, and audit reads as
the host acting for that subject, asserted by `ProducerId`.

The subject is the producer's assertion. Within one application's nodes that is the same
trust the self-echo skip already rests on; across applications it is a decision to let
another application's roles authorize work here. Turn it on per receiver, deliberately.

## Compatibility

Additive. `DistributedMessageDeliveryEngine` and `DistributedMessageReceiver` gained optional
trailing constructor dependencies resolved from the container when present. Envelopes from
older producers carry no subject and dispatch as the host.

## See also

- `docs/CHANGELOG.md` — `[3.2.0]`
- `Cirreum.Messaging.Distributed` 2.1.0 release notes — the envelope subject
- `Cirreum.Services.Server` 1.7.0 release notes — the host invocation factory
