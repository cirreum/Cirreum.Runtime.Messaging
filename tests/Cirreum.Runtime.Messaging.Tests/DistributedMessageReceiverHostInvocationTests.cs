namespace Cirreum.Runtime.Messaging.Tests;

using Cirreum;
using Cirreum.Conductor;
using Cirreum.Invocation;
using Cirreum.Messaging;
using Cirreum.Messaging.Options;
using Cirreum.Runtime.Messaging.Receiving;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Text.Json;

public class DistributedMessageReceiverHostInvocationTests {

	private static readonly MessageDefinition QueueDefinition = new(
		"tests.queue",
		"1.0",
		typeof(QueueTestMessage).FullName!,
		[]);

	private readonly IMessagingClient _client = Substitute.For<IMessagingClient>();
	private readonly IMessagingQueueReceiver _queueReceiver = Substitute.For<IMessagingQueueReceiver>();
	private readonly IPublisher _publisher = Substitute.For<IPublisher>();
	private readonly INodeIdProvider _nodeIdProvider = Substitute.For<INodeIdProvider>();
	private readonly IDistributedMessageRegistry _registry = Substitute.For<IDistributedMessageRegistry>();
	private readonly IHostInvocationFactory _factory = Substitute.For<IHostInvocationFactory>();
	private readonly IHostInvocation _invocation = Substitute.For<IHostInvocation>();
	private readonly List<HostInvocationOptions?> _begun = [];

	private DistributedMessageReceiver CreateReceiver(bool trustSubject = false, bool withFactory = true) {

		this._client.UseQueueReceiver("q-inbound", Arg.Any<ReceiverTuning>()).Returns(this._queueReceiver);
		this._nodeIdProvider.NodeId.Returns("node-1");
		this._registry.ResolveType("tests.queue", "1.0").Returns(typeof(QueueTestMessage));
		this._publisher
			.PublishAsync(
				Arg.Any<DistributedMessageReceived<QueueTestMessage>>(),
				Arg.Any<PublisherStrategy?>(),
				Arg.Any<CancellationToken>())
			.Returns(Task.FromResult(Result.Success));

		var services = new ServiceCollection();
		services.AddKeyedSingleton("test-instance", (_, _) => this._client);
		services.AddScoped(_ => this._publisher);
		var provider = services.BuildServiceProvider();

		var context = Substitute.For<IInvocationContext>();
		context.Services.Returns(provider.CreateScope().ServiceProvider);
		this._invocation.Context.Returns(context);
		this._factory.Begin(Arg.Do<HostInvocationOptions?>(o => this._begun.Add(o))).Returns(this._invocation);

		return new DistributedMessageReceiver(
			provider,
			this._nodeIdProvider,
			this._registry,
			Options.Create(new ReceiverOptions {
				InstanceKey = "test-instance",
				QueueName = "q-inbound",
				TrustEnvelopeSubject = trustSubject
			}),
			NullLogger<DistributedMessageReceiver>.Instance,
			withFactory ? this._factory : null);
	}

	private static IMessagingQueueReceivedMessage Message(string content, TaskCompletionSource<string> acked) {
		var message = Substitute.For<IMessagingQueueReceivedMessage>();
		message.ContentString.Returns(content);
		message.SystemProperties.Returns(new Dictionary<string, string>());
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

	private async Task<string> RunOneAsync(DistributedMessageReceiver receiver, string envelopeJson) {
		var acked = new TaskCompletionSource<string>();
		var message = Message(envelopeJson, acked);
		var stream = Stream(message);
		this._queueReceiver.ReceiveMessagesStreamAsync(Arg.Any<CancellationToken>())
			.Returns(stream);
		await receiver.StartAsync(CancellationToken.None);
		try {
			return await acked.Task.WaitAsync(TimeSpan.FromSeconds(10));
		} finally {
			await receiver.StopAsync(CancellationToken.None);
			receiver.Dispose();
		}
	}

	private static string EnvelopeJson(DistributedMessageSubject? subject = null) =>
		JsonSerializer.Serialize(
			DistributedMessageEnvelope.Create(new QueueTestMessage("hello"), QueueDefinition, "remote-producer", subject));

	[Fact]
	public async Task WithFactory_DispatchRunsInsideAQueueSourcedHostInvocation() {
		var outcome = await this.RunOneAsync(this.CreateReceiver(), EnvelopeJson());

		outcome.Should().Be("complete");
		this._begun.Should().ContainSingle();
		this._begun[0]!.Source.Should().Be(InvocationSources.Queue);
		this._begun[0]!.User.Should().BeNull();
		await this._invocation.Received(1).DisposeAsync();
		await this._publisher.Received(1).PublishAsync(
			Arg.Any<DistributedMessageReceived<QueueTestMessage>>(),
			Arg.Any<PublisherStrategy?>(),
			Arg.Any<CancellationToken>());
	}

	[Fact]
	public async Task WithFactory_SubjectIgnored_UnlessTrusted() {
		var subject = new DistributedMessageSubject("u-1", "Ada", "Cirreum.Entra", ["Admin"]);

		await this.RunOneAsync(this.CreateReceiver(trustSubject: false), EnvelopeJson(subject));

		this._begun[0]!.User.Should().BeNull();
		this._begun[0]!.OriginScheme.Should().BeNull();
		this._begun[0]!.Roles.Should().BeNull();
	}

	[Fact]
	public async Task WithFactory_TrustedSubject_RunsAsThatSubject() {
		var subject = new DistributedMessageSubject("u-1", "Ada", "Cirreum.Entra", ["Admin"]);

		await this.RunOneAsync(this.CreateReceiver(trustSubject: true), EnvelopeJson(subject));

		var options = this._begun[0]!;
		options.Source.Should().Be(InvocationSources.Queue);
		options.User.Should().NotBeNull();
		options.User!.Identity!.IsAuthenticated.Should().BeTrue();
		options.User.Identity.AuthenticationType.Should().Be("Cirreum.Entra");
		options.User.FindFirst("sub")!.Value.Should().Be("u-1");
		options.User.FindFirst("name")!.Value.Should().Be("Ada");
		options.OriginScheme.Should().Be("Cirreum.Entra");
		options.Roles.Should().Equal("Admin");
	}

	[Fact]
	public async Task WithFactory_TrustedButBlankScheme_FallsBackToTheHost() {
		var subject = new DistributedMessageSubject("u-1", "Ada", "", []);

		await this.RunOneAsync(this.CreateReceiver(trustSubject: true), EnvelopeJson(subject));

		this._begun[0]!.User.Should().BeNull();
	}

	[Fact]
	public async Task WithoutFactory_DispatchesInAPlainScope() {
		var outcome = await this.RunOneAsync(this.CreateReceiver(withFactory: false), EnvelopeJson());

		outcome.Should().Be("complete");
		this._begun.Should().BeEmpty();
		await this._publisher.Received(1).PublishAsync(
			Arg.Any<DistributedMessageReceived<QueueTestMessage>>(),
			Arg.Any<PublisherStrategy?>(),
			Arg.Any<CancellationToken>());
	}

}
