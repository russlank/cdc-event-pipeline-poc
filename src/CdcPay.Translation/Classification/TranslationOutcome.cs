using CdcPay.Contracts;
using CdcPay.Contracts.Events;
using CdcPay.Translation.Raw;

namespace CdcPay.Translation.Classification;

/// <summary>What the translator does with a group of source rows.</summary>
public abstract record TranslationOutcome(IReadOnlyList<RawChange> Rows);

/// <summary>Publish a business event.</summary>
public sealed record EventOutcome(PayEvent Event, IReadOnlyList<RawChange> Rows) : TranslationOutcome(Rows)
{
    public string Key => PayEventContract.Key(Event.tenantId, Event.employeeId);
}

/// <summary>Rows without pay meaning; counted, not published. <see cref="Reason"/> is one of <see cref="IgnoreReasons"/>.</summary>
public sealed record IgnoredOutcome(string Reason, IReadOnlyList<RawChange> Rows) : TranslationOutcome(Rows);

/// <summary>
/// Rows that cannot be translated; each goes to the dead-letter topic unchanged.
/// <see cref="Reason"/> is one of <see cref="DeadLetterReasons"/>; <see cref="Detail"/> never contains row values.
/// </summary>
public sealed record DeadLetterOutcome(string Reason, string Detail, IReadOnlyList<RawChange> Rows) : TranslationOutcome(Rows);

/// <summary>Bounded vocabulary of dead-letter reasons (header <c>dlq.reason</c>, metric tag).</summary>
public static class DeadLetterReasons
{
    public const string Unparseable = nameof(Unparseable);
    public const string InvalidRow = nameof(InvalidRow);
    public const string InvalidCorrection = nameof(InvalidCorrection);
    public const string UnsupportedChange = nameof(UnsupportedChange);
    public const string UnclassifiedTransaction = nameof(UnclassifiedTransaction);
    public const string IncompleteTransaction = nameof(IncompleteTransaction);
}

/// <summary>Bounded vocabulary of ignore reasons (metric tag).</summary>
public static class IgnoreReasons
{
    /// <summary>Debezium heartbeat and signal tables.</summary>
    public const string Infrastructure = nameof(Infrastructure);

    /// <summary>Tenant reference data.</summary>
    public const string ReferenceData = nameof(ReferenceData);

    /// <summary>Employee columns without pay meaning (Name).</summary>
    public const string NonPayChange = nameof(NonPayChange);

    /// <summary>Gender changes after hire are not propagated (PoC limitation).</summary>
    public const string GenderChange = nameof(GenderChange);
}
