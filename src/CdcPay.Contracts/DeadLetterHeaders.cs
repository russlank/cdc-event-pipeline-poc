using System.Globalization;
using System.Text;

namespace CdcPay.Contracts;

/// <summary>
/// Headers of a message on <see cref="PayEventContract.DeadLetterTopic"/>. The key and value stay
/// as they were; these headers (UTF-8 values) say what failed, where, and when.
/// </summary>
public static class DeadLetterHeaders
{
    public const string Reason = "dlq.reason";
    public const string Error = "dlq.error";
    public const string Component = "dlq.component";
    public const string SourceTopic = "dlq.source.topic";
    public const string SourcePartition = "dlq.source.partition";
    public const string SourceOffset = "dlq.source.offset";
    public const string Timestamp = "dlq.timestamp";

    /// <summary>The headers in order; <c>error</c> may name codes, never row content (<see cref="DiagnosticValues"/>).</summary>
    public static IReadOnlyList<KeyValuePair<string, byte[]>> Create(
        string reason, string error, string component, string sourceTopic, int sourcePartition, long sourceOffset, DateTimeOffset timestamp) =>
    [
        new(Reason, Utf8(reason)),
        new(Error, Utf8(error)),
        new(Component, Utf8(component)),
        new(SourceTopic, Utf8(sourceTopic)),
        new(SourcePartition, Utf8(sourcePartition.ToString(CultureInfo.InvariantCulture))),
        new(SourceOffset, Utf8(sourceOffset.ToString(CultureInfo.InvariantCulture))),
        new(Timestamp, Utf8(timestamp.ToString("O", CultureInfo.InvariantCulture))),
    ];

    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);
}
