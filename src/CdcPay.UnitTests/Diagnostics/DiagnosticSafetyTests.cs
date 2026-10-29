extern alias projector;
extern alias translator;

using System.Reflection;
using System.Text;
using Avro;
using CdcPay.Common;
using CdcPay.Contracts;
using CdcPay.Contracts.Events;
using CdcPay.PayHistory;
using CdcPay.Translation.Classification;
using CdcPay.Translation.Raw;
using Microsoft.Extensions.Logging;
using static CdcPay.UnitTests.Translation.Changes;

namespace CdcPay.UnitTests.Diagnostics;

/// <summary>
/// The rule for <c>dlq.error</c> and logs: they may name values from closed code domains
/// (currency, reason, operation, table), never row content such as amounts, dates, names, or
/// free text. Every dead-letter detail is built from rows full of recognisable content, and every
/// log message may take only parameters of bounded kinds.
/// </summary>
public sealed class DiagnosticSafetyTests
{
    private const string Name = "Ada Sentinel";
    private const string Title = "Sentinel Officer";
    private const string FreeText = "bonus for Ada Sentinel";
    private const decimal Amount = 98765.43m;
    private static readonly DateOnly From = new(2031, 7, 19);
    private static readonly DateOnly Before = new(2031, 3, 4);

    /// <summary>Fragments of the row content above, in any formatting a message could use.</summary>
    private static readonly string[] RowContent = ["Sentinel", "bonus", "98765", "765.43", "2031", "19.07", "07/19", "7/19"];

    /// <summary>String parameters of log messages: configuration, codes, Kafka or source positions, build stamps.</summary>
    private static readonly HashSet<string> BoundedStringParameters = new(StringComparer.Ordinal)
    {
        "rawTopicPattern", "transactionTopic", "topic", "consumerGroup",
        "errorCode", "errorReason",
        "eventType", "reason", "operation",
        "position", "transactionId",
        "version", "commit", "branch",
        // Built only from codes and counts; every place that builds one is covered below.
        "detail",
    };

    public static TheoryData<string, RawChange[]> InvalidTransactions => new()
    {
        { "free-text reason", [Create(Salary(900, Amount, From, reason: FreeText))] },
        { "unsupported currency", [Create(Salary(900, Amount, From, currency: "GBP"))] },
        { "negative amount", [Create(Salary(900, -Amount, From))] },
        { "salary period ends before it starts", [Create(Salary(900, Amount, From, Before))] },
        { "position level", [Create(Position(90, Title, 0, From))] },
        { "position period ends before it starts", [Create(Position(90, Title, 2, From, Before))] },
        { "termination before hire", [Create(new EmployeeRow(1, 1001, Name, "F", From, Before))] },
        { "delete", [Delete(Salary(900, Amount, From))] },
        { "unclassified update", [Update(Salary(900, Amount, From), Salary(900, Amount + 1, From))] },
        { "unknown table", [Create(Other("Payroll"))] },
    };

    [Theory]
    [InlineData("GBP", "'GBP'")]
    [InlineData("Bonus", "'Bonus'")]
    [InlineData("pay_history", "'pay_history'")]
    [InlineData(FreeText, DiagnosticValues.NotACode)]
    [InlineData("98765.43", DiagnosticValues.NotACode)]
    [InlineData("2031-07-19", DiagnosticValues.NotACode)]
    [InlineData("O'Brien", DiagnosticValues.NotACode)]
    [InlineData("AVeryLongValueThatIsNoLongerACode", DiagnosticValues.NotACode)]
    [InlineData("GBP\n", DiagnosticValues.NotACode)]
    [InlineData("", DiagnosticValues.NotACode)]
    [InlineData(null, DiagnosticValues.NotACode)]
    public void Code_NamesOnlyValuesShapedLikeACode(string? value, string expected)
    {
        Assert.Equal(expected, DiagnosticValues.Code(value));
    }

    [Theory]
    [MemberData(nameof(InvalidTransactions), DisableDiscoveryEnumeration = true)]
    public void ClassifierDeadLetter_NamesNoRowContent(string shape, RawChange[] rows)
    {
        IReadOnlyList<TranslationOutcome> outcomes = new EventClassifier(new TranslationOptions()).Classify(Transaction(rows));

        DeadLetterOutcome deadLetter = Assert.IsType<DeadLetterOutcome>(Assert.Single(outcomes));
        AssertNoRowContent(deadLetter.Detail, shape);
    }

    [Fact]
    public void ClassifierDeadLetter_NamesTheRejectedCode()
    {
        IReadOnlyList<TranslationOutcome> outcomes = new EventClassifier(new TranslationOptions())
            .Classify(Transaction(Create(Salary(900, Amount, From, currency: "GBP"))));

        Assert.Equal("Salary.Currency 'GBP' is not supported", Assert.IsType<DeadLetterOutcome>(Assert.Single(outcomes)).Detail);
    }

