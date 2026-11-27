namespace Nullbox.Gmail.Auth;

/// <summary>Short names for the Gmail scopes nullbox asks for, as used by <c>nullbox-auth --scopes</c>.</summary>
public static class GmailScopes
{
    public const string Modify = "https://www.googleapis.com/auth/gmail.modify";
    public const string Full = "https://mail.google.com/";
    public const string Readonly = "https://www.googleapis.com/auth/gmail.readonly";
    public const string SettingsBasic = "https://www.googleapis.com/auth/gmail.settings.basic";

    private static readonly Dictionary<string, string> ByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["modify"] = Modify,
        ["full"] = Full,
        ["readonly"] = Readonly,
        ["settings"] = SettingsBasic,
    };

    public static IReadOnlyCollection<string> Names => ByName.Keys;

    /// <summary>
    /// Resolves a comma-separated list of short names or full scope URLs. Null or empty gives the default for
    /// the token kind: <c>modify</c> for mail, <c>settings</c> for filters.
    /// </summary>
    public static IReadOnlyList<string> Resolve(string? list, bool filters = false)
    {
        if (string.IsNullOrWhiteSpace(list))
        {
            return [filters ? SettingsBasic : Modify];
        }

        var scopes = new List<string>();
        foreach (var part in list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var scope = ByName.TryGetValue(part, out var known)
                ? known
                : part.StartsWith("https://", StringComparison.Ordinal)
                    ? part
                    : throw new ArgumentException($"Unknown scope '{part}'. Use {string.Join(", ", Names)} or a full scope URL.");
            if (!scopes.Contains(scope, StringComparer.Ordinal))
            {
                scopes.Add(scope);
            }
        }

        return scopes;
    }
}
