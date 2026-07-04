namespace Cirreum.Runtime.Messaging.Tests;

using Cirreum.Messaging;

/// <summary>
/// Queue-routed test message. Public so registry assembly scans discover it.
/// </summary>
[MessageVersion("tests.queue", "1.0")]
[DistributedMessageTarget(MessageTarget.Queue)]
public sealed record QueueTestMessage(string Payload) : DistributedMessage;