using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Google.Apis.Auth.OAuth2.Responses;
using Nullbox.Core.Gmail;

namespace Nullbox.Gmail.Auth;

/// <summary>
/// The authorized-user JSON that Python google-auth writes (<c>creds.to_json()</c>), as emailBlaster's token.json is:
/// <c>token</c>, <c>refresh_token</c>, <c>client_id</c>, <c>client_secret</c>, <c>scopes</c>, <c>expiry</c>.
/// Read and written in the same shape so the file stays usable by both tools.
/// </summary>
public sealed record AuthorizedUserToken
{
    public string? AccessToken { get; init; }

    public required string RefreshToken { get; init; }

    public string? ClientId { get; init; }

    public string? ClientSecret { get; init; }

    public IReadOnlyList<string> Scopes { get; init; } = [];

    public DateTimeOffset? Expiry { get; init; }

    /// <summary>Parses a token file. Messages name the missing field, never a value.</summary>
    public static AuthorizedUserToken Parse(string json)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject ?? throw new GmailCredentialFormatException("token.json must be a JSON object.");
        }
        catch (JsonException)
        {
            throw new GmailCredentialFormatException("token.json is not valid JSON.");
        }

        if (root.ContainsKey("installed") || root.ContainsKey("web"))
        {
            throw new GmailCredentialFormatException("This is an OAuth client file (credentials.json), not a token. Upload token.json.");
        }

        var refresh = Text(root, "refresh_token");
        if (string.IsNullOrWhiteSpace(refresh))
        {
            throw new GmailCredentialFormatException("token.json has no refresh_token.");
        }

        return new AuthorizedUserToken
        {
            AccessToken = Text(root, "token") ?? Text(root, "access_token"),
            RefreshToken = refresh,
            ClientId = Text(root, "client_id"),
            ClientSecret = Text(root, "client_secret"),
            Scopes = ParseScopes(root["scopes"] ?? root["scope"]),
            Expiry = ParseExpiry(Text(root, "expiry")),
        };
    }

    /// <summary>The token after a refresh; Google usually omits the refresh token then, so the old one is kept.</summary>
    public AuthorizedUserToken WithResponse(TokenResponse response)
        => FromResponse(response, ClientId ?? string.Empty, ClientSecret ?? string.Empty, Scopes, RefreshToken);

    public static AuthorizedUserToken FromResponse(
        TokenResponse response,
        string clientId,
        string clientSecret,
        IReadOnlyList<string> fallbackScopes,
        string? fallbackRefreshToken = null)
        => new()
        {
            AccessToken = response.AccessToken,
            RefreshToken = string.IsNullOrEmpty(response.RefreshToken)
                ? fallbackRefreshToken ?? throw new GmailCredentialFormatException("Google returned no refresh token.")
                : response.RefreshToken,
            ClientId = clientId,
            ClientSecret = clientSecret,
            Scopes = string.IsNullOrWhiteSpace(response.Scope) ? fallbackScopes : ParseScopes(JsonValue.Create(response.Scope)),
            Expiry = response.ExpiresInSeconds is { } seconds
                ? new DateTimeOffset(response.IssuedUtc, TimeSpan.Zero).AddSeconds(seconds)
                : null,
        };

    /// <summary>Same field names and expiry format as google-auth, so emailBlaster can still read the file.</summary>
    public string ToJson()
    {
        var root = new JsonObject
        {
            ["token"] = AccessToken,
            ["refresh_token"] = RefreshToken,
            ["token_uri"] = GoogleCredentialFiles.TokenUri,
            ["client_id"] = ClientId,
            ["client_secret"] = ClientSecret,
            ["scopes"] = new JsonArray([.. Scopes.Select(s => (JsonNode?)JsonValue.Create(s))]),
            ["universe_domain"] = "googleapis.com",
            ["account"] = string.Empty,
            ["expiry"] = Expiry?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture),
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public TokenResponse ToTokenResponse(DateTime nowUtc)
    {
        var expiry = Expiry ?? DateTimeOffset.MinValue;
        // No access token or no expiry: mark it already expired so the first request refreshes.
        var issued = AccessToken is null || Expiry is null ? nowUtc.AddHours(-2) : nowUtc;
        var lifetime = AccessToken is null || Expiry is null ? 60 : (long)Math.Max(0, (expiry.UtcDateTime - nowUtc).TotalSeconds);
        return new TokenResponse
        {
            AccessToken = AccessToken ?? string.Empty,
            RefreshToken = RefreshToken,
            Scope = string.Join(' ', Scopes),
            TokenType = "Bearer",
            IssuedUtc = issued,
            ExpiresInSeconds = lifetime,
        };
    }

    internal static IReadOnlyList<string> ParseScopes(JsonNode? node) => node switch
    {
        JsonArray array => [.. array.Select(s => s?.ToString()).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!)],
        JsonValue value when value.TryGetValue<string>(out var text) => text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        _ => [],
    };

    private static DateTimeOffset? ParseExpiry(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        // google-auth writes naive UTC with a trailing Z; treat a value without an offset as UTC too.
        return DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var expiry)
            ? expiry
            : throw new GmailCredentialFormatException("token.json has an unreadable expiry.");
    }

    private static string? Text(JsonObject root, string name)
        => root[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}

/// <summary>The OAuth client from credentials.json (<c>installed</c>, or <c>web</c>).</summary>
public sealed record GoogleOAuthClient(string ClientId, string ClientSecret)
{
    public static GoogleOAuthClient Parse(string json)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject ?? throw new GmailCredentialFormatException("credentials.json must be a JSON object.");
        }
        catch (JsonException)
        {
            throw new GmailCredentialFormatException("credentials.json is not valid JSON.");
        }

        var client = root["installed"] as JsonObject ?? root["web"] as JsonObject
            ?? throw new GmailCredentialFormatException("credentials.json has no \"installed\" (desktop app) client.");
        var id = client["client_id"]?.ToString();
        var secret = client["client_secret"]?.ToString();
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(secret))
        {
            throw new GmailCredentialFormatException("credentials.json is missing client_id or client_secret.");
        }

        return new GoogleOAuthClient(id, secret);
    }
}

/// <summary>File locations and safe writes under <c>{data}/google/</c>.</summary>
public sealed class GoogleCredentialFiles(string dataDirectory)
{
    /// <summary>Refreshes always go to Google's endpoint, never to a <c>token_uri</c> read from an uploaded file.</summary>
    public const string TokenUri = "https://oauth2.googleapis.com/token";

    public string Directory { get; } = Path.Combine(dataDirectory, "google");

    public string ClientPath => Path.Combine(Directory, "credentials.json");

    public string TokenPath(GmailTokenKind kind) => Path.Combine(Directory, kind == GmailTokenKind.Filters ? "token_filters.json" : "token.json");

    /// <summary>Writes via a temp file and rename, so a crash never leaves half a token; owner-only (0600) on Unix.</summary>
    public static void WriteAtomically(string path, string contents)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        System.IO.Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        try
        {
            using (var stream = new FileStream(temp, options))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(contents);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }
}
