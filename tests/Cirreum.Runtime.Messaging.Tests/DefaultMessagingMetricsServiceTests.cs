namespace Cirreum.Runtime.Messaging.Tests;

using Cirreum.Conductor;
using Cirreum.Diagnostics;
using Cirreum.Messaging;
using Cirreum.Runtime.Messaging.Metrics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Diagnostics.Metrics;

/// <summary>
/// Pins the messaging instrument contract: names under <c>cirreum.messaging.*</c>, a declared unit
/// on every instrument, and seconds on every duration histogram.
/// </summary>
/// <remarks>
/// Each service instance owns a meter named <c>Cirreum.Messaging</c>, and other test classes
/// construct instances in parallel. Measurement tests therefore record a value no other test
/// uses and assert on that value.
/// </remarks>
public class DefaultMessagingMetricsServiceTests {

	private const string MeterName = "Cirreum.Messaging";

	private sealed record Measurement(Instrument Instrument, double Value, KeyValuePair<string, object?>[] Tags);

	private static DefaultMessagingMetricsService CreateService() =>
		new(
			NullLogger<DefaultMessagingMetricsService>.Instance,
			Options.Create(new MetricsOptions { EnablePeriodicReporting = false }),
			Substitute.For<IConductor>());

	private static (MeterListener Listener, List<Instrument> Instruments, List<Measurement> Measurements) Listen() {
		var instruments = new List<Instrument>();
		var measurements = new List<Measurement>();
		var listener = new MeterListener {
			InstrumentPublished = (instrument, l) => {
				if (instrument.Meter.Name == MeterName) {
					lock (instruments) {
						instruments.Add(instrument);
					}
					l.EnableMeasurementEvents(instrument);
				}
			}
		};
		listener.SetMeasurementEventCallback<double>((i, v, t, _) => Add(i, v, t));
		listener.SetMeasurementEventCallback<long>((i, v, t, _) => Add(i, v, t));
		listener.SetMeasurementEventCallback<int>((i, v, t, _) => Add(i, v, t));
		listener.Start();
		return (listener, instruments, measurements);

		void Add(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags) {
			var copy = tags.ToArray();
			lock (measurements) {
				measurements.Add(new(instrument, value, copy));
			}
		}
	}

	[Fact]
	public void Instruments_AreNamespacedAndDeclareUnits() {
		var (listener, instruments, _) = Listen();
		using (listener)
		using (CreateService()) {
		}

		instruments.Should().HaveCount(14);
		instruments.Should().AllSatisfy(i => {
			i.Name.Should().StartWith("cirreum.messaging.");
			i.Name.Should().NotEndWith(".total");
			i.Unit.Should().NotBeNullOrEmpty();
		});

		var durations = instruments.Where(i => i.Name.EndsWith(".duration")).ToList();
		durations.Select(i => i.Name).Should().BeEquivalentTo([
			"cirreum.messaging.enqueue.duration",
			"cirreum.messaging.queue_wait.duration",
			"cirreum.messaging.publish.duration",
			"cirreum.messaging.batch.processing.duration"]);
		durations.Should().AllSatisfy(i => {
			i.Unit.Should().Be("s");
			((Histogram<double>)i).Advice!.HistogramBucketBoundaries
				.Should().Equal(CirreumTelemetry.Histograms.DurationAdvice.HistogramBucketBoundaries);
		});

		instruments.Where(i => i.Name.EndsWith(".percentage") || i.Name.EndsWith(".fullness"))
			.Should().AllSatisfy(i => i.Unit.Should().Be("%"));
	}

	[Fact]
	public void RecordMessageQueued_RecordsSeconds() {
		var (listener, _, measurements) = Listen();
		using (listener)
		using (var service = CreateService()) {
			service.RecordMessageQueued("probe", MessageTarget.Queue, queueTimeMs: 1234, DistributedMessagePriority.Standard);
		}

		measurements.Should().Contain(m =>
			m.Instrument.Name == "cirreum.messaging.enqueue.duration" && m.Value == 1.234);
	}

	[Fact]
	public void RecordMessageFailed_TagsErrorTypePerTheSemanticConventions() {
		const string errorType = "System.Probe.MessagingFailureException";
		var (listener, _, measurements) = Listen();
		using (listener)
		using (var service = CreateService()) {
			service.RecordMessageFailed("probe", MessageTarget.Topic, errorType, processingTimeMs: 5);
		}

		measurements.Where(m => m.Tags.Any(t => Equals(t.Value, errorType)))
			.Should().NotBeEmpty()
			.And.AllSatisfy(m => m.Tags.Select(t => t.Key).Should().Contain("error.type").And.NotContain("error_type"));
	}

	[Fact]
	public void RecordPartialBatch_TagsTheBatchState() {
		var (listener, _, measurements) = Listen();
		using (listener)
		using (var service = CreateService()) {
			service.RecordPartialBatch(batchCapacity: 977, batchSize: 3);
		}

		var size = measurements.Single(m => m.Instrument.Name == "cirreum.messaging.batches.partial.size" && m.Value == 3
			&& m.Tags.Any(t => t.Key == "batch_state"));
		size.Tags.Should().Contain(t => t.Key == "batch_state" && Equals(t.Value, "partial"));
		size.Tags.Select(t => t.Key).Should().NotContain("batchCapacity");
	}

}
