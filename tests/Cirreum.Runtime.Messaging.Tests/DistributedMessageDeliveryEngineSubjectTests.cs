namespace Cirreum.Runtime.Messaging.Tests;

using Cirreum;
using Cirreum.Authentication;
using Cirreum.Invocation;
using Cirreum.Messaging;
using Cirreum.Messaging.Metrics;
using Cirreum.Messaging.Options;
using Cirreum.Runtime.Messaging;
using Cirreum.Runtime.Messaging.Batching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Security.Claims;

public class DistributedMessageDeliveryEngineSubjectTests {

	private static readonly MessageDefinition QueueDefinition = new(
		"tests.queue",
		"1.0",
		typeof(QueueTestMessage).FullName!,
		[]);

	private readonly IMessagingClient _client = Substitute.For<IMessagingClient>();
	private readonly IMessagingQueueSender _queueSender = Substitute.For<IMessagingQueueSender>();
	private readonly IDistributedMessageRegistry _registry = Substitute.For<IDistributedMessageRegistry>();

	private DistributedMessageDeliveryEngine CreateEngine(IInvocationContextAccessor? accessor) {

		this._client.UseQueueSender("q-events").Returns(this._queueSender);
		this._registry.GetDefinitionFor<QueueTestMessage>().Returns(QueueDefinition);
		this._registry.GetTargetFor<QueueTestMessage>().Returns(MessageTarget.Queue);

		var services = new ServiceCollection();
		services.AddKeyedSingleton("test-instance", (_, _) => this._client);

		return new DistributedMessageDeliveryEngine(
			services.BuildServiceProvider(),
			Substitute.For<IBatchProcessor>(),
			this._registry,
			Substitute.For<IDomainEnvironment>(),
			Substitute.For<INodeIdProvider>(),
			Options.Create(new DistributedMessagingOptions {
				InstanceKey = "test-instance",
				QueueName = "q-events",
				TopicName = "t-notifications"
			}),
			NullLogger<DistributedMessageDeliveryEngine>.Instance,
			Substitute.For<IMessagingMetricsService>(),
			accessor);
	}

	private static IInvocationContextAccessor AccessorWith(ClaimsPrincipal? user, string? originScheme = null) {
		var items = new Dictionary<object, object?> {
			[AuthenticationContextKeys.AuthenticatedScheme] = "Cirreum.Host"
		};
		if (originScheme is not null) {
			items[AuthenticationContextKeys.OriginScheme] = originScheme;
		}
		var invocation = Substitute.For<IInvocationContext>();
		invocation.User.Returns(user);
		invocation.Items.Returns(items);
		var accessor = Substitute.For<IInvocationContextAccessor>();
		accessor.Current.Returns(invocation);
		return accessor;
	}

	private async Task<DistributedMessageEnvelope> PublishAndReadEnvelopeAsync(DistributedMessageDeliveryEngine engine) {
		OutboundMessage? sent = null;
		this._queueSender
			.PublishMessageAsync(Arg.Do<OutboundMessage>(m => sent = m), Arg.Any<CancellationToken>())
			.Returns(Task.CompletedTask);

		await engine.PublishMessageAsync(new QueueTestMessage("hello"), CancellationToken.None);

		sent.Should().NotBeNull();
		return DistributedMessageEnvelope.FromJson(sent!.ContentString);
	}

	[Fact]
	public async Task NoAccessor_CarriesNoSubject() {
		var envelope = await this.PublishAndReadEnvelopeAsync(this.CreateEngine(accessor: null));

		envelope.Subject.Should().BeNull();
	}

	[Fact]
	public async Task NoCurrentInvocation_CarriesNoSubject() {
		var accessor = Substitute.For<IInvocationContextAccessor>();
		accessor.Current.Returns((IInvocationContext?)null);

		var envelope = await this.PublishAndReadEnvelopeAsync(this.CreateEngine(accessor));

		envelope.Subject.Should().BeNull();
	}

	[Fact]
	public async Task AnonymousInvocation_CarriesNoSubject() {
		var accessor = AccessorWith(new ClaimsPrincipal(new ClaimsIdentity()));

		var envelope = await this.PublishAndReadEnvelopeAsync(this.CreateEngine(accessor));

		envelope.Subject.Should().BeNull();
	}

	[Fact]
	public async Task AuthenticatedInvocation_CarriesIdNameSchemeAndRoles() {
		var user = new ClaimsPrincipal(new ClaimsIdentity(
			[
				new Claim("sub", "u-1"),
				new Claim("name", "Ada"),
				new Claim(ClaimTypes.Role, "Admin"),
				new Claim(ClaimTypes.Role, "Editor")
			],
			"Cirreum.Entra"));
		var accessor = AccessorWith(user, originScheme: "Cirreum.Entra");

		var envelope = await this.PublishAndReadEnvelopeAsync(this.CreateEngine(accessor));

		envelope.Subject.Should().NotBeNull();
		envelope.Subject!.Id.Should().Be("u-1");
		envelope.Subject.Name.Should().Be("Ada");
		envelope.Subject.Scheme.Should().Be("Cirreum.Entra");
		envelope.Subject.Roles.Should().BeEquivalentTo(["Admin", "Editor"]);
	}

	[Fact]
	public async Task HostInvocation_CarriesTheHostAsSubject() {
		var user = new ClaimsPrincipal(new ClaimsIdentity(
			[new Claim("sub", "LapCast.Api"), new Claim("name", "LapCast.Api")],
			"Cirreum.Host"));
		var accessor = AccessorWith(user);

		var envelope = await this.PublishAndReadEnvelopeAsync(this.CreateEngine(accessor));

		envelope.Subject!.Id.Should().Be("LapCast.Api");
		envelope.Subject.Scheme.Should().Be("Cirreum.Host");
	}

}
