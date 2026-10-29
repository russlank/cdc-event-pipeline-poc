using CdcPay.Common;

namespace CdcPay.UnitTests.Common;

public sealed class ComponentIdentityTests
{
    [Theory]
    [InlineData("refs/heads/main", "main")]
    [InlineData("refs/tags/v1.0.0", "v1.0.0")]
    [InlineData("refs/remotes/origin/feature/x", "feature/x")]
    [InlineData("origin/main", "main")]
    [InlineData("  main  ", "main")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void NormalizeBranch_WithRefForms_ReturnsShortName(string? input, string? expected)
    {
        Assert.Equal(expected, ComponentIdentity.NormalizeBranch(input));
    }

    [Fact]
    public void ToString_WithCommitAndBranch_ReturnsSingleLineIdentity()
    {
        var identity = new ComponentIdentity("CdcPay.PayQueryApi", "CDC Pay Events PoC", "0.1.0", "abc1234567", "main", null);

        Assert.Equal("CdcPay.PayQueryApi v0.1.0 (commit abc1234567) [main]", identity.ToString());
    }

    [Fact]
    public void ToString_WithoutCommitAndBranch_OmitsThem()
    {
        var identity = new ComponentIdentity("CdcPay.PayQueryApi", "CDC Pay Events PoC", "0.1.0", null, null, null);

        Assert.Equal("CdcPay.PayQueryApi v0.1.0", identity.ToString());
    }

    [Fact]
    public void FromAssembly_WithRepositoryBuild_ReadsBuildProperties()
    {
        var identity = ComponentIdentity.FromAssembly(typeof(ComponentIdentity).Assembly);

        Assert.Equal("CdcPay.Common", identity.Component);
        Assert.Equal("CDC Pay Events PoC", identity.Product);
        Assert.Matches(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.]+)?$", identity.Version);
        Assert.DoesNotContain("+", identity.Version, StringComparison.Ordinal);
    }

    [Fact]
    public void FromAssembly_WithNull_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ComponentIdentity.FromAssembly(null!));
    }
}
