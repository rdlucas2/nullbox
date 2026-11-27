using Microsoft.Extensions.Logging.Abstractions;
using Nullbox.Core.Gmail;
using Nullbox.Gmail.Auth;

namespace Nullbox.Tests.Gmail.Auth;

public sealed class GmailCredentialStoreTests : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "nullbox-auth-" + Guid.NewGuid().ToString("N"));
    private readonly FakeGoogle _google = new();
    private readonly GoogleCredentialFiles _files;

    public GmailCredentialStoreTests() => _files = new GoogleCredentialFiles(_data);

    public void Dispose()
    {
        if (Directory.Exists(_data))
        {
            Directory.Delete(_data, recursive: true);
        }
    }

    [Fact]
    public void Missing_token_reports_not_configured()
    {
        var store = Store();

        Assert.Equal(GmailAuthState.NotConfigured, store.GetStatus(GmailTokenKind.Primary).State);
        Assert.Throws<GmailNotConfiguredException>(() => store.GetCredential(GmailTokenKind.Primary));
    }

    [Fact]
    public void Loads_an_emailblaster_token_with_its_scopes_and_expiry()
    {
        var expiry = DateTimeOffset.UtcNow.AddMinutes(30);
        WriteToken(TokenSamples.EmailBlasterToken(expiry));

        var status = Store().GetStatus(GmailTokenKind.Primary);

        Assert.Equal(GmailAuthState.Ready, status.State);
        Assert.Equal(["https://www.googleapis.com/auth/gmail.modify"], status.Scopes);
        Assert.InRange(status.AccessTokenExpiry!.Value, expiry.AddSeconds(-5), expiry.AddSeconds(5));
    }

    [Fact]
    public async Task Valid_token_is_used_without_refreshing()
    {
        WriteToken(TokenSamples.EmailBlasterToken(DateTimeOffset.UtcNow.AddMinutes(30)));

        await Factory().Create().Users.GetProfile("me").ExecuteAsync();

        Assert.Empty(_google.TokenRequests);
        Assert.Equal("Bearer old-access-token", Assert.Single(_google.ProfileAuthorizations));
    }

    [Fact]
    public async Task Expired_token_refreshes_and_writes_back_keeping_the_refresh_token()
    {
        WriteToken(TokenSamples.EmailBlasterToken(DateTimeOffset.UtcNow.AddHours(-1)));
        var store = Store();

        await new GmailClientFactory(store, _google.Factory).Create().Users.GetProfile("me").ExecuteAsync();

        var refresh = Assert.Single(_google.TokenRequests)!;
        Assert.Contains("grant_type=refresh_token", refresh, StringComparison.Ordinal);
        Assert.Contains("refresh_token=the-refresh-token", refresh, StringComparison.Ordinal);
        Assert.Equal("Bearer new-access-token", Assert.Single(_google.ProfileAuthorizations));

        var saved = AuthorizedUserToken.Parse(File.ReadAllText(_files.TokenPath(GmailTokenKind.Primary)));
        Assert.Equal("new-access-token", saved.AccessToken);
        Assert.Equal("the-refresh-token", saved.RefreshToken);
        Assert.True(saved.Expiry > DateTimeOffset.UtcNow.AddMinutes(50));
        Assert.NotNull(store.GetStatus(GmailTokenKind.Primary).LastRefresh);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(_files.TokenPath(GmailTokenKind.Primary)));
        }

        Assert.Empty(Directory.GetFiles(_files.Directory, "*.tmp"));
    }

    [Theory]
    [InlineData("invalid_grant")]
    [InlineData("invalid_client")]
    [InlineData("unauthorized_client")]
    [InlineData("deleted_client")]
    public async Task Revoked_token_surfaces_reauthorization_and_stops_calling_google(string error)
    {
        WriteToken(TokenSamples.EmailBlasterToken(DateTimeOffset.UtcNow.AddHours(-1)));
        _google.Token = _ => FakeGoogle.TokenError(error);
        var store = Store();
        var gmail = new GmailClientFactory(store, _google.Factory).Create();

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => gmail.Users.GetProfile("me").ExecuteAsync());
        Assert.IsType<GmailReauthorizationRequiredException>(Unwrap(ex));

        var status = store.GetStatus(GmailTokenKind.Primary);
        Assert.Equal(GmailAuthState.ReauthorizationRequired, status.State);
        Assert.Contains(error, status.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(_files.TokenPath(GmailTokenKind.Primary)), "the rejected token stays for diagnosis");

        var calls = _google.Requests.Count;
        await Assert.ThrowsAnyAsync<Exception>(() => gmail.Users.GetProfile("me").ExecuteAsync());
        Assert.Equal(calls, _google.Requests.Count);
    }

    [Fact]
    public async Task A_new_token_file_clears_the_reauthorization_state_without_a_restart()
    {
        WriteToken(TokenSamples.EmailBlasterToken(DateTimeOffset.UtcNow.AddHours(-1)));
        _google.Token = _ => FakeGoogle.TokenError("invalid_grant");
        var store = Store();
        var gmail = new GmailClientFactory(store, _google.Factory).Create();
        await Assert.ThrowsAnyAsync<Exception>(() => gmail.Users.GetProfile("me").ExecuteAsync());

        WriteToken(TokenSamples.EmailBlasterToken(DateTimeOffset.UtcNow.AddMinutes(30)));
        File.SetLastWriteTimeUtc(_files.TokenPath(GmailTokenKind.Primary), DateTime.UtcNow.AddSeconds(5));

        await gmail.Users.GetProfile("me").ExecuteAsync();
        Assert.Equal(GmailAuthState.Ready, store.GetStatus(GmailTokenKind.Primary).State);
    }

    [Fact]
    public async Task Client_id_falls_back_to_credentials_json()
    {
        WriteToken(TokenSamples.EmailBlasterToken(DateTimeOffset.UtcNow.AddHours(-1), withClient: false));
        File.WriteAllText(_files.ClientPath, TokenSamples.ClientFile);

        await Factory().Create().Users.GetProfile("me").ExecuteAsync();

        Assert.Contains("client_id=123-abc.apps.googleusercontent.com", Assert.Single(_google.TokenRequests), StringComparison.Ordinal);
    }

    [Fact]
    public void Token_without_client_and_no_credentials_json_is_invalid()
    {
        WriteToken(TokenSamples.EmailBlasterToken(DateTimeOffset.UtcNow, withClient: false));

        var status = Store().GetStatus(GmailTokenKind.Primary);

        Assert.Equal(GmailAuthState.Invalid, status.State);
        Assert.Contains("credentials.json", status.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not json", "not valid JSON")]
    [InlineData("[1,2]", "JSON object")]
    [InlineData("""{"token":"x"}""", "refresh_token")]
    [InlineData("""{"installed":{"client_id":"a"}}""", "credentials.json")]
    [InlineData("""{"refresh_token":"r","expiry":"soon"}""", "expiry")]
    public void Malformed_token_files_are_reported_without_values(string json, string expected)
    {
        WriteToken(json);

        var status = Store().GetStatus(GmailTokenKind.Primary);

        Assert.Equal(GmailAuthState.Invalid, status.State);
        Assert.Contains(expected, status.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Replace_token_tests_with_get_profile_then_saves()
    {
        var store = Store();

        var email = await store.ReplaceTokenAsync(GmailTokenKind.Primary, TokenSamples.EmailBlasterToken(DateTimeOffset.UtcNow.AddMinutes(30)));

        Assert.Equal("ryan@example.com", email);
        Assert.Single(_google.ProfileAuthorizations);
        Assert.Equal(GmailAuthState.Ready, store.GetStatus(GmailTokenKind.Primary).State);
        Assert.Equal("the-refresh-token", AuthorizedUserToken.Parse(File.ReadAllText(_files.TokenPath(GmailTokenKind.Primary))).RefreshToken);
    }

    [Fact]
    public async Task Replace_token_refuses_a_revoked_token_and_keeps_the_old_file()
    {
        WriteToken(TokenSamples.EmailBlasterToken(DateTimeOffset.UtcNow.AddMinutes(30)));
        var before = File.ReadAllText(_files.TokenPath(GmailTokenKind.Primary));
        _google.Token = _ => FakeGoogle.TokenError("invalid_grant");

        var ex = await Assert.ThrowsAsync<GmailCredentialFormatException>(() =>
            Store().ReplaceTokenAsync(GmailTokenKind.Primary, TokenSamples.EmailBlasterToken(DateTimeOffset.UtcNow.AddHours(-1))));

        Assert.Contains("invalid_grant", ex.Message, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllText(_files.TokenPath(GmailTokenKind.Primary)));
    }

    [Fact]
    public async Task Replace_token_refuses_a_token_without_gmail_access()
    {
        _google.Profile = () => FakeGoogle.Json(new { error = new { code = 403, message = "insufficient scopes" } }, System.Net.HttpStatusCode.Forbidden);

        await Assert.ThrowsAsync<GmailCredentialFormatException>(() =>
            Store().ReplaceTokenAsync(GmailTokenKind.Filters, TokenSamples.EmailBlasterToken(DateTimeOffset.UtcNow.AddMinutes(30))));

        Assert.False(File.Exists(_files.TokenPath(GmailTokenKind.Filters)));
    }

    [Fact]
    public void Written_token_round_trips_in_google_auth_format()
    {
        var token = AuthorizedUserToken.Parse(TokenSamples.EmailBlasterToken(new DateTimeOffset(2026, 11, 27, 5, 0, 0, TimeSpan.Zero)));

        var json = token.ToJson();
        var again = AuthorizedUserToken.Parse(json);

        Assert.Equal(token with { Scopes = again.Scopes }, again);
        Assert.Equal(token.Scopes, again.Scopes);
        Assert.Contains("\"expiry\": \"2026-11-27T05:00:00.000000Z\"", json, StringComparison.Ordinal);
        Assert.Contains("\"token_uri\": \"https://oauth2.googleapis.com/token\"", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, false, GmailScopes.Modify)]
    [InlineData(null, true, GmailScopes.SettingsBasic)]
    [InlineData("full", false, GmailScopes.Full)]
    [InlineData("modify, settings, modify", false, GmailScopes.Modify + " " + GmailScopes.SettingsBasic)]
    public void Scope_names_resolve(string? list, bool filters, string expected)
        => Assert.Equal(expected, string.Join(' ', GmailScopes.Resolve(list, filters)));

    [Fact]
    public void Unknown_scope_name_is_rejected()
        => Assert.Throws<ArgumentException>(() => GmailScopes.Resolve("everything"));

    private static Exception Unwrap(Exception ex)
    {
        while (ex is not Core.Gmail.GmailReauthorizationRequiredException && ex.InnerException is { } inner)
        {
            ex = inner;
        }

        return ex;
    }

    private GmailCredentialStore Store() => new(_files, NullLogger<GmailCredentialStore>.Instance, httpClientFactory: _google.Factory);

    private GmailClientFactory Factory() => new(Store(), _google.Factory);

    private void WriteToken(string json, GmailTokenKind kind = GmailTokenKind.Primary)
    {
        Directory.CreateDirectory(_files.Directory);
        File.WriteAllText(_files.TokenPath(kind), json);
    }
}
