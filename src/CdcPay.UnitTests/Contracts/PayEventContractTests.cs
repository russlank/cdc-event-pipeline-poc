using Avro;
using Avro.IO;
using Avro.Specific;
using CdcPay.Contracts;
using CdcPay.Contracts.Events;
using CdcPay.Translation.Assembly;
using CdcPay.Translation.Classification;
using static CdcPay.UnitTests.Translation.Changes;

namespace CdcPay.UnitTests.Contracts;

public sealed class PayEventContractTests
{
    private static readonly DateOnly May = new(2026, 5, 1);
    private static readonly DateOnly Nov = new(2026, 11, 1);

    [Fact]
    public void GeneratedSchema_MatchesContractFile()
    {
        using Stream stream = typeof(PayEventContract).Assembly.GetManifestResourceStream("contracts.avro.v1.PayEvent.avsc")
            ?? throw new InvalidOperationException("contract schema is not embedded");
        using StreamReader reader = new(stream);
        var contract = Schema.Parse(reader.ReadToEnd());

        // Fails when contracts/avro/v1/PayEvent.avsc changed without contracts/scripts/generate-csharp.sh.
        Assert.Equal(contract.ToString(), PayEvent._SCHEMA.ToString());
    }

    [Fact]
    public void EventTypes_MatchPayloadBranches()
    {
        var payload = (UnionSchema)((RecordSchema)PayEvent._SCHEMA).Fields.Single(field => field.Name == "payload").Schema;

        Assert.Equal(
            [EventTypes.EmployeeHired, EventTypes.SalaryChanged, EventTypes.SalaryCorrected, EventTypes.PositionChanged, EventTypes.EmployeeTerminated],
            payload.Schemas.Select(branch => ((NamedSchema)branch).Name.Replace("Payload", string.Empty, StringComparison.Ordinal)));
    }

    public static TheoryData<string> Events() =>
    [
        EventTypes.EmployeeHired,
        EventTypes.SalaryChanged,
        EventTypes.SalaryCorrected,
        EventTypes.PositionChanged,
        EventTypes.EmployeeTerminated,
    ];

    [Theory]
    [MemberData(nameof(Events))]
    public void ClassifiedEvent_RoundTripsThroughAvro(string eventType)
    {
        PayEvent original = Translate(eventType);

        PayEvent copy = RoundTrip(original);

        Assert.Equal(original.eventId, copy.eventId);
        Assert.Equal(eventType, copy.eventType);
        Assert.Equal(original.effectiveFrom, copy.effectiveFrom);
        Assert.Equal(original.effectiveTo, copy.effectiveTo);
        Assert.Equal(original.recordedAt, copy.recordedAt);
        Assert.Equal(original.payload.GetType(), copy.payload.GetType());
        Assert.Equal(original.source.tables, copy.source.tables);
    }

    private static PayEvent Translate(string eventType)
    {
        EventClassifier classifier = new(new TranslationOptions());
        CompletedTransaction transaction = eventType switch
        {
            EventTypes.EmployeeHired => Transaction(Create(Employee(1101, hired: Nov), order: 1), Create(Salary(500, 36000m, Nov, reason: "Hire", employeeId: 1101), order: 2)),
            EventTypes.SalaryChanged => Transaction(Update(Salary(113, 37389m, May), Salary(113, 37389m, May, Nov), order: 1), Create(Salary(114, 38137.5m, Nov), order: 2)),
            EventTypes.SalaryCorrected => Transaction(Update(Salary(113, 37389m, May, Nov), Salary(113, 37939.25m, May, null, reason: "Correction"))),
            EventTypes.PositionChanged => Transaction(Create(Position(10, "Lead", 4, Nov))),
            EventTypes.EmployeeTerminated => Transaction(Update(Employee(), Employee(terminated: new DateOnly(2026, 10, 31)))),
            _ => throw new ArgumentOutOfRangeException(nameof(eventType)),
        };
        return Assert.IsType<EventOutcome>(Assert.Single(classifier.Classify(transaction))).Event;
    }

    private static PayEvent RoundTrip(PayEvent original)
    {
        using MemoryStream buffer = new();
        new SpecificDatumWriter<PayEvent>(PayEvent._SCHEMA).Write(original, new BinaryEncoder(buffer));
        buffer.Position = 0;
        return new SpecificDatumReader<PayEvent>(PayEvent._SCHEMA, PayEvent._SCHEMA).Read(null!, new BinaryDecoder(buffer));
    }
}
