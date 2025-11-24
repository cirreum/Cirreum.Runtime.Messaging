namespace Cirreum.Runtime.Messaging.Metrics;

using Cirreum.Messaging;

[MessageDefinition($"{nameof(DefaultTransportPublisher)}.QueueAlert", "1.0", MessageTarget.Queue)]
public record QueueDepthAlertMessage(
	long CurrentDepth,
	int CritcalThreshold
) : DistributedMessage {
	public override bool? UseBackgroundDelivery { get; set; } = true;
}