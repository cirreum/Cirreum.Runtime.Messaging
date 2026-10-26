# Cirreum.Runtime.Messaging 3.1.0 — messaging metrics under the Cirreum namespace, in seconds

## Why this release exists

The messaging meter was the last Cirreum telemetry surface that did not follow the platform's
instrument conventions:

| Problem | Effect |
| ------- | ------ |
| Instruments named `messaging.*` | That namespace belongs to the OpenTelemetry messaging semantic conventions, which the Azure Service Bus instrumentation also emits into. Cirreum's series could sit beside — or collide with — the SDK's own. |
| Time histograms declared no unit | Backends could not label or convert them; the descriptions said "(ms)" but nothing machine-readable did. |
| Durations in milliseconds | Every other Cirreum duration histogram, and every .NET built-in one, records seconds. |
| `messaging.batches.partial.total` | A Prometheus exporter appends `_total` to counters, so it exported as `…_total_total`. |
| `{%}` as a unit | The UCUM unit for percent is `%`; braces mark an annotation, not a unit. |

## What changed

| Before | After | Unit |
| ------ | ----- | ---- |
| `messaging.messages.received` | `cirreum.messaging.messages.received` | `{message}` |
| `messaging.messages.queued` | `cirreum.messaging.messages.queued` | `{message}` |
| `messaging.messages.delivered` | `cirreum.messaging.messages.delivered` | `{message}` |
| `messaging.messages.failed` | `cirreum.messaging.messages.failed` | `{message}` |
| `messaging.processor.queue_time` | `cirreum.messaging.enqueue.duration` | `s` |
| `messaging.processor.queue_wait_time` | `cirreum.messaging.queue_wait.duration` | `s` |
| `messaging.batch.publish_time` | `cirreum.messaging.publish.duration` | `s` |
| `messaging.batch.processing_time` | `cirreum.messaging.batch.processing.duration` | `s` |
| `messaging.batches.partial.total` | `cirreum.messaging.batches.partial` | `{batch}` |
| `messaging.batches.partial.size` | `cirreum.messaging.batches.partial.size` | `{message}` |
| `messaging.batches.partial.percentage` | `cirreum.messaging.batches.partial.percentage` | `%` |
| `messaging.batch.fullness` | `cirreum.messaging.batch.fullness` | `%` |
| `messaging.batches.processed` | `cirreum.messaging.batches.processed` | `{batch}` |
| `messaging.processor.queue_depth` | `cirreum.messaging.queue.depth` | `{message}` |

The four duration histograms take their unit and bucket boundaries from `CirreumTelemetry.Histograms`
in `Cirreum.Kernel` — `0.005` to `10` seconds, the boundaries of the ASP.NET Core and `HttpClient`
request-duration histograms.

### Tags

| Before | After | Why |
| ------ | ----- | --- |
| `error_type` = `TimeoutException` | `error.type` = `System.TimeoutException` | The semantic-conventions key, with the fully qualified type |
| `batchCapacity` | `batch_state` | Its values — `full`, `partial`, `empty`, `overflow` — describe the batch, not its capacity |

`target`, `priority`, `delivery_mode`, and `metric_type` are unchanged.

## Compatibility

* **No signature changes.** `IMessagingMetricsService` still takes milliseconds and converts when
  it records. The meter name, `Cirreum.Messaging`, is unchanged, so `AddMessaging()` still
  registers it with OpenTelemetry and no exporter configuration changes.
* **Dashboards and alerts** on the old names need the new names. Queries on an old name return
  nothing rather than mixing units. Duration thresholds change scale — a 250 ms alert is now
  `0.25`.
* **Queries filtering on `error_type` or `batchCapacity`** need the new keys, and `error.type`
  values now carry the namespace.
