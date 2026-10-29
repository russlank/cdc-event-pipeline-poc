using System.Globalization;
using System.Text.Json;
using CdcPay.Contracts;

namespace CdcPay.Translation.Raw;

/// <summary>
/// Parses Debezium SQL Server JSON (JsonConverter without schemas, decimals as strings,
/// dates as days since 1970-01-01) into <see cref="RawChange"/> and <see cref="TransactionMarker"/>.
/// </summary>
public static class DebeziumParser
{
    private static readonly int EpochDayNumber = new DateOnly(1970, 1, 1).DayNumber;

    public static RawChange ParseChange(RawMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        try
        {
            using var document = JsonDocument.Parse(message.Value);
            JsonElement root = document.RootElement;
            JsonElement source = Required(root, "source");

            string table = RequiredString(source, "table");
            ChangeOperation operation = RequiredString(root, "op") switch
            {
                "c" => ChangeOperation.Create,
                "u" => ChangeOperation.Update,
                "d" => ChangeOperation.Delete,
                "r" => ChangeOperation.Read,
                _ => throw new DebeziumFormatException("Unknown operation."),
            };

            JsonElement transaction = root.TryGetProperty("transaction", out JsonElement t) ? t : default;
            bool hasTransaction = transaction.ValueKind == JsonValueKind.Object;

            RawChange change = new(
                message,
                RequiredString(source, "db"),
                RequiredString(source, "schema"),
                table,
                operation,
                ParseRow(table, root, "before"),
                ParseRow(table, root, "after"),
                Required(source, "ts_ms").GetInt64(),
                RequiredString(source, "commit_lsn"),
                OptionalString(source, "change_lsn"),
                OptionalInt64(source, "event_serial_no"),
                RequiredString(source, "snapshot") != "false",
                hasTransaction ? RequiredString(transaction, "id") : null,
                hasTransaction ? Required(transaction, "total_order").GetInt64() : null);

            bool imagesMatchOperation = operation switch
            {
                ChangeOperation.Create or ChangeOperation.Read => change.After is not null,
                ChangeOperation.Update => change.Before is not null && change.After is not null,
                ChangeOperation.Delete => change.Before is not null,
                _ => false,
            };
            return imagesMatchOperation
                ? change
                : throw new DebeziumFormatException($"Row images do not match operation {operation}.");
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or KeyNotFoundException)
        {
            throw new DebeziumFormatException($"Not a Debezium change event: {Describe(exception)}", exception);
        }
    }

    public static TransactionMarker ParseTransactionMarker(RawMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        try
        {
            using var document = JsonDocument.Parse(message.Value);
            JsonElement root = document.RootElement;

            TransactionStatus status = RequiredString(root, "status") switch
            {
                "BEGIN" => TransactionStatus.Begin,
                "END" => TransactionStatus.End,
                _ => throw new DebeziumFormatException("Unknown transaction status."),
            };

            Dictionary<string, long> counts = new(StringComparer.Ordinal);
            if (root.TryGetProperty("data_collections", out JsonElement collections) && collections.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement collection in collections.EnumerateArray())
                {
                    counts[RequiredString(collection, "data_collection")] = Required(collection, "event_count").GetInt64();
                }
            }

            JsonElement eventCount = root.TryGetProperty("event_count", out JsonElement c) ? c : default;
            return new TransactionMarker(
                message,
                status,
                RequiredString(root, "id"),
                eventCount.ValueKind == JsonValueKind.Number ? eventCount.GetInt64() : null,
                counts,
                Required(root, "ts_ms").GetInt64());
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or KeyNotFoundException)
        {
            throw new DebeziumFormatException($"Not a Debezium transaction event: {Describe(exception)}", exception);
        }
    }

    /// <summary>
    /// Describes a parse failure without quoting the message: framework exception messages can
    /// contain the offending text, which may be row data, and this description reaches logs and
    /// dead-letter headers.
    /// </summary>
    private static string Describe(Exception exception) => exception switch
    {
        JsonException json => $"invalid JSON at line {json.LineNumber}, byte {json.BytePositionInLine}",
        InvalidOperationException => "a field has an unexpected JSON type",
        FormatException => "a field has a malformed value",
        KeyNotFoundException => "a required field is missing",
        _ => exception.GetType().Name,
    };

    private static SourceRow? ParseRow(string table, JsonElement root, string image)
    {
        if (!root.TryGetProperty(image, out JsonElement row) || row.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return table switch
        {
            "Employee" => new EmployeeRow(
                Int(row, "TenantId"),
                Int(row, "EmployeeId"),
                RequiredString(row, "Name"),
                RequiredString(row, "Gender"),
                Date(row, "HireDate"),
                OptionalDate(row, "TerminationDate")),
            "Salary" => new SalaryRow(
                Int(row, "SalaryId"),
                Int(row, "TenantId"),
                Int(row, "EmployeeId"),
                decimal.Parse(RequiredString(row, "MonthlyAmount"), NumberStyles.Number, CultureInfo.InvariantCulture),
                RequiredString(row, "Currency"),
                Date(row, "EffectiveFrom"),
                OptionalDate(row, "EffectiveTo"),
                RequiredString(row, "Reason"),
                // Absent before the column was added (old capture instance): the contract default.
                OptionalString(row, "PayComponent") ?? PayEventContract.DefaultPayComponent),
            "Position" => new PositionRow(
                Int(row, "PositionId"),
                Int(row, "TenantId"),
                Int(row, "EmployeeId"),
                RequiredString(row, "Title"),
                Int(row, "Level"),
                Date(row, "EffectiveFrom"),
                OptionalDate(row, "EffectiveTo")),
            _ => new OtherRow(table),
        };
    }

    private static JsonElement Required(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind != JsonValueKind.Null
            ? value
            : throw new DebeziumFormatException($"Missing field '{name}'.");

    private static string RequiredString(JsonElement element, string name) =>
        Required(element, name).GetString() ?? throw new DebeziumFormatException($"Missing field '{name}'.");

    private static string? OptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long? OptionalInt64(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number ? value.GetInt64() : null;

    private static int Int(JsonElement element, string name) => Required(element, name).GetInt32();

    private static DateOnly Date(JsonElement element, string name) => DateOnly.FromDayNumber(EpochDayNumber + Int(element, name));

    private static DateOnly? OptionalDate(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            ? DateOnly.FromDayNumber(EpochDayNumber + value.GetInt32())
            : null;
}
