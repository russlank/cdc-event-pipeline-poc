using System.Globalization;

namespace CdcPay.PayQueryApi;

/// <summary>Parsing of the as-of query parameters. All instants are UTC.</summary>
public static class AsOf
{
    private const string DateFormat = "yyyy-MM-dd";

    private static readonly string[] DateTimeFormats =
    [
        "yyyy-MM-dd'T'HH:mm:ssK",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK",
        "yyyy-MM-dd'T'HH:mm:ss",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF",
    ];

    /// <summary><c>validAt</c>: a calendar date, <c>yyyy-MM-dd</c>.</summary>
    public static bool TryParseValidAt(string? value, out DateOnly validAt) =>
        DateOnly.TryParseExact(value, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out validAt);

    /// <summary>
    /// <c>knownAt</c>: an ISO 8601 date-time (UTC when it has no offset), or a bare date meaning the
    /// end of that day UTC: the last microsecond, the precision of the store.
    /// </summary>
    public static bool TryParseKnownAt(string? value, out DateTimeOffset knownAt)
    {
        if (DateOnly.TryParseExact(value, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly day))
        {
            knownAt = new DateTimeOffset(day.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddMicroseconds(-1);
            return true;
        }

        if (DateTimeOffset.TryParseExact(
                value,
                DateTimeFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset instant))
        {
            knownAt = instant;
            return true;
        }

        knownAt = default;
        return false;
    }
}
