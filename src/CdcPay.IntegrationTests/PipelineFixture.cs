using System.Net;
using System.Net.Sockets;
using CdcPay.Contracts;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Confluent.SchemaRegistry;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Npgsql;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(CdcPay.IntegrationTests.PipelineFixture))]

namespace CdcPay.IntegrationTests;

/// <summary>
/// Kafka, Schema Registry, and PostgreSQL for the integration tests. Started with Testcontainers,
/// or taken from <c>CDCPAY_IT_KAFKA</c>, <c>CDCPAY_IT_SCHEMA_REGISTRY</c>, and
/// <c>CDCPAY_IT_POSTGRES</c> (a connection string) when all three are set, as in CI services.
/// Each test gets its own topic, subject, and database, so tests share the services safely.
/// </summary>
public sealed class PipelineFixture : IAsyncLifetime
{
    private const string KafkaImage = "apache/kafka:4.3.2";
    private const string SchemaRegistryImage = "confluentinc/cp-schema-registry:8.3.2";
    private const string PostgresImage = "postgres:18.4-alpine";

    private readonly List<IAsyncDisposable> _resources = [];

    public string BootstrapServers { get; private set; } = string.Empty;

    public string SchemaRegistryUrl { get; private set; } = string.Empty;

    private string PostgresConnectionString { get; set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        string? kafka = Environment.GetEnvironmentVariable("CDCPAY_IT_KAFKA");
        string? registry = Environment.GetEnvironmentVariable("CDCPAY_IT_SCHEMA_REGISTRY");
        string? postgres = Environment.GetEnvironmentVariable("CDCPAY_IT_POSTGRES");
        if (!string.IsNullOrEmpty(kafka) && !string.IsNullOrEmpty(registry) && !string.IsNullOrEmpty(postgres))
        {
            (BootstrapServers, SchemaRegistryUrl, PostgresConnectionString) = (kafka, registry, postgres);
            await WaitForExternalServicesAsync();
            return;
        }

        await StartContainersAsync();
    }

    public async ValueTask DisposeAsync()
    {
        for (int i = _resources.Count - 1; i >= 0; i--)
        {
            await _resources[i].DisposeAsync();
        }
    }

