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
    [InlineData("/rules", "promo-cleanup")]
    [InlineData("/rules/new", "New rule set")]
    [InlineData("/rules/important-mail-guard", "Save as v2")]
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

    [Fact]
    public async Task Rule_sets_export_as_a_json_file_for_the_admin_only()
    {
        using var client = factory.CreateNonRedirectingClient();
        using (var anonymous = await client.GetAsync("/api/rule-sets/promo-cleanup/export"))
        {
            Assert.Equal(HttpStatusCode.Redirect, anonymous.StatusCode);
        }

        using var login = await LoginHelper.LoginAsync(client, NullboxAppFactory.AdminPassword);
        using var export = await client.GetAsync("/api/rule-sets/promo-cleanup/export?version=1");
        using var missing = await client.GetAsync("/api/rule-sets/no-such-set/export");

        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.Equal("application/json", export.Content.Headers.ContentType?.MediaType);
        Assert.Equal("promo-cleanup-v1.json", export.Content.Headers.ContentDisposition?.FileNameStar ?? export.Content.Headers.ContentDisposition?.FileName);
        var document = Nullbox.Core.Rules.RuleJson.ParseOrThrow(await export.Content.ReadAsStringAsync());
        Assert.Equal(Nullbox.Core.Rules.RuleSetSeeds.PromoCleanup, document.Name);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
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
