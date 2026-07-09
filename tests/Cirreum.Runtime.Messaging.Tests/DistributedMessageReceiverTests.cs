namespace Cirreum.Runtime.Messaging.Tests;

using Cirreum;
using Cirreum.Conductor;
using Cirreum.Messaging;
using Cirreum.Messaging.Options;
using Cirreum.Runtime.Messaging.Receiving;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Text.Json;

public class DistributedMessageReceiverTests {

	private static readonly MessageDefinition QueueDefinition = new(
		"tests.queue",
		"1.0",
		typeof(QueueTestMessage).FullName!,
		[]);

	private readonly IMessagingClient _client = Substitute.For<IMessagingClient>();
	private readonly IMessagingQueueReceiver _queueReceiver = Substitute.For<IMessagingQueueReceiver>();
	private readonly IMessagingSubscriptionReceiver _subscriptionReceiver = Substitute.For<IMessagingSubscriptionReceiver>();
	private readonly IPublisher _publisher = Substitute.For<IPublisher>();
	private readonly INodeIdProvider _nodeIdProvider = Substitute.For<INodeIdProvider>();
	private readonly IDistributedMessageRegistry _registry = Substitute.For<IDistributedMessageRegistry>();

	private DistributedMessageReceiver CreateReceiver(bool subscription = false) {

		this._client.UseQueueReceiver("q-inbound").Returns(this._queueReceiver);
		this._client.UseSubscription("t-inbound", "s-inbound").Returns(this._subscriptionReceiver);
		this._nodeIdProvider.NodeId.Returns("node-1");
		// The registry resolves the known test identity by (identifier, version); an
		// unregistered identity resolves to null (the substitute default).
		this._registry.ResolveType("tests.queue", "1.0").Returns(typeof(QueueTestMessage));

		var options = subscription
			? new ReceiverOptions {
				InstanceKey = "test-instance",
				TopicName = "t-inbound",
				SubscriptionName = "s-inbound"
			}
			: new ReceiverOptions {
				InstanceKey = "test-instance",
				QueueName = "q-inbound"
			};

		var services = new ServiceCollection();
		services.AddKeyedSingleton("test-instance", (_, _) => this._client);
		services.AddScoped(_ => this._publisher);

		return new DistributedMessageReceiver(
			services.BuildServiceProvider(),
			this._nodeIdProvider,
			this._registry,
			Options.Create(options),
			NullLogger<DistributedMessageReceiver>.Instance);
	}

	/// <summary>
	/// Builds a received-message substitute whose terminal ack (complete / abandon /
	/// dead-letter) resolves <paramref name="acked"/> so tests can await processing.
	/// </summary>
	private static T Message<T>(
		string content,
		TaskCompletionSource<string> acked,
		IReadOnlyDictionary<string, object>? properties = null)
		where T : class, IMessagingReceivedMessage {

		var message = Substitute.For<T>();
		message.ContentString.Returns(content);
		message.Properties.Returns(properties ?? new Dictionary<string, object>());
		message.CompleteMessageAsync(Arg.Any<CancellationToken>())
			.Returns(_ => { acked.TrySetResult("complete"); return Task.CompletedTask; });
		message.AbandonMessageAsync(Arg.Any<CancellationToken>())
			.Returns(_ => { acked.TrySetResult("abandon"); return Task.CompletedTask; });
		message.DeadLetterMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
			.Returns(_ => { acked.TrySetResult("deadletter"); return Task.CompletedTask; });
		return message;
	}

	private static async IAsyncEnumerable<T> Stream<T>(params T[] messages) {
		foreach (var message in messages) {
			yield return message;
		}
		await Task.CompletedTask;
	}

	private async Task<string> RunAsync(DistributedMessageReceiver receiver, TaskCompletionSource<string> acked) {
		await receiver.StartAsync(CancellationToken.None);
		try {
			return await acked.Task.WaitAsync(TimeSpan.FromSeconds(10));
		} finally {
			await receiver.StopAsync(CancellationToken.None);
			receiver.Dispose();
		}
	}

	private async Task<string> RunOneQueueMessageAsync(IMessagingQueueReceivedMessage message, TaskCompletionSource<string> acked) {
		this._queueReceiver.ReceiveMessagesStreamAsync(Arg.Any<CancellationToken>())
			.Returns(Stream(message));
		return await this.RunAsync(this.CreateReceiver(subscription: false), acked);
	}

