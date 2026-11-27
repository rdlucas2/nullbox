using System.Net;
using Nullbox.Tests.Infrastructure;
using Nullbox.Web.Components.Layout;

namespace Nullbox.Tests.Ui;

[Collection(AppCollection.Name)]
public sealed class UiShellTests(NullboxAppFactory factory)
{
    [Theory]
    [InlineData("/")]
    [InlineData("/rules")]
    [InlineData("/settings")]
    public async Task Ui_pages_redirect_anonymous_users_to_login(string path)
    {
        using var client = factory.CreateNonRedirectingClient();

        using var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/login", response.Headers.Location?.PathAndQuery);
    }

    [Fact]
    public async Task Dashboard_renders_status_cards_and_nav_when_signed_in()
    {
        using var client = factory.CreateNonRedirectingClient();
        using var login = await LoginHelper.LoginAsync(client, NullboxAppFactory.AdminPassword, "/");
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);

        using var response = await client.GetAsync("/");
        var page = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Dashboard", page);
        Assert.Contains("data-card=\"gmail\"", page);
        Assert.Contains("data-card=\"poll\"", page);
        Assert.Contains("data-card=\"queue\"", page);
        Assert.Contains("data-card=\"jev\"", page);
        foreach (var item in NavItems.All)
        {
            Assert.Contains(item.Title, page);
        }
    }

    [Theory]
    [InlineData("/rules", "Rule sets")]
    [InlineData("/test", "Test")]
    [InlineData("/audit", "Audit")]
    [InlineData("/jobs", "Open the Hangfire dashboard")]
    [InlineData("/settings", "Gmail connection")]
    public async Task Section_pages_render_when_signed_in(string path, string expected)
    {
        using var client = factory.CreateNonRedirectingClient();
        using var login = await LoginHelper.LoginAsync(client, NullboxAppFactory.AdminPassword);

        using var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(expected, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Sign_out_form_posts_to_logout_with_an_antiforgery_token()
    {
        using var client = factory.CreateNonRedirectingClient();
        using var login = await LoginHelper.LoginAsync(client, NullboxAppFactory.AdminPassword);

        var page = await client.GetStringAsync("/rules");

        Assert.Contains("action=\"/logout\"", page);
        Assert.Contains("__RequestVerificationToken", page);
    }

    [Theory]
    [InlineData("/_content/MudBlazor/MudBlazor.min.css")]
    [InlineData("/_framework/blazor.web.js")]
    public async Task Static_assets_do_not_need_a_login(string path)
    {
        using var client = factory.CreateNonRedirectingClient();

        using var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
