namespace Microsoft.AspNetCore.Hosting;

using Cirreum.Conductor;
using Cirreum.Messaging;
using Cirreum.Messaging.Batching;
using Cirreum.Messaging.Configuration;
using Cirreum.Messaging.Health;
using Cirreum.Messaging.Metrics;
using Cirreum.Messaging.Options;
using Cirreum.Runtime.Messaging;
using Cirreum.Runtime.Messaging.Batching;
using Cirreum.Runtime.Messaging.Metrics;
using Cirreum.Runtime.Messaging.Receiving;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

public static class HostingExtensions {

	private class ConfigureMessagingMarker { }

	/// <summary>
	/// Add support for messaging (Service Bus) by registering any configured providers
	/// and associated instances.
	/// </summary>
	/// <param name="builder">The host application builder.</param>
	/// <param name="configure">Optional fluent composition callback — e.g.,
	/// <c>AddMessaging(m => m.UseBatchingPolicy&lt;MyPolicy&gt;())</c>. Applied on every
	/// call, even when the messaging stack itself is already registered.</param>
	public static IHostApplicationBuilder AddMessaging(
		this IHostApplicationBuilder builder,
		Action<IMessagingBuilder>? configure = null) {

		// Apply the fluent composition first (Replace-based, so it wins over the
		// framework defaults regardless of call order) and before the marker
		// early-return so a configure-only call still lands.
		configure?.Invoke(new MessagingBuilder(builder.Services));

		// Check if already registered using a marker service
		if (builder.Services.IsMarkerTypeRegistered<ConfigureMessagingMarker>()) {
			return builder;
		}

		// Mark as registered
		builder.Services.MarkTypeAsRegistered<ConfigureMessagingMarker>();

		// Service Providers...
		return builder
			.RegisterServiceProvider<AzureServiceBusRegistrar, AzureServiceBusSettings, AzureServiceBusInstanceSettings, AzureServiceBusHealthCheckOptions>()
			// .RegisterServiceProvider<AWSServiceBusRegistrar, ...>();
			.AddDistributedMessaging();

	}

	private static IHostApplicationBuilder AddDistributedMessaging(
		this IHostApplicationBuilder builder) {

		// Ensure we have in-memory caching...
		builder.Services.AddMemoryCache();

		// Add messaging to OpenTelemetry
		builder.Services.AddOpenTelemetry()
			.WithMetrics(metrics => metrics
				.AddMeter(DistributeMessagingStrings.MessagingNamespace))
			.WithTracing(tracing => tracing
				.AddSource(DistributeMessagingStrings.MessagingNamespace)
			);

		// Register the Distributed Messaging Metrics Service
		var metricsSection = builder.Configuration.GetSection($"{DistributeMessagingStrings.ConfigurationKey}:{DistributedMessagingOptions.ConfigurationName}:{MetricsOptions.ConfigurationName}");
		builder.Services.Configure<MetricsOptions>(metricsSection);
		builder.Services.AddSingleton<IMessagingMetricsService, DefaultMessagingMetricsService>();

		// Register the default node-identity provider. Used by the publisher to stamp
		// the `cirreum.node` application property on every outbound message and by
		// the receiver (when configured) to skip self-echoes pre-deserialization.
		// TryAdd so apps that need bespoke resolution can register their own
		// INodeIdProvider before calling AddMessaging().
		builder.Services.TryAddSingleton<INodeIdProvider, DefaultNodeIdProvider>();

		// Register the DistributedMessage channel's registry. Its assembly scan runs at
		// host startup via DistributedMessageRegistryBootstrap, an ISystemInitializer the
		// Cirreum.Startup scan discovers from this assembly.
		builder.Services.TryAddSingleton<DistributedMessageRegistry>();
		builder.Services.TryAddSingleton<IDistributedMessageRegistry>(sp =>
			sp.GetRequiredService<DistributedMessageRegistry>());

		// Register the Distributed Transport Publisher
		var section = builder.Configuration.GetSection($"{DistributeMessagingStrings.ConfigurationKey}:{DistributedMessagingOptions.ConfigurationName}");
		if (section.Exists()) {
			var instanceName = section.GetValue<string>(nameof(DistributedMessagingOptions.InstanceKey));
			if (!string.IsNullOrEmpty(instanceName)) {

				// Add Configuration Options
				builder.Services
					.AddOptions<DistributedMessagingOptions>()
						.Bind(section);

				// Register the batching policy. TryAdd so apps with dynamic batching
				// needs register their own IBatchingPolicy before calling AddMessaging();
				// the default returns the channel's configured base values unchanged.
				builder.Services.TryAddSingleton<IBatchingPolicy, DefaultBatchingPolicy>();

				// Register the batch processor
				builder.Services.AddSingleton<DefaultBatchProcessor>();
				builder.Services.AddSingleton<IBatchProcessor>(sp =>
					sp.GetRequiredService<DefaultBatchProcessor>());
				builder.Services.AddSingleton<IHostedService>(sp =>
					sp.GetRequiredService<DefaultBatchProcessor>());

				// Register the delivery engine as the DistributedMessage channel's
				// transport publisher (Replace so it wins over any no-op default), and
				// the outbound Conductor bridge that routes published DistributedMessage
				// notifications through it.
				builder.Services.AddSingleton<DefaultTransportPublisher>();
				builder.Services.Replace(
					ServiceDescriptor.Singleton<IDistributedTransportPublisher<DistributedMessage>>(sp =>
						sp.GetRequiredService<DefaultTransportPublisher>()));
				builder.Services.TryAddEnumerable(
					ServiceDescriptor.Transient(
						typeof(INotificationHandler<>),
						typeof(OutboundDistributedMessageHandler<>)));
			}

			// Register the Distributed Message Receiver — conditional on the
			// Receiver configuration section being present with a valid
			// InstanceKey and at least one source (Queue or Topic+Subscription).
			var receiverSection = section.GetSection(ReceiverOptions.ConfigurationName);
			if (receiverSection.Exists()) {

				var receiverInstance = receiverSection.GetValue<string>(nameof(ReceiverOptions.InstanceKey));
				var queueName = receiverSection.GetValue<string>(nameof(ReceiverOptions.QueueName));
				var topicName = receiverSection.GetValue<string>(nameof(ReceiverOptions.TopicName));
				var subscriptionName = receiverSection.GetValue<string>(nameof(ReceiverOptions.SubscriptionName));

				var hasQueue = !string.IsNullOrEmpty(queueName);
				var hasTopicPair = !string.IsNullOrEmpty(topicName) && !string.IsNullOrEmpty(subscriptionName);

				if (!string.IsNullOrEmpty(receiverInstance) && (hasQueue || hasTopicPair)) {

					// Bind options
					builder.Services
						.AddOptions<ReceiverOptions>()
							.Bind(receiverSection);

					// Register the receiver (single instance; IHostedService).
					builder.Services.AddSingleton<DistributedMessageReceiver>();
					builder.Services.AddSingleton<IHostedService>(sp =>
						sp.GetRequiredService<DistributedMessageReceiver>());
				}
			}
		}

		return builder;

	}

}