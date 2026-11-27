using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Nullbox.Core.Gmail;
using Nullbox.Tests.Infrastructure;

namespace Nullbox.Tests.Gmail.Auth;

[Collection(AppCollection.Name)]
public sealed partial class GmailSettingsPageTests(NullboxAppFactory factory) : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "nullbox-settings-" + Guid.NewGuid().ToString("N"));
    private readonly FakeGoogle _google = new();

    public void Dispose()
    {
        if (Directory.Exists(_data))
        {
            Directory.Delete(_data, recursive: true);
        }
    }

    [Fact]
    public async Task Settings_page_requires_login()
    {
        using var client = factory.CreateNonRedirectingClient();

        using var response = await client.GetAsync("/settings/gmail");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/login", response.Headers.Location?.PathAndQuery);
    }

    [Fact]
    public async Task Shows_not_connected_until_a_token_is_uploaded_then_connected()
    {
        using var app = App();
        using var client = await SignedInClient(app);

        var before = await client.GetStringAsync("/settings/gmail");
        Assert.Contains("Not connected", before, StringComparison.Ordinal);

        using var response = await Upload(client, TokenSamples.EmailBlasterToken(DateTimeOffset.UtcNow.AddMinutes(30)));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var after = await client.GetStringAsync(response.Headers.Location!.OriginalString);
        Assert.Contains("Saved the mail token for ryan@example.com", after, StringComparison.Ordinal);
        Assert.Contains("Connected", after, StringComparison.Ordinal);
        Assert.Contains("https://www.googleapis.com/auth/gmail.modify", after, StringComparison.Ordinal);
        Assert.DoesNotContain("the-refresh-token", after, StringComparison.Ordinal);
        Assert.Equal(GmailAuthState.Ready, app.Services.GetRequiredService<IGmailCredentialStore>().GetStatus(GmailTokenKind.Primary).State);
    }

    [Fact]
    public async Task Malformed_upload_is_rejected_with_the_reason()
    {
        using var app = App();
        using var client = await SignedInClient(app);

        using var response = await Upload(client, TokenSamples.ClientFile);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("not a token", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_data, "google", "token.json")));
    }

    [Fact]
    public async Task Upload_without_antiforgery_token_is_refused()
    {
        using var app = App();
        using var client = await SignedInClient(app);

        using var content = new MultipartFormDataContent { { new StringContent("primary"), "kind" }, { new StringContent("{}"), "tokenJson" } };
        using var response = await client.PostAsync("/settings/gmail", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private WebApplicationFactoryWithGoogle App() => new(factory, _data, _google);

    private static async Task<HttpClient> SignedInClient(WebApplicationFactoryWithGoogle app)
    {
        var client = app.Factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var login = await LoginHelper.LoginAsync(client, NullboxAppFactory.AdminPassword);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        return client;
    }

    private static async Task<HttpResponseMessage> Upload(HttpClient client, string json)
    {
        var page = await client.GetStringAsync("/settings/gmail");
        var token = TokenField().Match(page);
        using var content = new MultipartFormDataContent
        {
            { new StringContent(token.Groups["value"].Value), "__RequestVerificationToken" },
            { new StringContent("primary"), "kind" },
            { new StringContent(json), "tokenJson" },
        };
        return await client.PostAsync("/settings/gmail", content);
    }

    [GeneratedRegex("""name="__RequestVerificationToken" value="(?<value>[^"]+)""")]
    private static partial Regex TokenField();

    /// <summary>The shared app with its own data directory and the fake Google endpoints.</summary>
    private sealed class WebApplicationFactoryWithGoogle(NullboxAppFactory baseFactory, string data, FakeGoogle google) : IDisposable
    {
        public Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Factory { get; } = baseFactory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Nullbox:DataDirectory", data);
            b.ConfigureTestServices(s => s.AddSingleton(google.Factory));
        });

        public IServiceProvider Services => Factory.Services;

        public void Dispose() => Factory.Dispose();
    }
}
