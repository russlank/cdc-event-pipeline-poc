using Avro;
using Avro.Generic;
using Avro.IO;
using Avro.Specific;
using CdcPay.Contracts;
using CdcPay.Contracts.Events;
using CdcPay.Translation.Classification;
using static CdcPay.UnitTests.Translation.Changes;

namespace CdcPay.UnitTests.Contracts;

/// <summary>
/// Contract v1 → v2 (payComponent with default "Base") resolved in both directions with
/// Avro schema resolution, as consumers on either version would read the topic.
/// </summary>
public sealed class ContractEvolutionTests
{
    private static readonly Schema V1 = Contract(1);
    private static readonly Schema V2 = Contract(2);

    [Fact]
    public void V1Reader_ReadsV2Data_IgnoringTheNewField()
    {
        byte[] v2Bytes = WriteSpecific(SalaryChangedEvent("Allowance"));

        GenericRecord asV1 = ReadGeneric(v2Bytes, writer: V2, reader: V1);

        var payload = (GenericRecord)asV1["payload"];
        Assert.Equal("SalaryChangedPayload", payload.Schema.Name);
        Assert.False(payload.TryGetValue("payComponent", out _));
        Assert.Equal("Raise", payload["reason"]);
        Assert.Equal(2, asV1["schemaVersion"]);
    }

    [Fact]
    public void V2Reader_ReadsV1Data_WithTheDefault()
    {
        GenericRecord asV1 = ReadGeneric(WriteSpecific(SalaryChangedEvent("Allowance")), writer: V2, reader: V1);
        byte[] v1Bytes = WriteGeneric(asV1, V1);

        PayEvent asV2 = ReadSpecific(v1Bytes, writer: V1);

        SalaryChangedPayload payload = Assert.IsType<SalaryChangedPayload>(asV2.payload);
        Assert.Equal(PayEventContract.DefaultPayComponent, payload.payComponent);
        Assert.Equal(38137m, payload.monthlyAmount.ToType<decimal>());
    }

    [Fact]
    public void GeneratedClasses_AreTheLatestContract()
    {
        Assert.Equal(V2.ToString(), PayEvent._SCHEMA.ToString());
        Assert.Equal(2, PayEventContract.SchemaVersion);
    }

    private static PayEvent SalaryChangedEvent(string payComponent)
    {
        EventClassifier classifier = new(new TranslationOptions());
        PayEvent @event = Assert.IsType<EventOutcome>(Assert.Single(classifier.Classify(
            Transaction(Create(Salary(114, 38137m, new DateOnly(2026, 11, 1)) with { PayComponent = payComponent }))))).Event;
        Assert.Equal(payComponent, ((SalaryChangedPayload)@event.payload).payComponent);
        return @event;
    }

    private static Schema Contract(int version)
    {
        using Stream stream = typeof(PayEventContract).Assembly.GetManifestResourceStream($"contracts.avro.v{version}.PayEvent.avsc")
            ?? throw new InvalidOperationException($"contract v{version} is not embedded");
        using StreamReader reader = new(stream);
        return Schema.Parse(reader.ReadToEnd());
    }

    private static byte[] WriteSpecific(PayEvent @event)
    {
        using MemoryStream buffer = new();
        new SpecificDatumWriter<PayEvent>(PayEvent._SCHEMA).Write(@event, new BinaryEncoder(buffer));
        return buffer.ToArray();
    }

    private static byte[] WriteGeneric(GenericRecord record, Schema schema)
    {
        using MemoryStream buffer = new();
        new GenericDatumWriter<GenericRecord>(schema).Write(record, new BinaryEncoder(buffer));
        return buffer.ToArray();
    }

    private static GenericRecord ReadGeneric(byte[] bytes, Schema writer, Schema reader)
    {
        using MemoryStream buffer = new(bytes);
        return new GenericDatumReader<GenericRecord>(writer, reader).Read(null!, new BinaryDecoder(buffer));
    }

    private static PayEvent ReadSpecific(byte[] bytes, Schema writer)
    {
        using MemoryStream buffer = new(bytes);
        return new SpecificDatumReader<PayEvent>(writer, PayEvent._SCHEMA).Read(null!, new BinaryDecoder(buffer));
    }
}
