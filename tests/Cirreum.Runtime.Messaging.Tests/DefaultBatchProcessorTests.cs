namespace Cirreum.Runtime.Messaging.Tests;

using Cirreum.Messaging;
using Cirreum.Messaging.Batching;
using Cirreum.Messaging.Metrics;
using Cirreum.Messaging.Options;
using Cirreum.Runtime.Messaging.Batching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

public class DefaultBatchProcessorTests {

	private readonly IMessagingClient _client = Substitute.For<IMessagingClient>();
	private readonly IMessagingQueueSender _queueSender = Substitute.For<IMessagingQueueSender>();
	private readonly IBatchingPolicy _policy = Substitute.For<IBatchingPolicy>();
	private readonly IMessagingMetricsService _metrics = Substitute.For<IMessagingMetricsService>();

	private DefaultBatchProcessor CreateProcessor(
		int batchCapacity = 2,
		int circuitBreakerThreshold = 5) {

		this._client.UseQueueSender("q-events").Returns(this._queueSender);
		this._policy.Evaluate(Arg.Any<BatchingContext>())
			.Returns(ci => {
				var context = ci.Arg<BatchingContext>();
				return new BatchingDecision(context.BaseFillWaitTime, context.BaseBatchCapacity);
			});

		var services = new ServiceCollection();
		services.AddKeyedSingleton("test-instance", (_, _) => this._client);

		return new DefaultBatchProcessor(
			services.BuildServiceProvider(),
			this._policy,
			Options.Create(new DistributedMessagingOptions {
				InstanceKey = "test-instance",
				QueueName = "q-events",
				TopicName = "t-notifications",
				BackgroundDelivery = new() {
					BatchCapacity = batchCapacity,
					BatchFillWaitTime = TimeSpan.FromMilliseconds(40),
					QueueCapacity = 100,
					CircuitBreakerThreshold = circuitBreakerThreshold,
					CircuitResetTimeout = TimeSpan.FromMinutes(10)
				}
			}),
			NullLogger<DefaultBatchProcessor>.Instance,
			this._metrics);
	}

	private static OutboundMessage Outbound(string subject) =>
		OutboundMessage.AsJsonContent(new { }).WithSubject(subject);

	[Fact]
	public async Task SubmitBeforeStart_Throws() {
		var processor = this.CreateProcessor();

		var act = () => processor.SubmitMessageAsync(
			Outbound("s"), MessageTarget.Queue, DistributedMessagePriority.Standard, CancellationToken.None);

		await act.Should().ThrowAsync<InvalidOperationException>();
		processor.Dispose();
	}

	[Fact]
	public async Task SubmittedMessages_AreFlushedAsOneBulkSend_PerTheBatchingPolicy() {
		var flushed = new TaskCompletionSource<int>();
		this._queueSender
			.PublishMessagesAsync(
				Arg.Do<IEnumerable<OutboundMessage>>(m => flushed.TrySetResult(m.Count())),
				Arg.Any<IDictionary<string, object>?>(),
				Arg.Any<CancellationToken>())
			.Returns(Task.CompletedTask);
		var processor = this.CreateProcessor(batchCapacity: 2);

		await processor.StartAsync(CancellationToken.None);
		try {
			await processor.SubmitMessageAsync(Outbound("a"), MessageTarget.Queue, DistributedMessagePriority.Standard, CancellationToken.None);
			await processor.SubmitMessageAsync(Outbound("b"), MessageTarget.Queue, DistributedMessagePriority.Standard, CancellationToken.None);

			var sentCount = await flushed.Task.WaitAsync(TimeSpan.FromSeconds(10));

			sentCount.Should().Be(2);
			this._policy.Received().Evaluate(Arg.Is<BatchingContext>(c =>
				c.BaseBatchCapacity == 2
				&& c.BaseFillWaitTime == TimeSpan.FromMilliseconds(40)));
		} finally {
			await processor.StopAsync(CancellationToken.None);
			processor.Dispose();
		}
	}

	[Fact]
	public async Task OpenCircuit_StopsFurtherSends_UntilTheResetTimeout() {
		var firstFailure = new TaskCompletionSource();
		var sendAttempts = 0;
		this._queueSender
			.PublishMessagesAsync(
				Arg.Any<IEnumerable<OutboundMessage>>(),
				Arg.Any<IDictionary<string, object>?>(),
				Arg.Any<CancellationToken>())
			.Returns(_ => {
				Interlocked.Increment(ref sendAttempts);
				firstFailure.TrySetResult();
				return Task.FromException(new InvalidOperationException("broker down"));
			});
		var processor = this.CreateProcessor(circuitBreakerThreshold: 1);

		await processor.StartAsync(CancellationToken.None);
		try {
			await processor.SubmitMessageAsync(Outbound("a"), MessageTarget.Queue, DistributedMessagePriority.Standard, CancellationToken.None);
			await firstFailure.Task.WaitAsync(TimeSpan.FromSeconds(10));

			// The circuit is now open (threshold 1, 10-minute reset). Further
			// submissions must buffer without reaching the broker.
			await processor.SubmitMessageAsync(Outbound("b"), MessageTarget.Queue, DistributedMessagePriority.Standard, CancellationToken.None);
			await Task.Delay(400);

			sendAttempts.Should().Be(1);
		} finally {
			await processor.StopAsync(CancellationToken.None);
			processor.Dispose();
		}
	}

}