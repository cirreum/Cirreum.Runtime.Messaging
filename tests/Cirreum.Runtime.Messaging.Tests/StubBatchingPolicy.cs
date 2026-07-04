namespace Cirreum.Runtime.Messaging.Tests;

using Cirreum.Messaging.Batching;

/// <summary>
/// Marker policy for composition tests — pass-through behavior, distinct type.
/// </summary>
public sealed class StubBatchingPolicy : IBatchingPolicy {

	public BatchingDecision Evaluate(BatchingContext context) =>
		new(context.BaseFillWaitTime, context.BaseBatchCapacity);

}