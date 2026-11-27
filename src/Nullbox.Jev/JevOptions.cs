namespace Nullbox.Jev;

/// <summary>Settings for the Jev client, bound from <c>Nullbox:Jev</c>.</summary>
public sealed class JevOptions
{
    public const string SectionName = "Nullbox:Jev";

    public Uri BaseUrl { get; set; } = new("https://api.typesafe.ai/");

    /// <summary>Bearer key. Falls back to the <c>JEV_API_KEY</c> or <c>TYPESAFE_API_KEY</c> env var.</summary>
    public string? ApiKey { get; set; }

    public string Model { get; set; } = Nullbox.Core.Jev.JevRequest.DefaultModel;

    /// <summary>Per-attempt timeout.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Retries after the first attempt on 429, 529, 502-504, network errors and timeouts.</summary>
    public int MaxRetries { get; set; } = 4;

    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Upper bound for any single wait, including a server's <c>retry-after</c>.</summary>
    public TimeSpan RetryMaxDelay { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Estimated token ceiling per request (Jev's limit is 64k). The state's longest strings, usually the body,
    /// are cut until the request fits. Estimate: UTF-8 bytes / 4.
    /// </summary>
    public int MaxRequestTokens { get; set; } = 60_000;

    /// <summary>Daily (UTC) input-token budget. Billing is per input token.</summary>
    public long MaxInputTokensPerDay { get; set; } = 2_000_000;

    public int MaxRequestsPerHour { get; set; } = 600;

    public bool CacheEnabled { get; set; } = true;

    /// <summary>SQLite file for the result cache and usage ledger. Defaults to <c>{data}/jev.db</c>.</summary>
    public string? DatabasePath { get; set; }
}
