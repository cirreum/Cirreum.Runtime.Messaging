namespace Cirreum.Runtime.Messaging.Tests;

using Cirreum;
using Cirreum.Messaging;
using Cirreum.Messaging.Metrics;
using Cirreum.Messaging.Options;
using Cirreum.Runtime.Messaging;
using Cirreum.Runtime.Messaging.Batching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

public class DistributedMessageDeliveryEngineTests {

	private static readonly MessageDefinition QueueDefinition = new(
		"tests.queue",
		"1.0",
		typeof(QueueTestMessage).FullName!,
		[]);

	private readonly IMessagingClient _client = Substitute.For<IMessagingClient>();
	private readonly IMessagingQueueSender _queueSender = Substitute.For<IMessagingQueueSender>();
	private readonly IMessagingTopicSender _topicSender = Substitute.For<IMessagingTopicSender>();
	private readonly IBatchProcessor _batchProcessor = Substitute.For<IBatchProcessor>();
	private readonly IDistributedMessageRegistry _registry = Substitute.For<IDistributedMessageRegistry>();
	private readonly INodeIdProvider _nodeIdProvider = Substitute.For<INodeIdProvider>();
	private readonly IMessagingMetricsService _metrics = Substitute.For<IMessagingMetricsService>();

	private DistributedMessageDeliveryEngine CreateEngine(
		bool useBackgroundDeliveryByDefault = false,
		string? instanceKey = "test-instance") {

		this._client.UseQueueSender("q-events").Returns(this._queueSender);
		this._client.UseTopic("t-notifications").Returns(this._topicSender);
		this._registry.GetDefinitionFor<QueueTestMessage>().Returns(QueueDefinition);
		this._registry.GetTargetFor<QueueTestMessage>().Returns(MessageTarget.Queue);
		this._nodeIdProvider.NodeId.Returns("node-1");

		var services = new ServiceCollection();
		services.AddKeyedSingleton("test-instance", (_, _) => this._client);
		var serviceProvider = services.BuildServiceProvider();

		var options = Options.Create(new DistributedMessagingOptions {
			InstanceKey = instanceKey!,
			QueueName = "q-events",
			TopicName = "t-notifications",
			BackgroundDelivery = new() {
				UseBackgroundDeliveryByDefault = useBackgroundDeliveryByDefault
			}
		});

		return new DistributedMessageDeliveryEngine(
			serviceProvider,
			this._batchProcessor,
			this._registry,
			Substitute.For<IDomainEnvironment>(),
			this._nodeIdProvider,
			options,
			NullLogger<DistributedMessageDeliveryEngine>.Instance,
			this._metrics);
	}

	[Fact]
	public async Task TypedDirectPublish_SendsToTheQueue_WithStampedProperties() {
		OutboundMessage? sent = null;
		this._queueSender
			.PublishMessageAsync(Arg.Do<OutboundMessage>(m => sent = m), Arg.Any<CancellationToken>())
			.Returns(Task.CompletedTask);
		var engine = this.CreateEngine();

		await engine.PublishMessageAsync(new QueueTestMessage("hello"), CancellationToken.None);

		sent.Should().NotBeNull();
		sent!.Subject.Should().Be("tests.queue.v1.0");
		sent.Properties["cirreum.identifier"].Should().Be("tests.queue");
		sent.Properties["cirreum.version"].Should().Be("1.0");
		sent.Properties["cirreum.node"].Should().Be("node-1");
		sent.Properties.Should().ContainKey("cirreum.producer");
		this._metrics.Received(1).RecordMessageReceived("tests.queue.v1.0", MessageTarget.Queue);
		this._metrics.Received(1).RecordMessageDelivered("tests.queue.v1.0", MessageTarget.Queue, Arg.Any<long>());
		await this._batchProcessor.DidNotReceiveWithAnyArgs().SubmitMessageAsync(default!, default, default, default);
	}

	[Fact]
	public async Task PerMessageBackgroundFlag_SubmitsToTheBatchProcessor() {
		var engine = this.CreateEngine(useBackgroundDeliveryByDefault: false);
		var message = new QueueTestMessage("hello") {
			UseBackgroundDelivery = true,
			Priority = DistributedMessagePriority.TimeSensitive
		};

		await engine.PublishMessageAsync(message, CancellationToken.None);

		await this._batchProcessor.Received(1).SubmitMessageAsync(
			Arg.Any<OutboundMessage>(),
			MessageTarget.Queue,
			DistributedMessagePriority.TimeSensitive,
			Arg.Any<CancellationToken>());
		await this._queueSender.DidNotReceive()
			.PublishMessageAsync(Arg.Any<OutboundMessage>(), Arg.Any<CancellationToken>());
		this._metrics.Received(1).RecordMessageQueued(
			"tests.queue.v1.0",
			MessageTarget.Queue,
			Arg.Any<long>(),
			DistributedMessagePriority.TimeSensitive);
	}

	[Fact]
	public async Task ChannelDefaultBackground_AppliesWhenTheMessageDoesNotChoose() {
		var engine = this.CreateEngine(useBackgroundDeliveryByDefault: true);

		await engine.PublishMessageAsync(new QueueTestMessage("hello"), CancellationToken.None);

		await this._batchProcessor.Received(1).SubmitMessageAsync(
			Arg.Any<OutboundMessage>(),
			MessageTarget.Queue,
			DistributedMessagePriority.Standard,
			Arg.Any<CancellationToken>());
	}

	[Fact]
	public void MissingInstanceKey_FailsConstruction() {
		var act = () => this.CreateEngine(instanceKey: "");

		act.Should().Throw<InvalidOperationException>();
	}

}
