using System.Globalization;
using Avro;
using CdcPay.Common;
using CdcPay.Contracts;
using CdcPay.Contracts.Events;
using CdcPay.Translation.Raw;

namespace CdcPay.Translation.Classification;

/// <summary>Builds the event envelope: identity, valid and transaction time, retroactivity, source.</summary>
internal static class PayEventFactory
{
    public static PayEvent Create(
        string eventType,
        RawChange anchor,
        IEnumerable<RawChange> contributingRows,
        int tenantId,
        int employeeId,
        DateOnly effectiveFrom,
        DateOnly? effectiveTo,
        object payload)
    {
        DateTime recordedAt = DateTimeOffset.FromUnixTimeMilliseconds(anchor.CommitTimestampMs).UtcDateTime;

        return new PayEvent
        {
            eventId = EventId(eventType, anchor),
            eventType = eventType,
            schemaVersion = PayEventContract.SchemaVersion,
            tenantId = tenantId,
            employeeId = employeeId,
            effectiveFrom = ToAvroDate(effectiveFrom),
            effectiveTo = effectiveTo is { } to ? ToAvroDate(to) : null,
            recordedAt = recordedAt,
            isRetroactive = !anchor.Snapshot && effectiveFrom < DateOnly.FromDateTime(recordedAt),
            source = new EventSource
            {
                tables = [.. contributingRows.Select(row => row.QualifiedTable).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
                commitLsn = anchor.CommitLsn,
                changeLsn = anchor.ChangeLsn,
                transactionId = anchor.TransactionId,
                snapshot = anchor.Snapshot,
            },
            payload = payload,
        };
    }

    /// <summary>
    /// Streaming: from the source position of the anchor row, unique per change and stable on replay.
    /// Snapshot: from the row's business content, because every snapshot reads at a new LSN.
    /// </summary>
    public static Guid EventId(string eventType, RawChange anchor) =>
        anchor.Snapshot
            ? EventIds.FromName($"snapshot|{eventType}|{anchor.QualifiedTable}|{anchor.Row.Key}|{Content(anchor.Row)}")
            : EventIds.FromName($"{anchor.CommitLsn}|{anchor.ChangeLsn}|{anchor.QualifiedTable}|{anchor.Row.Key}");

    public static DateTime ToAvroDate(DateOnly date) => date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

    /// <summary>Exactly two decimal places, as the contract's decimal(12,2) requires.</summary>
    public static AvroDecimal ToAvroDecimal(decimal amount) => new(decimal.Round(amount, 2, MidpointRounding.ToEven) + 0.00m);

    private static string Content(SourceRow row) => row switch
    {
        EmployeeRow e => string.Join('|', e.Gender, Date(e.HireDate), Date(e.TerminationDate)),
        // PayComponent counts only when it is not the default, so rows snapshotted before and
        // after the column was added keep their ids.
        SalaryRow s => string.Join('|', s.TenantId, s.EmployeeId, s.MonthlyAmount.ToString("0.00", CultureInfo.InvariantCulture), s.Currency, Date(s.EffectiveFrom), Date(s.EffectiveTo), s.Reason)
            + (s.PayComponent == PayEventContract.DefaultPayComponent ? string.Empty : $"|{s.PayComponent}"),
        PositionRow p => string.Join('|', p.TenantId, p.EmployeeId, p.Title, p.Level, Date(p.EffectiveFrom), Date(p.EffectiveTo)),
        _ => row.Key,
    };

    private static string Date(DateOnly? date) => date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "-";
}
