using CdcPay.Common;

namespace CdcPay.UnitTests.Common;

public sealed class EventIdsTests
{
    private static readonly Guid DnsNamespace = new("6ba7b810-9dad-11d1-80b4-00c04fd430c8");

    [Theory]
    [InlineData("python.org", "886313e1-3b8a-5372-9b90-0c9aee199e5d")]
    [InlineData("www.example.com", "2ed6657d-e927-568b-95e1-2665a8aea6a2")]
    public void CreateVersion5_WithKnownVectors_MatchesReference(string name, string expected)
    {
        Assert.Equal(new Guid(expected), EventIds.CreateVersion5(DnsNamespace, name));
    }

    [Fact]
    public void Namespace_IsFixed()
    {
        // Changing the namespace would change every event id and break deduplication across versions.
        Assert.Equal(new Guid("cd1b09ed-9da5-5b7c-bbe3-a9ec41c38cb7"), EventIds.Namespace);
    }

    [Fact]
    public void FromName_WithSameName_ReturnsSameId()
    {
        Assert.Equal(EventIds.FromName("0000002e:000003c8:0006|0000002e:000003c8:0004|dbo.Salary|114"),
            EventIds.FromName("0000002e:000003c8:0006|0000002e:000003c8:0004|dbo.Salary|114"));
    }

    [Fact]
    public void FromName_WithDifferentNames_ReturnsDifferentIds()
    {
        Assert.NotEqual(EventIds.FromName("a"), EventIds.FromName("b"));
    }

    [Fact]
    public void FromName_SetsVersionAndVariantBits()
    {
        string id = EventIds.FromName("anything").ToString();

        Assert.Equal('5', id[14]);
        Assert.Contains(id[19], "89ab");
    }
}
