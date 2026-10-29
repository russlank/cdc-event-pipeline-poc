using System.Net;
using System.Net.Http.Json;
using CdcPay.Common;
using CdcPay.Common.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace CdcPay.UnitTests.Common;

public sealed class ServiceEndpointsTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Theory]
    [InlineData(ServiceDefaultsExtensions.LivePath)]
    [InlineData(ServiceDefaultsExtensions.ReadyPath)]
    public async Task HealthEndpoint_WithoutDependencies_ReturnsHealthy(string path)
    {
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IdentityEndpoint_ReturnsHostIdentity()
    {
        using HttpClient client = factory.CreateClient();

        ComponentIdentity? identity = await client.GetFromJsonAsync<ComponentIdentity>(
            new Uri(ServiceDefaultsExtensions.IdentityPath, UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.NotNull(identity);
        Assert.Equal("CdcPay.PayQueryApi", identity.Component);
        Assert.Equal("CDC Pay Events PoC", identity.Product);
        Assert.StartsWith("0.1.0", identity.Version, StringComparison.Ordinal);
    }
}
