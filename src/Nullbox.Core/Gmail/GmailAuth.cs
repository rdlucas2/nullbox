namespace Nullbox.Core.Gmail;

/// <summary>Which token a Gmail client acts with. Filters need the separate <c>gmail.settings.basic</c> consent.</summary>
public enum GmailTokenKind
{
    /// <summary><c>{data}/google/token.json</c>: mail read and modify.</summary>
    Primary,

    /// <summary><c>{data}/google/token_filters.json</c>: Gmail filter settings (optional).</summary>
    Filters,
}

public enum GmailAuthState
{
    /// <summary>No token file yet.</summary>
    NotConfigured,

    /// <summary>Token loaded; it refreshes itself.</summary>
    Ready,

    /// <summary>Google rejected the refresh token (revoked, or a Testing-mode consent screen expired it). A new token.json is needed.</summary>
    ReauthorizationRequired,

    /// <summary>A file is present but unreadable or missing required fields.</summary>
    Invalid,
}

/// <summary>What the settings page shows about one token. Never carries a token value.</summary>
public sealed record GmailCredentialStatus(
    GmailTokenKind Kind,
    GmailAuthState State,
    IReadOnlyList<string> Scopes,
    DateTimeOffset? AccessTokenExpiry,
    DateTimeOffset? LastRefresh,
    string? Message);

/// <summary>Reads and writes the Google client and token files under <c>{data}/google/</c>.</summary>
public interface IGmailCredentialStore
{
    GmailCredentialStatus GetStatus(GmailTokenKind kind);

    /// <summary>Validates an uploaded authorized-user token, proves it with <c>users.getProfile</c>, then saves it.</summary>
    /// <returns>The account's email address.</returns>
    Task<string> ReplaceTokenAsync(GmailTokenKind kind, string tokenJson, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the token file, so jobs stop using Gmail until a new token is uploaded. Google keeps the grant until it
    /// is removed under the Google account's third-party access.
    /// </summary>
    /// <returns>False when there was no token to delete.</returns>
    bool Disconnect(GmailTokenKind kind);
}

/// <summary>Thrown when no token file exists for the requested kind.</summary>
public sealed class GmailNotConfiguredException(GmailTokenKind kind, string path)
    : InvalidOperationException($"Gmail is not connected: {path} is missing. Copy token.json from emailBlaster or run nullbox-auth.")
{
    public GmailTokenKind Kind { get; } = kind;
}

/// <summary>Thrown when Google refuses the refresh token. Jobs should stop and wait for a new token, not retry.</summary>
public sealed class GmailReauthorizationRequiredException(GmailTokenKind kind, string reason, Exception? inner = null)
    : InvalidOperationException($"Gmail needs to be re-authorized ({reason}). Upload a new token.json on the settings page.", inner)
{
    public GmailTokenKind Kind { get; } = kind;
}

/// <summary>Thrown when a credentials or token file is malformed. The message names the problem, never a value.</summary>
public sealed class GmailCredentialFormatException(string message) : FormatException(message);
