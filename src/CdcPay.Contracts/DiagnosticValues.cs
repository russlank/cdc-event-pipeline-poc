using System.Text.RegularExpressions;

namespace CdcPay.Contracts;

/// <summary>
/// What log messages and <c>dlq.error</c> may say about a value. Values from closed code domains
/// (currency, reason, operation, table) may be named; row content (amounts, dates, names, free
/// text) never is.
/// </summary>
public static partial class DiagnosticValues
{
    /// <summary>Stands in for a value that does not have the shape of a code.</summary>
    public const string NotACode = "(not a code)";

    /// <summary>
    /// The value of a code column, quoted, when it has the shape of a code: a letter, then at most
    /// 31 letters, digits, or underscores. A rejected value is outside its domain and may be
    /// anything, so any other value is replaced by <see cref="NotACode"/>.
    /// </summary>
    public static string Code(string? value) =>
        value is not null && CodeShape().IsMatch(value) ? $"'{value}'" : NotACode;

    [GeneratedRegex(@"\A[A-Za-z][A-Za-z0-9_]{0,31}\z", RegexOptions.CultureInvariant)]
    private static partial Regex CodeShape();
}
