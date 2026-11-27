using System.Net;
using System.Net.Http.Json;
using Nullbox.Core;
using Nullbox.Tests.Infrastructure;

namespace Nullbox.Tests.Web;

[Collection(AppCollection.Name)]
public sealed class HealthEndpointTests(NullboxAppFactory factory)
{
    [Fact]
    public async Task Healthz_returns_200_healthy_without_login()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Root_returns_app_identity()
    {
        using var client = factory.CreateClient();

        var info = await client.GetFromJsonAsync<AppIdentity>("/");

        Assert.NotNull(info);
        Assert.Equal(AppInfo.Name, info.Name);
        Assert.False(string.IsNullOrWhiteSpace(info.Version));
    }

    private sealed record AppIdentity(string Name, string Version);
}
