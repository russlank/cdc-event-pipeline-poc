extern alias projector;
extern alias translator;

using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using CdcPay.Common;
using CdcPay.Common.Hosting;
using CdcPay.Contracts;
using CdcPay.PayHistory.Storage;
using CdcPay.Translation.Classification;
using CdcPay.UnitTests.PayQueryApi;
using Confluent.Kafka;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using projector::CdcPay.PayHistoryProjector;
using translator::CdcPay.EventTranslator;

namespace CdcPay.UnitTests.Diagnostics;

/// <summary>
/// The telemetry catalog: translator instruments, log event ids, span attributes, and metric
/// tags carry only bounded values, and probes stay out of traces and request logs.
/// </summary>
public sealed class TelemetryTests
{
    /// <summary>Event id range of each log catalog: the first id, then 100 ids.</summary>
    private static readonly Dictionary<string, int> LogRanges = new(StringComparer.Ordinal)
    {
        ["ServiceLogs"] = 1000,
        ["TranslatorLogs"] = 2000,
        ["ProjectorLogs"] = 3000,
    };

    private static readonly HashSet<string> SpanAttributes = new(StringComparer.Ordinal)
    {
        "messaging.system", "messaging.destination.name", "messaging.operation.type",
        "cdcpay.event_type", "cdcpay.event_id", "cdcpay.position", "cdcpay.outcome",
    };

    [Fact]
    public void TranslatorMetrics_CountByEventTypeAndReason()
    {
        using ServiceProvider services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        TranslatorStatus status = new() { OpenTransactions = 3 };
        IMeterFactory meterFactory = services.GetRequiredService<IMeterFactory>();
        TranslatorMetrics metrics = new(meterFactory, status);
        List<(string Instrument, double Value, string? Tag)> measurements = [];
        using MeterListener listener = Listen(meterFactory, TranslatorMetrics.MeterName, measurements);

        metrics.Produced(EventTypes.SalaryChanged, TimeSpan.FromMilliseconds(1500));
        metrics.DeadLettered(DeadLetterReasons.InvalidRow);
        metrics.Ignored(IgnoreReasons.Infrastructure, 2);
        metrics.Abandoned();
        listener.RecordObservableInstruments();

        Assert.Equal(
            [
                ("cdcpay.translator.events.produced", 1d, (string?)"event_type=SalaryChanged"),
                ("cdcpay.translator.end_to_end.lag", 1.5d, "event_type=SalaryChanged"),
                ("cdcpay.translator.dlq.produced", 1d, "reason=InvalidRow"),
                ("cdcpay.translator.changes.ignored", 2d, "reason=Infrastructure"),
                ("cdcpay.translator.transactions.abandoned", 1d, null),
                ("cdcpay.translator.transactions.open", 3d, null),
            ],
            measurements);
    }

    [Fact]
    public void MetricTags_AreBounded()
    {
        using ServiceProvider services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        IMeterFactory meters = services.GetRequiredService<IMeterFactory>();
        TranslatorMetrics translatorMetrics = new(meters, new TranslatorStatus());
        ProjectorMetrics projectorMetrics = new(meters);
        List<(string Instrument, double Value, string? Tag)> measurements = [];
        using MeterListener translatorListener = Listen(meters, TranslatorMetrics.MeterName, measurements);
        using MeterListener projectorListener = Listen(meters, ProjectorMetrics.MeterName, measurements);

        foreach (string eventType in EventTypes.All)
        {
            translatorMetrics.Produced(eventType, TimeSpan.FromSeconds(1));
        }

        foreach (ProjectionResult result in Enum.GetValues<ProjectionResult>())
        {
            projectorMetrics.Projected(result);
        }

        translatorMetrics.DeadLettered(DeadLetterReasons.Unparseable);
        translatorMetrics.Ignored(IgnoreReasons.GenderChange, 1);
        projectorMetrics.DeadLettered(ProjectorFailures.InvalidEvent);

        Assert.All(measurements, measurement => Assert.True(
            measurement.Instrument.StartsWith("cdcpay.", StringComparison.Ordinal)
            && (measurement.Tag is null || measurement.Tag.Split('=')[0] is "event_type" or "reason" or "result"),
            $"{measurement.Instrument} has tag {measurement.Tag}"));
    }

    [Fact]
    public void LogEventIds_AreUniqueAndInTheirCatalogsRange()
    {
        List<(string Catalog, int Id)> ids =
        [
            .. new[]
                {
                    typeof(ComponentIdentity).Assembly,
                    typeof(DeadLetterPublisher).Assembly,
                    typeof(ProjectorDeadLetters).Assembly,
                }
                .SelectMany(assembly => assembly.GetTypes())
                .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
                .Select(method => (Catalog: method.DeclaringType!.Name, Attribute: method.GetCustomAttribute<LoggerMessageAttribute>()))
                .Where(entry => entry.Attribute is not null)
                .Select(entry => (entry.Catalog, entry.Attribute!.EventId)),
        ];

        Assert.NotEmpty(ids);
        Assert.Equal(ids.Count, ids.Select(entry => entry.Id).Distinct().Count());
        Assert.All(ids, entry => Assert.True(
            LogRanges.TryGetValue(entry.Catalog, out int first) && entry.Id >= first && entry.Id < first + 100,
            $"{entry.Catalog} uses event id {entry.Id}"));
    }

