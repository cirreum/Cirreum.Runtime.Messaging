namespace Cirreum.Runtime.Messaging.Metrics;

using Cirreum.Messaging;

// Frozen wire identity: a literal, deliberately NOT derived from the engine's type name.
// It is a cross-process message identifier, so it must stay stable across internal renames
// to avoid a rolling-upgrade identity mismatch between replicas.
[MessageVersion("DefaultTransportPublisher.QueueAlert", "1.0")]
[DistributedMessageTarget(MessageTarget.Queue)]
public record QueueDepthAlertMessage(
	long CurrentDepth,
	int CritcalThreshold
) : DistributedMessage {
	public override bool? UseBackgroundDelivery { get; set; } = true;
}