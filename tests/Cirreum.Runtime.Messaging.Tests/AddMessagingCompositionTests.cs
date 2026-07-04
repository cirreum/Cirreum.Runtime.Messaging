namespace Cirreum.Runtime.Messaging.Tests;

using Cirreum.Conductor;
using Cirreum.Messaging;
using Cirreum.Messaging.Batching;
using Cirreum.Runtime.Messaging;
using Cirreum.Runtime.Messaging.Batching;
using Cirreum.Runtime.Messaging.Receiving;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

public class AddMessagingCompositionTests {

	private static HostApplicationBuilder Builder(Dictionary<string, string?>? config = null) {
		var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
		if (config is not null) {
			builder.Configuration.AddInMemoryCollection(config);
		}
		return builder;
	}

	private static Dictionary<string, string?> SenderConfig(bool withReceiver = false) {
		var config = new Dictionary<string, string?> {
			["Cirreum:Messaging:Distributed:InstanceKey"] = "test-instance",
			["Cirreum:Messaging:Distributed:QueueName"] = "q-events",
			["Cirreum:Messaging:Distributed:TopicName"] = "t-notifications"
		};
		if (withReceiver) {
			config["Cirreum:Messaging:Distributed:Receiver:InstanceKey"] = "test-instance";
			config["Cirreum:Messaging:Distributed:Receiver:QueueName"] = "q-inbound";
		}
		return config;
	}

	[Fact]
	public void ConfiguredChannel_RegistersTheDeliveryComponents() {
		var builder = Builder(SenderConfig());

		builder.AddMessaging();

		builder.Services.Should().Contain(d => d.ServiceType == typeof(DefaultBatchProcessor));
		builder.Services.Should().Contain(d => d.ServiceType == typeof(IBatchProcessor));
		builder.Services.Should().Contain(d => d.ServiceType == typeof(IDistributedTransportPublisher<DistributedMessage>));
		builder.Services.Should().Contain(d =>
			d.ServiceType == typeof(INotificationHandler<>)
			&& d.ImplementationType == typeof(OutboundDistributedMessageHandler<>));
	}

	[Fact]
	public void UnconfiguredChannel_RegistersNoDeliveryComponents_ButKeepsTheRegistry() {
		var builder = Builder();

		builder.AddMessaging();

		builder.Services.Should().NotContain(d => d.ServiceType == typeof(DefaultBatchProcessor));
		builder.Services.Should().NotContain(d => d.ServiceType == typeof(IDistributedTransportPublisher<DistributedMessage>));
		builder.Services.Should().Contain(d => d.ServiceType == typeof(IDistributedMessageRegistry));
		builder.Services.Should().Contain(d => d.ServiceType == typeof(INodeIdProvider));
	}

	[Fact]
	public void DefaultBatchingPolicy_IsThePassThrough() {
		var builder = Builder(SenderConfig());

		builder.AddMessaging();

		using var provider = builder.Services.BuildServiceProvider();
		provider.GetRequiredService<IBatchingPolicy>().Should().BeOfType<DefaultBatchingPolicy>();
	}

	[Fact]
	public void CompositionCallback_ReplacesTheDefaultPolicy() {
		var builder = Builder(SenderConfig());

		builder.AddMessaging(m => m.UseBatchingPolicy<StubBatchingPolicy>());

		using var provider = builder.Services.BuildServiceProvider();
		provider.GetRequiredService<IBatchingPolicy>().Should().BeOfType<StubBatchingPolicy>();
	}

	[Fact]
	public void CompositionCallback_AppliesEvenAfterTheStackIsRegistered() {
		var builder = Builder(SenderConfig());

		builder.AddMessaging();
		builder.AddMessaging(m => m.UseBatchingPolicy<StubBatchingPolicy>());

		using var provider = builder.Services.BuildServiceProvider();
		provider.GetRequiredService<IBatchingPolicy>().Should().BeOfType<StubBatchingPolicy>();
	}

	[Fact]
	public void RepeatedAddMessaging_DoesNotDuplicateTheOutboundBridge() {
		var builder = Builder(SenderConfig());

		builder.AddMessaging();
		builder.AddMessaging();

		builder.Services
			.Count(d => d.ImplementationType == typeof(OutboundDistributedMessageHandler<>))
			.Should().Be(1);
	}

	[Fact]
	public void CompleteReceiverSection_RegistersTheReceiver() {
		var builder = Builder(SenderConfig(withReceiver: true));

		builder.AddMessaging();

		builder.Services.Should().Contain(d => d.ServiceType == typeof(DistributedMessageReceiver));
	}

	[Fact]
	public void IncompleteReceiverSection_DoesNotRegisterTheReceiver() {
		var config = SenderConfig();
		config["Cirreum:Messaging:Distributed:Receiver:QueueName"] = "q-inbound"; // no InstanceKey
		var builder = Builder(config);

		builder.AddMessaging();

		builder.Services.Should().NotContain(d => d.ServiceType == typeof(DistributedMessageReceiver));
	}

}