    [Fact]
    public void Spans_ContinueTheTraceThroughKafkaWithBoundedAttributes()
    {
        List<Activity> spans = [];
        using ActivityListener listener = new()
        {
            ShouldListenTo = source => source.Name.StartsWith(ServiceDefaultsExtensions.TelemetryPrefix, StringComparison.Ordinal),
            Sample = (ref _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Add,
        };
        ActivitySource.AddActivityListener(listener);
        var eventId = Guid.NewGuid();

        Headers? headers;
        using (Activity? publish = TranslatorActivities.StartPublish(EventTypes.SalaryChanged, eventId))
        {
            headers = TranslatorActivities.TraceHeaders(publish);
        }

        using (Activity? process = ProjectorActivities.StartProcess(PayEventContract.Topic, $"{PayEventContract.Topic}[0]@7", headers))
        {
            ProjectorActivities.SetEvent(process, EventTypes.SalaryChanged, eventId);
            ProjectorActivities.SetOutcome(process, nameof(ProjectionResult.Applied));
        }

        Assert.Equal(2, spans.Count);
        Assert.Equal(spans[0].TraceId, spans[1].TraceId);
        Assert.Equal(spans[0].SpanId, spans[1].ParentSpanId);
        Assert.All(spans.SelectMany(span => span.TagObjects), tag => Assert.Contains(tag.Key, SpanAttributes));
        Assert.All(
            typeof(TranslatorActivities).GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Concat(typeof(ProjectorActivities).GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
                .SelectMany(method => method.GetParameters()),
            parameter => Assert.True(
                parameter.ParameterType == typeof(Activity) || parameter.ParameterType == typeof(Headers)
                || parameter.ParameterType == typeof(TopicPartitionOffset) || DiagnosticSafetyTests.IsBounded(parameter)
                || parameter.Name is "outcome",
                $"span helper takes {parameter.Name}"));
    }

    [Fact]
    public void Position_IsWrittenLikeTheTranslators()
    {
        Assert.Equal(
            "hr.employee-pay.v1[1]@1083",
            ProjectorActivities.Position(new TopicPartitionOffset(PayEventContract.Topic, new Partition(1), new Offset(1083))));
    }

    [Fact]
    public void StartProcess_WithoutTraceHeader_StartsANewTrace()
    {
        using ActivityListener listener = new()
        {
            ShouldListenTo = source => source.Name == ProjectorActivities.SourceName,
            Sample = (ref _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);

        using Activity? process = ProjectorActivities.StartProcess(PayEventContract.Topic, "position", [new Header("other", [1])]);

        Assert.NotNull(process);
        Assert.Equal(default, process.ParentSpanId);
    }

    [Theory]
    [InlineData("/health/live", true)]
    [InlineData("/health/ready", true)]
    [InlineData("/system/component-identity", true)]
    [InlineData("/tenants/1/employees/1003/salary", false)]
    [InlineData("/healthy", false)]
    public void IsOperatorPath_MatchesProbesAndIdentityOnly(string path, bool expected)
    {
        Assert.Equal(expected, ServiceDefaultsExtensions.IsOperatorPath(new PathString(path)));
    }

    [Fact]
    public async Task Probes_AreNotLoggedAsRequests()
    {
        CapturingLoggerProvider logs = new();
        await using QueryApiFactory api = new();
        using HttpClient client = api.WithWebHostBuilder(builder => builder.ConfigureLogging(logging => logging.AddProvider(logs))).CreateClient();

        foreach (string path in new[] { ServiceDefaultsExtensions.LivePath, ServiceDefaultsExtensions.ReadyPath, ServiceDefaultsExtensions.IdentityPath })
        {
            _ = (await client.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        }

        Assert.DoesNotContain(logs.Categories, category => category.StartsWith("Microsoft.AspNetCore.Hosting", StringComparison.Ordinal)
            || category.StartsWith("Microsoft.AspNetCore.Routing", StringComparison.Ordinal));
    }

    /// <summary>Listens to the named meter of this test's factory only; other tests create meters with the same names in parallel.</summary>
    private static MeterListener Listen(IMeterFactory meterFactory, string meterName, List<(string Instrument, double Value, string? Tag)> measurements)
    {
        MeterListener listener = new()
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == meterName && ReferenceEquals(instrument.Meter.Scope, meterFactory))
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => measurements.Add((instrument.Name, value, Tag(tags))));
        listener.SetMeasurementEventCallback<int>((instrument, value, tags, _) => measurements.Add((instrument.Name, value, Tag(tags))));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => measurements.Add((instrument.Name, value, Tag(tags))));
        listener.Start();
        return listener;
    }

    private static string? Tag(ReadOnlySpan<KeyValuePair<string, object?>> tags) => tags.Length == 0 ? null : $"{tags[0].Key}={tags[0].Value}";

    /// <summary>Records the category of every log entry written at Information or above.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _categories = new();

        public IEnumerable<string> Categories => _categories;

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _categories);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(string category, System.Collections.Concurrent.ConcurrentQueue<string> categories) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                categories.Enqueue(category);
        }
    }
}
