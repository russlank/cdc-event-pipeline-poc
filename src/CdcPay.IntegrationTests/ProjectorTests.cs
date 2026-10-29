using CdcPay.Contracts;
using CdcPay.Contracts.Events;
using CdcPay.PayHistory;
using CdcPay.PayHistory.Storage;
using CdcPay.PayHistoryProjector;
using Confluent.Kafka;
using Confluent.SchemaRegistry;
using Confluent.SchemaRegistry.Serdes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace CdcPay.IntegrationTests;

/// <summary>The projector host against real Kafka, Schema Registry, and PostgreSQL.</summary>
public sealed class ProjectorTests(PipelineFixture pipeline)
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task Projector_FixtureStream_BuildsTheBitemporalHistory()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string topic = await pipeline.CreateTopicAsync(cancellationToken);
        string database = await pipeline.CreateDatabaseAsync(cancellationToken);
        await ProduceAsync(topic, [.. FixtureStream.Events, FixtureStream.Events[2]], cancellationToken);

        using (IHost host = await StartProjectorAsync(topic, "it-projector", database, cancellationToken))
        {
            await WaitForCommittedEndAsync(topic, "it-projector", cancellationToken);
            await host.StopAsync(cancellationToken);
        }

        await using var dataSource = NpgsqlDataSource.Create(database);
        PayHistoryReader reader = new(dataSource);
        Assert.Equal(FixtureStream.Events.Count, await CountAsync(dataSource, "processed_event", cancellationToken));

        Assert.Equal(42500m, await AmountAsync(reader, 1003, new DateOnly(2026, 8, 1), null));
        Assert.Equal(42000m, await AmountAsync(reader, 1003, new DateOnly(2026, 8, 1), At(FixtureStream.RetroactiveRaise).AddMinutes(1)));
        Assert.Equal(40000m, await AmountAsync(reader, 1003, new DateOnly(2026, 8, 1), At(FixtureStream.Hire)));
        Assert.Equal(40000m, await AmountAsync(reader, 1003, new DateOnly(2026, 6, 1), null));
        Assert.Equal(38000m, await AmountAsync(reader, 1101, new DateOnly(2026, 12, 31), null));
        Assert.Null(await AmountAsync(reader, 1101, new DateOnly(2027, 1, 1), null));

        IReadOnlyList<PayHistoryRow> history = await reader.HistoryAsync(1, 1003, cancellationToken);
        Assert.Equal(4, history.Count);
        HashSet<DateTimeOffset> eventInstants = [.. FixtureStream.Events.Select(e => At(e.recordedAt))];
        Assert.All(history, row => Assert.Contains(row.RecordedFrom, eventInstants));
        Assert.All(history, row => Assert.True(row.RecordedTo is null || eventInstants.Contains(row.RecordedTo.Value)));
    }

    [Fact]
    public async Task Projector_ReplayFromTheStart_ChangesNothing()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string topic = await pipeline.CreateTopicAsync(cancellationToken);
        string database = await pipeline.CreateDatabaseAsync(cancellationToken);
        await ProduceAsync(topic, FixtureStream.Events, cancellationToken);
        await using var dataSource = NpgsqlDataSource.Create(database);
        PayHistoryReader reader = new(dataSource);

        IReadOnlyList<PayHistoryRow> before = await ProjectAsync("it-first");
        IReadOnlyList<PayHistoryRow> after = await ProjectAsync("it-replay");

        Assert.NotEmpty(before);
        Assert.Equal(before, after);
        Assert.Equal(FixtureStream.Events.Count, await CountAsync(dataSource, "processed_event", cancellationToken));

        async Task<IReadOnlyList<PayHistoryRow>> ProjectAsync(string group)
        {
            using IHost host = await StartProjectorAsync(topic, group, database, cancellationToken);
            await WaitForCommittedEndAsync(topic, group, cancellationToken);
            await host.StopAsync(cancellationToken);
            return [.. (await reader.HistoryAsync(1, 1003, cancellationToken)).Concat(await reader.HistoryAsync(1, 1101, cancellationToken))];
        }
    }

    [Fact]
    public async Task Projector_PoisonMessages_AreDeadLetteredAndProjectionContinues()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string topic = await pipeline.CreateTopicAsync(cancellationToken);
        string deadLetters = await pipeline.CreateDeadLetterTopicAsync();
        string database = await pipeline.CreateDatabaseAsync(cancellationToken);
        byte[] notAvro = "not an Avro business event"u8.ToArray();
        PayEvent valid = FixtureStream.Events[0];
        // A SalaryChanged whose payload is a termination: valid Avro, not a usable event.
        PayEvent template = FixtureStream.Events[2];
        PayEvent mismatched = new()
        {
            eventId = Guid.NewGuid(),
            eventType = EventTypes.SalaryChanged,
            schemaVersion = template.schemaVersion,
            tenantId = template.tenantId,
            employeeId = template.employeeId,
            effectiveFrom = template.effectiveFrom,
            recordedAt = template.recordedAt,
            isRetroactive = template.isRetroactive,
            source = template.source,
            payload = new EmployeeTerminatedPayload { terminationDate = template.effectiveFrom },
        };

        // Same key, so all three land on one partition in this order.
        using (IProducer<byte[], byte[]> raw = new ProducerBuilder<byte[], byte[]>(new ProducerConfig { BootstrapServers = pipeline.BootstrapServers }).Build())
        {
            _ = await raw.ProduceAsync(topic, new Message<byte[], byte[]> { Key = "1:1003"u8.ToArray(), Value = notAvro }, cancellationToken);
        }

        await ProduceAsync(topic, [mismatched, valid], cancellationToken);

        using (IHost host = await StartProjectorAsync(topic, "it-poison", database, cancellationToken, deadLetters))
        {
            await WaitForCommittedEndAsync(topic, "it-poison", cancellationToken);
            await host.StopAsync(cancellationToken);
        }

        List<Message<byte[]?, byte[]>> dead = ReadAll(deadLetters, expected: 2);
        Assert.Equal(
            [(ProjectorFailures.Undeserializable, ProjectorDeadLetters.Component, topic), (ProjectorFailures.InvalidEvent, ProjectorDeadLetters.Component, topic)],
            dead.Select(message => (Header(message, DeadLetterHeaders.Reason), Header(message, DeadLetterHeaders.Component), Header(message, DeadLetterHeaders.SourceTopic))));
        Assert.Equal(notAvro, dead[0].Value);
        Assert.Equal("1:1003"u8.ToArray(), dead[0].Key);

        await using var dataSource = NpgsqlDataSource.Create(database);
        Assert.Single(await new PayHistoryReader(dataSource).HistoryAsync(1, 1003, cancellationToken));
        Assert.Equal(1, await CountAsync(dataSource, "processed_event", cancellationToken));
    }

    private List<Message<byte[]?, byte[]>> ReadAll(string topic, int expected)
    {
        using IConsumer<byte[]?, byte[]> consumer = new ConsumerBuilder<byte[]?, byte[]>(new ConsumerConfig
        {
            BootstrapServers = pipeline.BootstrapServers,
            GroupId = $"it-read-{Guid.NewGuid():N}",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
        }).Build();
        consumer.Subscribe(topic);
        List<Message<byte[]?, byte[]>> messages = [];
        DateTimeOffset deadline = DateTimeOffset.UtcNow + Deadline;
        while (messages.Count < expected && DateTimeOffset.UtcNow < deadline)
        {
            if (consumer.Consume(TimeSpan.FromMilliseconds(500)) is { Message: { } message })
            {
                messages.Add(message);
            }
        }

        consumer.Close();
        Assert.Equal(expected, messages.Count);
        return messages;
    }

    private static string Header(Message<byte[]?, byte[]> message, string name) =>
        System.Text.Encoding.UTF8.GetString(message.Headers.GetLastBytes(name));

    private async Task<IHost> StartProjectorAsync(
        string topic, string group, string database, CancellationToken cancellationToken, string? deadLetterTopic = null)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        _ = builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Kafka:BootstrapServers"] = pipeline.BootstrapServers,
            ["Kafka:SchemaRegistryUrl"] = pipeline.SchemaRegistryUrl,
            ["Kafka:Topic"] = topic,
            ["Kafka:ConsumerGroup"] = group,
            ["Kafka:DeadLetterTopic"] = deadLetterTopic ?? PayEventContract.DeadLetterTopic,
            ["Kafka:CommitInterval"] = "00:00:00.200",
            [$"ConnectionStrings:{ProjectorServiceExtensions.ReadModelConnection}"] = database,
        });
        _ = builder.AddPayHistoryProjector();
        IHost host = builder.Build();
        await host.StartAsync(cancellationToken);
        return host;
    }

    private async Task ProduceAsync(string topic, IEnumerable<PayEvent> events, CancellationToken cancellationToken)
    {
        using CachedSchemaRegistryClient registry = new(new SchemaRegistryConfig { Url = pipeline.SchemaRegistryUrl });
        using IProducer<string, PayEvent> producer = new ProducerBuilder<string, PayEvent>(
                new ProducerConfig { BootstrapServers = pipeline.BootstrapServers, EnableIdempotence = true })
            .SetValueSerializer(new AvroSerializer<PayEvent>(registry, new AvroSerializerConfig { AutoRegisterSchemas = false, UseLatestVersion = false }))
            .Build();
        foreach (PayEvent payEvent in events)
        {
            _ = await producer.ProduceAsync(
                topic,
                new Message<string, PayEvent> { Key = PayEventContract.Key(payEvent.tenantId, payEvent.employeeId), Value = payEvent },
                cancellationToken);
        }
    }

    /// <summary>Waits until the group's committed offset of every partition is the partition's end.</summary>
    private async Task WaitForCommittedEndAsync(string topic, string group, CancellationToken cancellationToken)
    {
        using IConsumer<Ignore, Ignore> probe = new ConsumerBuilder<Ignore, Ignore>(
            new ConsumerConfig { BootstrapServers = pipeline.BootstrapServers, GroupId = group, EnableAutoCommit = false }).Build();
        TopicPartition[] partitions = [.. Enumerable.Range(0, 3).Select(partition => new TopicPartition(topic, partition))];
        long[] ends = [.. partitions.Select(partition => probe.QueryWatermarkOffsets(partition, TimeSpan.FromSeconds(10)).High.Value)];

        DateTimeOffset deadline = DateTimeOffset.UtcNow + Deadline;
        while (true)
        {
            List<TopicPartitionOffset> committed = probe.Committed(partitions, TimeSpan.FromSeconds(10));
            if (committed.Select((offset, i) => offset.Offset.Value == ends[i] || ends[i] == 0).All(done => done))
            {
                return;
            }

            Assert.True(DateTimeOffset.UtcNow < deadline, $"group {group} did not commit the end of {topic}: {string.Join(", ", committed)}");
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }
    }

    private static async Task<decimal?> AmountAsync(PayHistoryReader reader, int employeeId, DateOnly validAt, DateTimeOffset? knownAt)
    {
        IReadOnlyList<PayHistoryRow> rows = await reader.SalaryAsync(1, employeeId, validAt, knownAt, TestContext.Current.CancellationToken);
        return rows.SingleOrDefault()?.MonthlyAmount;
    }

    private static async Task<long> CountAsync(NpgsqlDataSource dataSource, string table, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = dataSource.CreateCommand($"SELECT count(*) FROM {table}");
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static DateTimeOffset At(DateTime utc) => new(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
}
