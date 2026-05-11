namespace Microsoft.AspNetCore.Hosting;

using Cirreum.Messaging;
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
using Microsoft.Extensions.Options;

public static class HostingExtensions {

	private class ConfigureMessagingMarker { }

	/// <summary>
	/// Add support for messaging (Service Bus) by registering any configured providers
	/// and associated instances.
	/// </summary>
	public static IHostApplicationBuilder AddMessaging(this IHostApplicationBuilder builder) {

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
		var metricsSection = builder.Configuration.GetSection($"{DistributeMessagingStrings.ConfigurationKey}:{DistributionOptions.ConfigurationName}:{MetricsOptions.ConfigurationName}");
		builder.Services.Configure<MetricsOptions>(metricsSection);
		builder.Services.AddSingleton<IMessagingMetricsService, DefaultMessagingMetricsService>();

		// Register the default node-identity provider. Used by the publisher to stamp
		// the `cirreum.node` application property on every outbound message and by
		// the receiver (when configured) to skip self-echoes pre-deserialization.
		// TryAdd so apps that need bespoke resolution can register their own
		// INodeIdProvider before calling AddMessaging().
		builder.Services.TryAddSingleton<INodeIdProvider, DefaultNodeIdProvider>();

		// Register the Distributed Transport Publisher
		var section = builder.Configuration.GetSection($"{DistributeMessagingStrings.ConfigurationKey}:{DistributionOptions.ConfigurationName}");
		if (section.Exists()) {
			var instanceName = section.GetValue<string>($"{DistributionOptions.SenderInstanceConfigurationName}");
			if (!string.IsNullOrEmpty(instanceName)) {

				// Add Configuration Options
				builder.Services
					.AddOptions<DistributionOptions>()
						.Bind(section)
						.ValidateDataAnnotations()
					.Services.AddSingleton<IValidateOptions<DistributionOptions>, TimeBatchingValidation>();

				// Register the batch processor
				builder.Services.AddSingleton<DefaultBatchProcessor>();
				builder.Services.AddSingleton<IBatchProcessor>(sp =>
					sp.GetRequiredService<DefaultBatchProcessor>());
				builder.Services.AddSingleton<IHostedService>(sp =>
					sp.GetRequiredService<DefaultBatchProcessor>());

				// Register the distributed publisher
				builder.Services.Replace(
					ServiceDescriptor.Describe(
						typeof(IDistributedTransportPublisher),
						typeof(DefaultTransportPublisher),
						ServiceLifetime.Singleton)
					);
			}

			// Register the Distributed Message Receiver — conditional on the
			// Receiver configuration section being present with a valid
			// InstanceKey and at least one source (Queue or Topic+Subscription).
			var receiverSection = section.GetSection(ReceiverOptions.ConfigurationName);
			if (receiverSection.Exists()) {

				var receiverInstance = receiverSection.GetValue<string>(ReceiverOptions.ReceiverInstanceConfigurationName);
				var queueName = receiverSection.GetValue<string>(ReceiverOptions.QueueConfigurationName);
				var topicName = receiverSection.GetValue<string>(ReceiverOptions.TopicConfigurationName);
				var subscriptionName = receiverSection.GetValue<string>(ReceiverOptions.SubscriptionConfigurationName);

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