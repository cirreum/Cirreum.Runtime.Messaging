namespace Cirreum.Runtime.Messaging.Tests;

using Cirreum.Messaging;
using Cirreum.Messaging.Metrics;
using Cirreum.Messaging.Options;
using Cirreum.Runtime.Messaging;
using Cirreum.Runtime.Messaging.Batching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Diagnostics;

/// <summary>
/// Guards the messaging tracing surface. The channel's <see cref="ActivitySource"/> is shared and
/// process-lifetime: components start spans on it and never dispose it, and a span must cost
/// nothing when nobody is listening.
/// </summary>
public sealed class MessagingTelemetryTests {

	private const string SourceName = "Cirreum.Messaging";

	private static ActivityListener ListenerFor(ICollection<Activity> started) {
		var listener = new ActivityListener {
			ShouldListenTo = source => source.Name == SourceName,
			Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
			ActivityStarted = started.Add
		};
		ActivitySource.AddActivityListener(listener);
		return listener;
	}

	// -------------------------------------------------------------------------
	// The source itself
	// -------------------------------------------------------------------------

	[Fact]
	public void The_source_carries_a_version() {
		// A source without a version gives a backend no way to attribute spans to a release of
		// the instrumenting library. Matches the meter in DefaultMessagingMetricsService.
		MessagingTelemetry.ActivitySource.Name.Should().Be(SourceName);
		MessagingTelemetry.ActivitySource.Version.Should().NotBeNullOrWhiteSpace();
	}

	[Fact]
	public void Starting_a_span_with_no_listener_allocates_nothing() {
		// The regression this locks: CreateActivity(...) returns a live Activity even with no
		// listener attached, so the previous code allocated one per published message and per
		// batch regardless of whether anything collected it. StartActivity returns null instead.
		using var activity = MessagingTelemetry.ActivitySource.StartActivity("probe", ActivityKind.Producer);

		activity.Should().BeNull();
	}

	[Fact]
	public void Starting_a_span_with_a_listener_produces_one() {
		var started = new List<Activity>();
		using var listener = ListenerFor(started);

		using var activity = MessagingTelemetry.ActivitySource.StartActivity("probe", ActivityKind.Producer);

		activity.Should().NotBeNull();
		started.Should().ContainSingle().Which.OperationName.Should().Be("probe");
	}

	// -------------------------------------------------------------------------
	// The engine emits through it
	// -------------------------------------------------------------------------

	[Fact]
	public async Task Publishing_emits_a_span_on_the_shared_source() {
		var started = new List<Activity>();
		using var listener = ListenerFor(started);
		var harness = new EngineHarness();

		await harness.Engine.PublishMessageAsync(new QueueTestMessage("hello"), CancellationToken.None);

		started.Should().ContainSingle()
			.Which.OperationName.Should().Be(DistributeMessagingStrings.Activity_PublishMessageAsync);
	}

	[Fact]
	public async Task Disposing_the_engine_leaves_the_shared_source_usable() {
		// The engine used to dispose the ActivitySource it created. Now that the source is
		// shared and static, doing so would silently end tracing for the batch processor and
		// for every engine constructed afterwards.
		var harness = new EngineHarness();
		harness.Engine.Dispose();

		var started = new List<Activity>();
		using var listener = ListenerFor(started);
		using var activity = MessagingTelemetry.ActivitySource.StartActivity("after-dispose", ActivityKind.Producer);

		activity.Should().NotBeNull();
		started.Should().ContainSingle();
	}

	// -------------------------------------------------------------------------
	// Harness
	// -------------------------------------------------------------------------

	private sealed class EngineHarness {

		private static readonly MessageDefinition QueueDefinition = new(
			"tests.queue", "1.0", typeof(QueueTestMessage).FullName!, []);

		public DistributedMessageDeliveryEngine Engine { get; }

		public EngineHarness() {
			var client = Substitute.For<IMessagingClient>();
			var queueSender = Substitute.For<IMessagingQueueSender>();
			var registry = Substitute.For<IDistributedMessageRegistry>();
			var nodeIdProvider = Substitute.For<INodeIdProvider>();

			client.UseQueueSender("q-events").Returns(queueSender);
			registry.GetDefinitionFor<QueueTestMessage>().Returns(QueueDefinition);
			registry.GetTargetFor<QueueTestMessage>().Returns(MessageTarget.Queue);
			nodeIdProvider.NodeId.Returns("node-1");

			var services = new ServiceCollection();
			services.AddKeyedSingleton("test-instance", (_, _) => client);

			this.Engine = new DistributedMessageDeliveryEngine(
				services.BuildServiceProvider(),
				Substitute.For<IBatchProcessor>(),
				registry,
				Substitute.For<IDomainEnvironment>(),
				nodeIdProvider,
				Options.Create(new DistributedMessagingOptions {
					InstanceKey = "test-instance",
					QueueName = "q-events",
					TopicName = "t-notifications",
					BackgroundDelivery = new() { UseBackgroundDeliveryByDefault = false }
				}),
				NullLogger<DistributedMessageDeliveryEngine>.Instance,
				Substitute.For<IMessagingMetricsService>());
		}
	}
}