    [Theory]
    [InlineData("{\"after\":{\"Name\":\"Ada Sentinel\",\"MonthlyAmount\":\"98765.43\"")]
    [InlineData("Ada Sentinel earns 98765.43 from 2031-07-19")]
    [InlineData("""{"before":null,"after":{"SalaryId":1,"TenantId":1,"EmployeeId":1001,"MonthlyAmount":"98765.43 Sentinel","Currency":"SEK","EffectiveFrom":1,"EffectiveTo":null,"Reason":"Raise"},"op":"c","source":{"db":"HrCore","schema":"dbo","table":"Salary","ts_ms":1,"snapshot":"false","commit_lsn":"a"}}""")]
    [InlineData("""{"before":null,"after":{"SalaryId":1,"TenantId":1,"EmployeeId":1001,"MonthlyAmount":"98765.43","Currency":"SEK","EffectiveFrom":"2031-07-19","EffectiveTo":null,"Reason":"Raise"},"op":"c","source":{"db":"HrCore","schema":"dbo","table":"Salary","ts_ms":1,"snapshot":"false","commit_lsn":"a"}}""")]
    [InlineData("""{"before":null,"after":{"PositionId":1,"TenantId":1,"EmployeeId":1001,"Title":"Sentinel Officer","Level":"Sentinel","EffectiveFrom":1,"EffectiveTo":null},"op":"c","source":{"db":"HrCore","schema":"dbo","table":"Position","ts_ms":1,"snapshot":"false","commit_lsn":"a"}}""")]
    [InlineData("""{"before":null,"after":{"TenantId":1,"EmployeeId":1001,"Name":"Ada Sentinel","Gender":"F","HireDate":1},"op":"Sentinel","source":{"db":"HrCore","schema":"dbo","table":"Employee","ts_ms":1,"snapshot":"false","commit_lsn":"a"}}""")]
    public void ParserError_NamesNoMessageContent(string json)
    {
        RawMessage message = new(new KafkaPosition("cdc.HrCore.dbo.Salary", 0, 1), null, Encoding.UTF8.GetBytes(json));

        AssertNoRowContent(Assert.Throws<DebeziumFormatException>(() => DebeziumParser.ParseChange(message)).Message, json);
        AssertNoRowContent(Assert.Throws<DebeziumFormatException>(() => DebeziumParser.ParseTransactionMarker(message)).Message, json);
    }

    [Theory]
    [InlineData(FreeText, "event type (not a code) does not match payload SalaryChangedPayload")]
    [InlineData(EventTypes.PositionChanged, "event type 'PositionChanged' does not match payload SalaryChangedPayload")]
    public void PayEventError_NamesNoEventContent(string eventType, string expected)
    {
        PayEvent payEvent = new()
        {
            eventId = Guid.NewGuid(),
            eventType = eventType,
            schemaVersion = PayEventContract.SchemaVersion,
            tenantId = 1,
            employeeId = 1001,
            effectiveFrom = new DateTime(2031, 7, 19, 0, 0, 0, DateTimeKind.Utc),
            recordedAt = new DateTime(2031, 7, 19, 9, 30, 0, DateTimeKind.Utc),
            source = new EventSource { tables = ["dbo.Salary"], commitLsn = "0000002a:00000100:0003", snapshot = false },
            payload = new SalaryChangedPayload { salaryId = 1, monthlyAmount = new AvroDecimal(Amount), currency = "SEK", reason = "Raise", payComponent = "Base" },
        };

        string message = Assert.Throws<PayEventException>(() => PayFacts.From(payEvent)).Message;

        Assert.Equal(expected, message);
        AssertNoRowContent(message, eventType);
    }

    [Fact]
    public void LogMessages_TakeOnlyBoundedParameters()
    {
        Assembly[] components =
        [
            typeof(ComponentIdentity).Assembly,
            typeof(translator::CdcPay.EventTranslator.DeadLetterPublisher).Assembly,
            typeof(projector::CdcPay.PayHistoryProjector.ProjectorDeadLetters).Assembly,
            typeof(CdcPay.PayQueryApi.AsOf).Assembly,
        ];

        List<MethodInfo> logMethods =
        [
            .. components
                .SelectMany(assembly => assembly.GetTypes())
                .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
                .Where(method => method.GetCustomAttribute<LoggerMessageAttribute>() is not null),
        ];

        Assert.NotEmpty(logMethods);
        Assert.All(
            logMethods.SelectMany(method => method.GetParameters().Select(parameter => (Method: $"{method.DeclaringType!.Name}.{method.Name}", Parameter: parameter))),
            entry => Assert.True(IsBounded(entry.Parameter), $"{entry.Method} logs {entry.Parameter.Name} ({entry.Parameter.ParameterType.Name}), which could carry row content"));
    }

    internal static bool IsBounded(ParameterInfo parameter)
    {
        Type type = Nullable.GetUnderlyingType(parameter.ParameterType) ?? parameter.ParameterType;
        return type == typeof(ILogger)
            || type.IsEnum
            || type == typeof(int)
            || type == typeof(long)
            || type == typeof(Guid)
            || type == typeof(TimeSpan)
            || type == typeof(Type)
            || type == typeof(ComponentIdentity)
            || (type == typeof(string) && BoundedStringParameters.Contains(parameter.Name!));
    }

    private static void AssertNoRowContent(string text, string input)
    {
        foreach (string fragment in RowContent)
        {
            Assert.False(text.Contains(fragment, StringComparison.OrdinalIgnoreCase), $"'{text}' quotes '{fragment}' from {input}");
        }
    }
}