    /// <summary>A new topic with the business topic's partition count; its value subject has every contract version.</summary>
    public async Task<string> CreateTopicAsync(CancellationToken cancellationToken)
    {
        string topic = $"it.employee-pay.{Guid.NewGuid():N}";
        using (IAdminClient admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = BootstrapServers }).Build())
        {
            await admin.CreateTopicsAsync([new TopicSpecification { Name = topic, NumPartitions = 3, ReplicationFactor = 1 }]);
        }

        using CachedSchemaRegistryClient registryClient = new(new SchemaRegistryConfig { Url = SchemaRegistryUrl });
        foreach (int version in new[] { 1, 2 })
        {
            using Stream stream = typeof(PayEventContract).Assembly.GetManifestResourceStream($"contracts.avro.v{version}.PayEvent.avsc")
                ?? throw new InvalidOperationException($"contract v{version} is not embedded");
            using StreamReader reader = new(stream);
            _ = await registryClient.RegisterSchemaAsync($"{topic}-value", new Schema(await reader.ReadToEndAsync(cancellationToken), SchemaType.Avro));
        }

        return topic;
    }

    /// <summary>A new single-partition topic for dead letters.</summary>
    public async Task<string> CreateDeadLetterTopicAsync()
    {
        string topic = $"it.dlq.{Guid.NewGuid():N}";
        using IAdminClient admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = BootstrapServers }).Build();
        await admin.CreateTopicsAsync([new TopicSpecification { Name = topic, NumPartitions = 1, ReplicationFactor = 1 }]);
        return topic;
    }

    /// <summary>A new, empty database; returns its connection string.</summary>
    public async Task<string> CreateDatabaseAsync(CancellationToken cancellationToken)
    {
        string name = $"it_{Guid.NewGuid():N}";
        await using (var server = NpgsqlDataSource.Create(PostgresConnectionString))
        await using (NpgsqlCommand command = server.CreateCommand($"CREATE DATABASE {name}"))
        {
            _ = await command.ExecuteNonQueryAsync(cancellationToken);
        }

        return new NpgsqlConnectionStringBuilder(PostgresConnectionString) { Database = name }.ConnectionString;
    }

    private async Task StartContainersAsync()
    {
        INetwork network = new NetworkBuilder().Build();
        _resources.Add(network);
        await network.CreateAsync();

        // The advertised host listener must name the host port before the broker starts.
        int kafkaPort = FreeTcpPort();
        IContainer kafka = new ContainerBuilder(KafkaImage)
            .WithNetwork(network)
            .WithNetworkAliases("kafka")
            .WithPortBinding(kafkaPort, 29092)
            .WithEnvironment(new Dictionary<string, string>
            {
                ["CLUSTER_ID"] = "QTEGCfpKUmmNZpjgXb67cA",
                ["KAFKA_NODE_ID"] = "1",
                ["KAFKA_PROCESS_ROLES"] = "broker,controller",
                ["KAFKA_LISTENERS"] = "PLAINTEXT://:9092,CONTROLLER://:9093,HOST://:29092",
                ["KAFKA_ADVERTISED_LISTENERS"] = $"PLAINTEXT://kafka:9092,HOST://127.0.0.1:{kafkaPort}",
                ["KAFKA_LISTENER_SECURITY_PROTOCOL_MAP"] = "CONTROLLER:PLAINTEXT,PLAINTEXT:PLAINTEXT,HOST:PLAINTEXT",
                ["KAFKA_CONTROLLER_LISTENER_NAMES"] = "CONTROLLER",
                ["KAFKA_INTER_BROKER_LISTENER_NAME"] = "PLAINTEXT",
                ["KAFKA_CONTROLLER_QUORUM_VOTERS"] = "1@kafka:9093",
                ["KAFKA_OFFSETS_TOPIC_REPLICATION_FACTOR"] = "1",
                ["KAFKA_TRANSACTION_STATE_LOG_REPLICATION_FACTOR"] = "1",
                ["KAFKA_TRANSACTION_STATE_LOG_MIN_ISR"] = "1",
                ["KAFKA_GROUP_INITIAL_REBALANCE_DELAY_MS"] = "0",
                ["KAFKA_HEAP_OPTS"] = "-Xms512m -Xmx512m",
            })
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Kafka Server started"))
            .Build();
        _resources.Add(kafka);

        IContainer registry = new ContainerBuilder(SchemaRegistryImage)
            .WithNetwork(network)
            .WithPortBinding(8081, assignRandomHostPort: true)
            .WithEnvironment(new Dictionary<string, string>
            {
                ["SCHEMA_REGISTRY_HOST_NAME"] = "schema-registry",
                ["SCHEMA_REGISTRY_LISTENERS"] = "http://0.0.0.0:8081",
                ["SCHEMA_REGISTRY_KAFKASTORE_BOOTSTRAP_SERVERS"] = "PLAINTEXT://kafka:9092",
                ["SCHEMA_REGISTRY_KAFKASTORE_TOPIC_REPLICATION_FACTOR"] = "1",
            })
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request.ForPort(8081).ForPath("/subjects")))
            .Build();
        _resources.Add(registry);

        PostgreSqlContainer postgres = new PostgreSqlBuilder(PostgresImage).Build();
        _resources.Add(postgres);

        await Task.WhenAll(kafka.StartAsync(), postgres.StartAsync());
        await registry.StartAsync();

        BootstrapServers = $"127.0.0.1:{kafkaPort}";
        SchemaRegistryUrl = $"http://{registry.Hostname}:{registry.GetMappedPublicPort(8081)}";
        PostgresConnectionString = postgres.GetConnectionString();
    }

    /// <summary>CI service containers start with the pipeline, not when they are ready: retry for two minutes.</summary>
    private async Task WaitForExternalServicesAsync()
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(2);
        using HttpClient http = new() { Timeout = TimeSpan.FromSeconds(5) };
        while (true)
        {
            try
            {
                using (IAdminClient admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = BootstrapServers }).Build())
                {
                    _ = admin.GetMetadata(TimeSpan.FromSeconds(5));
                }

                using (HttpResponseMessage response = await http.GetAsync(new Uri($"{SchemaRegistryUrl}/subjects")))
                {
                    _ = response.EnsureSuccessStatusCode();
                }

                await using var server = NpgsqlDataSource.Create(PostgresConnectionString);
                await using NpgsqlConnection connection = await server.OpenConnectionAsync();
                return;
            }
            catch (Exception exception) when (exception is KafkaException or HttpRequestException or TaskCanceledException or NpgsqlException
                && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }
    }

    private static int FreeTcpPort()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
