using System.Text;
using CdcPay.Contracts;

namespace CdcPay.UnitTests.Contracts;

public sealed class DeadLetterHeadersTests
{
    [Fact]
    public void Create_ReturnsEveryHeaderAsUtf8()
    {
        IReadOnlyList<KeyValuePair<string, byte[]>> headers = DeadLetterHeaders.Create(
            "InvalidRow", "unsupported Currency", "event-translator", "cdc.HrCore.dbo.Salary", 0, 42, new DateTimeOffset(2026, 10, 29, 8, 0, 0, TimeSpan.Zero));

        Assert.Equal(
            [
                ("dlq.reason", "InvalidRow"),
                ("dlq.error", "unsupported Currency"),
                ("dlq.component", "event-translator"),
                ("dlq.source.topic", "cdc.HrCore.dbo.Salary"),
                ("dlq.source.partition", "0"),
                ("dlq.source.offset", "42"),
                ("dlq.timestamp", "2026-10-29T08:00:00.0000000+00:00"),
            ],
            headers.Select(header => (header.Key, Encoding.UTF8.GetString(header.Value))));
    }
}
