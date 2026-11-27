using System.Net;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Gmail.v1;
using Google.Apis.Services;
using Google.Apis.Util.Store;
using Microsoft.Extensions.Logging;
using Nullbox.Core.Gmail;

namespace Nullbox.Gmail.Auth;

/// <summary>
/// Loads emailBlaster-style <c>credentials.json</c> + <c>token.json</c> from <c>{data}/google/</c>, refreshes the
/// access token without a browser, writes refreshed tokens back atomically, and records when Google rejects the
/// refresh token so jobs stop instead of crash-looping. Token values are never logged.
/// </summary>
public sealed class GmailCredentialStore : IGmailCredentialStore
{
    private const string UserId = "me";

    private readonly GoogleCredentialFiles _files;
    private readonly ILogger<GmailCredentialStore> _logger;
    private readonly TimeProvider _time;
    private readonly Google.Apis.Http.IHttpClientFactory? _httpClientFactory;
    private readonly Lock _gate = new();
    private readonly Dictionary<GmailTokenKind, Entry> _entries = [];

    public GmailCredentialStore(
        GoogleCredentialFiles files,
        ILogger<GmailCredentialStore> logger,
        TimeProvider? time = null,
        Google.Apis.Http.IHttpClientFactory? httpClientFactory = null,
        string? tokenEndpoint = null)
    {
        _files = files;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _httpClientFactory = httpClientFactory;
        _tokenEndpoint = string.IsNullOrWhiteSpace(tokenEndpoint) ? null : tokenEndpoint;
    }

    private readonly string? _tokenEndpoint;

    /// <summary>The credential for <paramref name="kind"/>, reloaded when its file changes on disk.</summary>
    /// <exception cref="GmailNotConfiguredException">No token file.</exception>
    /// <exception cref="GmailReauthorizationRequiredException">Google already refused this token.</exception>
    /// <exception cref="GmailCredentialFormatException">A file is malformed.</exception>
    public UserCredential GetCredential(GmailTokenKind kind)
    {
        lock (_gate)
        {
            var entry = Load(kind);
            if (entry.State == GmailAuthState.ReauthorizationRequired)
            {
                throw new GmailReauthorizationRequiredException(kind, entry.Message ?? "refresh token rejected");
            }

            return entry.Credential!;
        }
    }

    public GmailCredentialStatus GetStatus(GmailTokenKind kind)
    {
        lock (_gate)
        {
            try
            {
                var entry = Load(kind);
                var token = entry.Credential?.Token;
                var expiry = token?.ExpiresInSeconds is { } s ? new DateTimeOffset(token.IssuedUtc, TimeSpan.Zero).AddSeconds(s) : (DateTimeOffset?)null;
                return new GmailCredentialStatus(kind, entry.State, entry.Token!.Scopes, expiry, entry.LastRefresh, entry.Message);
            }
            catch (GmailNotConfiguredException ex)
            {
                return new GmailCredentialStatus(kind, GmailAuthState.NotConfigured, [], null, null, ex.Message);
            }
            catch (GmailCredentialFormatException ex)
            {
                return new GmailCredentialStatus(kind, GmailAuthState.Invalid, [], null, null, ex.Message);
            }
        }
    }

