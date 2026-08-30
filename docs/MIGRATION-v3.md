# Cirreum.Runtime.Messaging v2 → v3 Migration

v3 moves distributed routing metadata out of the application's property bag and into the framework's
own. For an application consuming this package there is **no code change**: the keys, their values
and the self-echo behaviour are all unchanged. What changed is which bag carries them.

The dependency bump is the migration:

| Package | v2 | v3 |
| --- | --- | --- |
| Cirreum.Messaging | 1.1.1 | 2.0.0 |
| Cirreum.Messaging.Azure | 1.2.3 | 2.0.0 |

---

## What moved

`DistributedMessageDeliveryEngine` stamped four keys onto every outbound message's `Properties` —
the bag an application also writes to. They now go to `SystemProperties`:

```csharp
outboundMessage
    .WithSystemProperty(DistributeMessagingStrings.Property_Identifier, envelope.MessageIdentifier)
    .WithSystemProperty(DistributeMessagingStrings.Property_Version, envelope.MessageVersion)
    .WithSystemProperty(DistributeMessagingStrings.Property_Producer, envelope.ProducerId)
    .WithSystemProperty(DistributeMessagingStrings.Property_Node, this._nodeIdProvider.NodeId);
```

`DistributeMessagingStrings` is unchanged. The four `cirreum.*` constants are the same values, and
they remain addressable by broker-side subscription filters, since the provider carries system
properties in the same wire dictionary.

## Why it matters

The self-echo skip read the node id back like this:

```csharp
if (received.Properties.TryGetValue(Property_Node, out var nodeObj)
    && nodeObj is string remoteNode
    && remoteNode == this._nodeIdProvider.NodeId) {
```

A value arriving in any representation other than `string` failed the pattern silently — no match,
no log, no error — and the node went on to reprocess its own message. It worked only because Azure
Service Bus returns a string as a string. The type test is now gone: a system property is a `string`
by contract, and the read is a plain dictionary lookup.

The keys also now live in a space an application cannot occupy. Under v2, writing
`Properties["cirreum.node"]` from application code would have travelled and could have suppressed a
legitimate message's processing; the provider no longer forwards a reserved key from the application
bag.

## If you assert on message properties in tests

A test that inspected the routing keys reads them from the other bag now:

```csharp
// v2
sent.Properties["cirreum.node"].Should().Be("node-1");

// v3
sent.SystemProperties["cirreum.node"].Should().Be("node-1");
```

A substitute for `IMessagingReceivedMessage` used in a receive-loop test sets
`SystemProperties` (typed `IReadOnlyDictionary<string, string>`) rather than `Properties`.
