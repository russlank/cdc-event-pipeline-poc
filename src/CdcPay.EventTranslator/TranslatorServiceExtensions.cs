using CdcPay.Common.Hosting;
using CdcPay.Contracts.Events;
using CdcPay.Translation.Classification;
using Confluent.Kafka;
using Confluent.SchemaRegistry;
using Confluent.SchemaRegistry.Serdes;
using Microsoft.Extensions.Options;

namespace CdcPay.EventTranslator;

/// <summary>Composition of the translator host.</summary>
public static class TranslatorServiceExtensions
{
    public static IHostApplicationBuilder AddEventTranslator(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        _ = builder.Services.AddOptions<KafkaOptions>()
            .Bind(builder.Configuration.GetSection(KafkaOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        _ = builder.Services.AddOptions<TranslationOptions>()
            .Bind(builder.Configuration.GetSection(TranslationOptions.SectionName))
            .ValidateOnStart();

        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<TranslatorStatus>();
        builder.Services.AddSingleton<TranslatorMetrics>();
        builder.Services.AddSingleton<ISchemaRegistryClient>(services =>
            new CachedSchemaRegistryClient(new SchemaRegistryConfig { Url = services.GetRequiredService<IOptions<KafkaOptions>>().Value.SchemaRegistryUrl }));

        builder.Services.AddSingleton(services =>
        {
            KafkaOptions kafka = services.GetRequiredService<IOptions<KafkaOptions>>().Value;
            // Never auto-register: only schemas registered from contracts/ may be used. The
            // serializer looks up the id of the generated schema itself (UseLatestVersion would
            // stamp the newest id on data written with an older schema).
            AvroSerializerConfig avro = new() { AutoRegisterSchemas = false, UseLatestVersion = false };
            return new ProducerBuilder<string, PayEvent>(ProducerConfig(kafka, "events"))
                .SetValueSerializer(new AvroSerializer<PayEvent>(services.GetRequiredService<ISchemaRegistryClient>(), avro))
                .Build();
        });
        builder.Services.AddSingleton(services =>
            new ProducerBuilder<byte[]?, byte[]>(ProducerConfig(services.GetRequiredService<IOptions<KafkaOptions>>().Value, "dlq")).Build());
        builder.Services.AddSingleton<DeadLetterPublisher>();
        builder.Services.AddHostedService<TranslatorWorker>();

        builder.AddServiceDefaults(typeof(TranslatorWorker).Assembly)
            .AddCheck<TranslatorReadinessCheck>("kafka-consumer", tags: [ServiceDefaultsExtensions.ReadyTag]);
        return builder;
    }

    private static ProducerConfig ProducerConfig(KafkaOptions kafka, string purpose) => new()
    {
        BootstrapServers = kafka.BootstrapServers,
        ClientId = $"{kafka.ConsumerGroup}-{purpose}",
        EnableIdempotence = true,
        Acks = Acks.All,
        LingerMs = 5,
    };
}