	private async Task<string> RunOneSubscriptionMessageAsync(IMessagingSubscriptionReceivedMessage message, TaskCompletionSource<string> acked) {
		this._subscriptionReceiver.ReceiveMessagesStreamAsync(Arg.Any<CancellationToken>())
			.Returns(Stream(message));
		return await this.RunAsync(this.CreateReceiver(subscription: true), acked);
	}

	private static string EnvelopeJson(QueueTestMessage message) =>
		JsonSerializer.Serialize(
			DistributedMessageEnvelope.Create(message, QueueDefinition, "remote-producer"));

	private static string UnknownIdentityEnvelopeJson() =>
		JsonSerializer.Serialize(new DistributedMessageEnvelope {
			MessageType = "No.Such.Type, No.Such.Assembly",
			MessageIdentifier = "tests.unknown",
			MessageVersion = "1.0",
			SerializedMessage = "{}",
			ProducerId = "remote-producer"
		});

	[Fact]
	public async Task SelfEcho_IsCompletedWithoutDispatch() {
		var acked = new TaskCompletionSource<string>();
		var message = Message<IMessagingQueueReceivedMessage>(
			EnvelopeJson(new QueueTestMessage("own message")),
			acked,
			new Dictionary<string, object> { ["cirreum.node"] = "node-1" });

		var ack = await this.RunOneQueueMessageAsync(message, acked);

		ack.Should().Be("complete");
		await this._publisher.DidNotReceiveWithAnyArgs()
			.PublishAsync<DistributedMessageReceived<QueueTestMessage>>(default!, default, default);
	}

	[Fact]
	public async Task UndeserializableEnvelope_IsDeadLettered() {
		var acked = new TaskCompletionSource<string>();
		var message = Message<IMessagingQueueReceivedMessage>("this is not an envelope", acked);

		var ack = await this.RunOneQueueMessageAsync(message, acked);

		ack.Should().Be("deadletter");
	}

	[Fact]
	public async Task UnknownIdentity_OnQueue_IsDeadLetteredForTriage() {
		var acked = new TaskCompletionSource<string>();
		var message = Message<IMessagingQueueReceivedMessage>(UnknownIdentityEnvelopeJson(), acked);

		var ack = await this.RunOneQueueMessageAsync(message, acked);

		ack.Should().Be("deadletter");
	}

	[Fact]
	public async Task UnknownIdentity_OnSubscription_IsCompletedAsNormalFanOut() {
		var acked = new TaskCompletionSource<string>();
		var message = Message<IMessagingSubscriptionReceivedMessage>(UnknownIdentityEnvelopeJson(), acked);

		var ack = await this.RunOneSubscriptionMessageAsync(message, acked);

		ack.Should().Be("complete");
	}

	[Fact]
	public async Task SuccessfulDispatch_PublishesTheWrapper_AndCompletes() {
		this._publisher.PublishAsync(
				Arg.Any<DistributedMessageReceived<QueueTestMessage>>(),
				Arg.Any<PublisherStrategy?>(),
				Arg.Any<CancellationToken>())
			.Returns(Task.FromResult(Result.Success));
		var acked = new TaskCompletionSource<string>();
		var message = Message<IMessagingQueueReceivedMessage>(EnvelopeJson(new QueueTestMessage("inbound payload")), acked);

		var ack = await this.RunOneQueueMessageAsync(message, acked);

		ack.Should().Be("complete");
		await this._publisher.Received(1).PublishAsync(
			Arg.Is<DistributedMessageReceived<QueueTestMessage>>(r =>
				r.Message.Payload == "inbound payload"
				&& r.Envelope.ProducerId == "remote-producer"),
			Arg.Any<PublisherStrategy?>(),
			Arg.Any<CancellationToken>());
	}

	[Fact]
	public async Task FailedHandlerResult_AbandonsForRedelivery() {
		this._publisher.PublishAsync(
				Arg.Any<DistributedMessageReceived<QueueTestMessage>>(),
				Arg.Any<PublisherStrategy?>(),
				Arg.Any<CancellationToken>())
			.Returns(Task.FromResult(Result.Fail(new InvalidOperationException("handler failed"))));
		var acked = new TaskCompletionSource<string>();
		var message = Message<IMessagingQueueReceivedMessage>(EnvelopeJson(new QueueTestMessage("inbound payload")), acked);

		var ack = await this.RunOneQueueMessageAsync(message, acked);

		ack.Should().Be("abandon");
	}

}