    /// <summary>Marks a token as needing re-authorization after Google refused it mid-request.</summary>
    internal void MarkRejected(GmailTokenKind kind, string reason)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(kind, out var entry))
            {
                entry.State = GmailAuthState.ReauthorizationRequired;
                entry.Message = reason == "deleted_client"
                    ? $"Google rejected the refresh token ({reason}): the OAuth client in credentials.json no longer exists. Create a new OAuth client, then a new token.json with it."
                    : $"Google rejected the refresh token ({reason}). Upload a new token.json.";
            }
        }

        _logger.LogError("Gmail {Kind} token was rejected by Google ({Reason}); jobs will wait for a new token", kind, reason);
    }

    public async Task<string> ReplaceTokenAsync(GmailTokenKind kind, string tokenJson, CancellationToken cancellationToken = default)
    {
        var token = AuthorizedUserToken.Parse(tokenJson);
        var client = ResolveClient(token);
        var store = new MemoryTokenStore();
        var credential = new UserCredential(CreateFlow(client, store), UserId, token.ToTokenResponse(_time.GetUtcNow().UtcDateTime));

        string email;
        try
        {
            using var gmail = new GmailService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                HttpClientFactory = _httpClientFactory ?? new Google.Apis.Http.HttpClientFactory(),
                ApplicationName = "nullbox",
            });
            var profile = await gmail.Users.GetProfile(UserId).ExecuteAsync(cancellationToken);
            email = profile.EmailAddress;
        }
        catch (TokenResponseException ex)
        {
            throw new GmailCredentialFormatException($"Google refused this token ({ex.Error?.Error ?? "error"}). Create a new one.");
        }
        catch (Google.GoogleApiException ex) when (ex.HttpStatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            throw new GmailCredentialFormatException("The token works but has no Gmail access. Re-authorize with a Gmail scope.");
        }

        var saved = (token with { ClientId = client.ClientId, ClientSecret = client.ClientSecret }).WithResponse(credential.Token);
        lock (_gate)
        {
            GoogleCredentialFiles.WriteAtomically(_files.TokenPath(kind), saved.ToJson());
            _entries.Remove(kind);
        }

        _logger.LogInformation("Saved a new Gmail {Kind} token for {Email}", kind, email);
        return email;
    }

    public bool RemoveToken(GmailTokenKind kind)
    {
        bool removed;
        lock (_gate)
        {
            var path = _files.TokenPath(kind);
            removed = File.Exists(path);
            if (removed)
            {
                File.Delete(path);
            }

            _entries.Remove(kind);
        }

        if (removed)
        {
            _logger.LogWarning("Gmail {Kind} token removed (disconnected)", kind);
        }

        return removed;
    }

    private Entry Load(GmailTokenKind kind)
    {
        var path = _files.TokenPath(kind);
        if (!File.Exists(path))
        {
            _entries.Remove(kind);
            throw new GmailNotConfiguredException(kind, path);
        }

        var stamp = File.GetLastWriteTimeUtc(path);
        if (_entries.TryGetValue(kind, out var cached) && cached.FileStamp == stamp)
        {
            return cached;
        }

        var token = AuthorizedUserToken.Parse(File.ReadAllText(path));
        var client = ResolveClient(token);
        var entry = new Entry
        {
            Token = token with { ClientId = client.ClientId, ClientSecret = client.ClientSecret },
            FileStamp = stamp,
            LastRefresh = cached?.LastRefresh ?? new DateTimeOffset(stamp, TimeSpan.Zero),
            State = GmailAuthState.Ready,
        };
        var store = new TokenFileStore(this, kind, entry);
        entry.Credential = new UserCredential(CreateFlow(client, store), UserId, token.ToTokenResponse(_time.GetUtcNow().UtcDateTime));
        _entries[kind] = entry;
        _logger.LogInformation("Loaded Gmail {Kind} token with scopes {Scopes}", kind, string.Join(' ', token.Scopes));
        return entry;
    }

    private GoogleOAuthClient ResolveClient(AuthorizedUserToken token)
    {
        if (!string.IsNullOrWhiteSpace(token.ClientId) && !string.IsNullOrWhiteSpace(token.ClientSecret))
        {
            return new GoogleOAuthClient(token.ClientId, token.ClientSecret);
        }

        return File.Exists(_files.ClientPath)
            ? GoogleOAuthClient.Parse(File.ReadAllText(_files.ClientPath))
            : throw new GmailCredentialFormatException("token.json has no client_id/client_secret and credentials.json is missing.");
    }

    private AuthorizationCodeFlow CreateFlow(GoogleOAuthClient client, IDataStore store)
    {
        var secrets = new ClientSecrets { ClientId = client.ClientId, ClientSecret = client.ClientSecret };
        if (_tokenEndpoint is not null)
        {
            // Testing only (GmailOptions.TokenEndpoint): a plain flow is the only way to choose the token URL.
            var test = new AuthorizationCodeFlow.Initializer(GoogleAuthConsts.OidcAuthorizationUrl, _tokenEndpoint) { ClientSecrets = secrets, DataStore = store };
            if (_httpClientFactory is not null)
            {
                test.HttpClientFactory = _httpClientFactory;
            }

            return new AuthorizationCodeFlow(test);
        }

        var initializer = new GoogleAuthorizationCodeFlow.Initializer { ClientSecrets = secrets, DataStore = store };
        if (_httpClientFactory is not null)
        {
            initializer.HttpClientFactory = _httpClientFactory;
        }

        return new GoogleAuthorizationCodeFlow(initializer);
    }

    /// <summary>Called by the flow after every refresh: persist the new access token next to the old refresh token.</summary>
    private void SaveRefreshed(GmailTokenKind kind, Entry entry, TokenResponse response)
    {
        lock (_gate)
        {
            entry.Token = entry.Token!.WithResponse(response);
            var path = _files.TokenPath(kind);
            GoogleCredentialFiles.WriteAtomically(path, entry.Token.ToJson());
            entry.FileStamp = File.GetLastWriteTimeUtc(path);
            entry.LastRefresh = _time.GetUtcNow();
            entry.State = GmailAuthState.Ready;
            entry.Message = null;
        }

        _logger.LogInformation("Refreshed Gmail {Kind} access token", kind);
    }

    private sealed class Entry
    {
        public AuthorizedUserToken? Token { get; set; }

        public UserCredential? Credential { get; set; }

        public DateTime FileStamp { get; set; }

        public DateTimeOffset? LastRefresh { get; set; }

        public GmailAuthState State { get; set; }

        public string? Message { get; set; }
    }

    /// <summary>
    /// Bridges the Google flow's token store to token.json. Delete is deliberately a no-op: the flow deletes on any
    /// client-side refresh error, and keeping the file lets the settings page say what went wrong.
    /// </summary>
    private sealed class TokenFileStore(GmailCredentialStore owner, GmailTokenKind kind, Entry entry) : IDataStore
    {
        public Task StoreAsync<T>(string key, T value)
        {
            if (value is TokenResponse response)
            {
                owner.SaveRefreshed(kind, entry, response);
            }

            return Task.CompletedTask;
        }

        public Task DeleteAsync<T>(string key) => Task.CompletedTask;

        public Task<T> GetAsync<T>(string key) => Task.FromResult(default(T)!);

        public Task ClearAsync() => Task.CompletedTask;
    }

    private sealed class MemoryTokenStore : IDataStore
    {
        public Task StoreAsync<T>(string key, T value) => Task.CompletedTask;

        public Task DeleteAsync<T>(string key) => Task.CompletedTask;

        public Task<T> GetAsync<T>(string key) => Task.FromResult(default(T)!);

        public Task ClearAsync() => Task.CompletedTask;
    }
}
