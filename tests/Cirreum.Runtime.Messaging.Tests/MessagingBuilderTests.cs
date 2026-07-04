namespace Cirreum.Runtime.Messaging.Tests;

using Cirreum.Messaging.Batching;
using Cirreum.Runtime.Messaging;
using Microsoft.Extensions.DependencyInjection;

public class MessagingBuilderTests {

	[Fact]
	public void UseBatchingPolicy_ReplacesAnExistingRegistration() {
		var services = new ServiceCollection();
		services.AddSingleton<IBatchingPolicy, DefaultBatchingPolicy>();
		var builder = new MessagingBuilder(services);

		builder.UseBatchingPolicy<StubBatchingPolicy>();

		using var provider = services.BuildServiceProvider();
		provider.GetRequiredService<IBatchingPolicy>().Should().BeOfType<StubBatchingPolicy>();
		services.Count(d => d.ServiceType == typeof(IBatchingPolicy)).Should().Be(1);
	}

	[Fact]
	public void UseTimeOfDayBatching_RegistersAValidatedPolicyInstance() {
		var services = new ServiceCollection();
		var builder = new MessagingBuilder(services);

		builder.UseTimeOfDayBatching(schedule => schedule.Rules.Add(new() {
			Days = [DayOfWeek.Monday],
			StartHour = 8,
			EndHour = 18,
			ScalingFactor = 0.5
		}));

		using var provider = services.BuildServiceProvider();
		provider.GetRequiredService<IBatchingPolicy>().Should().BeOfType<TimeOfDayBatchingPolicy>();
	}

	[Fact]
	public void UseTimeOfDayBatching_InvalidSchedule_FailsAtCompositionTime() {
		var builder = new MessagingBuilder(new ServiceCollection());

		var act = () => builder.UseTimeOfDayBatching(schedule => schedule.Rules.Add(new() {
			Days = [],
			ScalingFactor = 0.5
		}));

		act.Should().Throw<ArgumentException>();
	}

	[Fact]
	public void Services_ExposesTheUnderlyingCollection() {
		var services = new ServiceCollection();

		new MessagingBuilder(services).Services.Should().BeSameAs(services);
	}

}