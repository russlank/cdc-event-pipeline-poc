using CdcPay.PayQueryApi;

namespace CdcPay.UnitTests.PayQueryApi;

public sealed class AsOfTests
{
    [Theory]
    [InlineData("2026-10-29T08:15:00Z", "2026-10-29T08:15:00.0000000+00:00")]
    [InlineData("2026-10-29T10:15:00+02:00", "2026-10-29T08:15:00.0000000+00:00")]
    [InlineData("2026-10-29T08:15:00.123456Z", "2026-10-29T08:15:00.1234560+00:00")]
    [InlineData("2026-10-29T08:15:00", "2026-10-29T08:15:00.0000000+00:00")]
    [InlineData("2026-10-29", "2026-10-29T23:59:59.9999990+00:00")]
    public void TryParseKnownAt_IsoValue_IsTheUtcInstant(string value, string expected)
    {
        Assert.True(AsOf.TryParseKnownAt(value, out DateTimeOffset knownAt));
        Assert.Equal(expected, knownAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("")]
    [InlineData("yesterday")]
    [InlineData("10/29/2026")]
    [InlineData("2026-13-01")]
    [InlineData("2026-10-29 08:15:00")]
    public void TryParseKnownAt_OtherText_Fails(string value) =>
        Assert.False(AsOf.TryParseKnownAt(value, out _));

    [Theory]
    [InlineData("2026-03-01", true)]
    [InlineData("2026-3-1", false)]
    [InlineData("2026-03-01T00:00:00Z", false)]
    [InlineData("2026-02-30", false)]
    public void TryParseValidAt_AcceptsOnlyCalendarDates(string value, bool valid) =>
        Assert.Equal(valid, AsOf.TryParseValidAt(value, out _));
}
