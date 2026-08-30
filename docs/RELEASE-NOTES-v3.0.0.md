# Cirreum.Runtime.Messaging 3.0.0

Distributed routing metadata moves from the application's property bag to the framework's own.

For an application there is no code change — the keys, their values, and the self-echo behaviour are
unchanged. The migration is the dependency bump: Cirreum.Messaging 2.0.0 and
Cirreum.Messaging.Azure 2.0.0.

## What this fixes

The self-echo skip — the check that stops a node reprocessing a message it published itself — read
the node id out of a shared, untyped bag:

```csharp
if (received.Properties.TryGetValue(Property_Node, out var nodeObj)
    && nodeObj is string remoteNode
    && remoteNode == this._nodeIdProvider.NodeId) {
```

Three things had to hold for that to work, and none of them was stated anywhere: that the provider
forwarded the property, that it did not filter it, and that the value came back as the type it went
out as. The last one is a type test that fails silently — no match, no log, no error — and the node
processes its own message. It held only because Azure Service Bus is the sole provider and its AMQP
mapping happens to return a string as a string. `DistributedMessageDeliveryEngine`'s own
documentation anticipates SNS message attributes and Kafka headers, and neither surfaces a `string`.

`SystemProperties` is reserved for the framework and typed `string`, so the round trip cannot change
the shape. The type test is gone, and the read is a plain dictionary lookup. The keys also sit in a
space an application cannot write to, so a message cannot carry a forged node id.

`DistributeMessagingStrings` is unchanged — the four `cirreum.*` keys are the same values, still
addressable by broker-side subscription filters.

Full steps, including the test-side change, in [MIGRATION-v3](MIGRATION-v3.md).
