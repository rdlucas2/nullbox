using System.Net;
using Nullbox.Tests.Infrastructure;

namespace Nullbox.Tests.Auth;

[Collection(AppCollection.Name)]
public sealed class DashboardAuthTests(NullboxAppFactory factory)
{
    [Fact]
    public async Task Dashboard_redirects_anonymous_users_to_login()
    {
        using var client = factory.CreateNonRedirectingClient();

        using var response = await client.GetAsync("/hangfire");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/login", response.Headers.Location?.PathAndQuery);
    }

    [Fact]
    public async Task Dashboard_api_is_not_reachable_anonymously()
    {
        using var client = factory.CreateNonRedirectingClient();

        using var response = await client.GetAsync("/hangfire/stats");

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Login_page_renders_with_antiforgery_token()
    {
        using var client = factory.CreateClient();

        var page = await client.GetStringAsync("/login");

        Assert.Contains("__RequestVerificationToken", page);
        Assert.Contains("type=\"password\"", page);
    }

    [Fact]
    public async Task Wrong_password_is_rejected_and_dashboard_stays_closed()
    {
        using var client = factory.CreateNonRedirectingClient();

        using var login = await LoginHelper.LoginAsync(client, "not-the-password");
        using var dashboard = await client.GetAsync("/hangfire");

        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
        Assert.Contains("Wrong password", await login.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Redirect, dashboard.StatusCode);
    }

    [Fact]
    public async Task Correct_password_opens_the_dashboard()
    {
        using var client = factory.CreateNonRedirectingClient();

        using var login = await LoginHelper.LoginAsync(client, NullboxAppFactory.AdminPassword);
        using var dashboard = await client.GetAsync("/hangfire");

        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Equal("/hangfire", login.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.OK, dashboard.StatusCode);
        Assert.Contains("nullbox jobs", await dashboard.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Login_post_without_antiforgery_token_is_rejected()
    {
        using var client = factory.CreateNonRedirectingClient();

        using var response = await client.PostAsync("/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["password"] = NullboxAppFactory.AdminPassword,
        }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Logout_ends_the_session()
    {
        using var client = factory.CreateNonRedirectingClient();
        using var login = await LoginHelper.LoginAsync(client, NullboxAppFactory.AdminPassword);
        var token = await LoginHelper.GetAntiforgeryFieldAsync(client);

        using var logout = await client.PostAsync("/logout", new FormUrlEncodedContent([token]));
        using var dashboard = await client.GetAsync("/hangfire");

        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, dashboard.StatusCode);
    }

    [Theory]
    [InlineData(null, "/hangfire")]
    [InlineData("", "/hangfire")]
    [InlineData("/hangfire/jobs/enqueued", "/hangfire/jobs/enqueued")]
    [InlineData("/", "/")]
    [InlineData("https://evil.example/", "/hangfire")]
    [InlineData("//evil.example/", "/hangfire")]
    [InlineData("/\\evil.example/", "/hangfire")]
    [InlineData("hangfire", "/hangfire")]
    public void Return_url_only_allows_local_paths(string? returnUrl, string expected)
        => Assert.Equal(expected, Nullbox.Web.Auth.AdminAuth.SafeReturnUrl(returnUrl));
}

/// <summary>With no ADMIN_PASSWORD set, nobody can sign in, including with an empty password.</summary>
public sealed class NoPasswordFactory : NullboxAppFactory
{
    protected override IReadOnlyDictionary<string, string?> Settings => new Dictionary<string, string?>(base.Settings)
    {
        ["Nullbox:AdminPassword"] = string.Empty,
    };
}

[Collection(AppCollection.Name)]
public sealed class NoPasswordConfiguredTests : IDisposable
{
    private readonly NoPasswordFactory _factory = new();

    [Theory]
    [InlineData("")]
    [InlineData("anything")]
    public async Task Login_is_refused_when_no_password_is_configured(string attempt)
    {
        using var client = _factory.CreateNonRedirectingClient();
        var page = await client.GetStringAsync("/login");
        Assert.Contains("set ADMIN_PASSWORD", page);

        using var login = await LoginHelper.LoginAsync(client, attempt);
        using var dashboard = await client.GetAsync("/hangfire");

        Assert.NotEqual(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, dashboard.StatusCode);
    }

    public void Dispose() => _factory.Dispose();
}
