using System.Text.RegularExpressions;

namespace Nullbox.Core.SavedRules;

/// <summary>
/// Matches email addresses against a saved rule's <c>from</c> or <c>to</c>, ignoring case:
/// <list type="bullet">
/// <item><c>@shop.example</c> matches any address at that domain or one of its subdomains (<c>a@mail.shop.example</c>).</item>
/// <item><c>me@gmail.com</c> matches that address and its plus-addresses (<c>me+news@gmail.com</c>).</item>
/// <item><c>me+news@gmail.com</c> matches only that plus-address.</item>
/// </list>
/// </summary>
public static partial class AddressPattern
{
    public const int MaxLength = 254;

    /// <summary>Why <paramref name="pattern"/> is not a valid address or <c>@domain</c>; null when it is.</summary>
    public static string? Problem(string? pattern)
    {
        var value = pattern?.Trim() ?? string.Empty;
        if (value.Length == 0)
        {
            return "Give an address (deals@shop.example) or a whole domain (@shop.example).";
        }

        if (value.Length > MaxLength)
        {
            return $"Addresses can be at most {MaxLength} characters.";
        }

        return PatternRegex().IsMatch(value)
            ? null
            : $"'{value}' is not an address or a domain. Write deals@shop.example, or @shop.example for the whole domain.";
    }

    /// <summary>Whether <paramref name="address"/> (a bare address) matches <paramref name="pattern"/>.</summary>
    public static bool Matches(string pattern, string address)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(address);

        var (patternLocal, patternDomain) = Split(pattern.Trim());
        var (local, domain) = Split(address.Trim());
        if (patternDomain.Length == 0 || domain.Length == 0)
        {
            return false;
        }

        if (patternLocal.Length == 0)
        {
            return domain.Equals(patternDomain, StringComparison.OrdinalIgnoreCase)
                || domain.EndsWith("." + patternDomain, StringComparison.OrdinalIgnoreCase);
        }

        if (!domain.Equals(patternDomain, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (local.Equals(patternLocal, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // me@ also matches me+anything@; me+news@ matches only itself (checked above).
        var plus = local.IndexOf('+', StringComparison.Ordinal);
        return !patternLocal.Contains('+', StringComparison.Ordinal)
            && plus > 0
            && local[..plus].Equals(patternLocal, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The bare addresses in a header such as <c>"Doe, Jo" &lt;jo@x.example&gt;, ann@y.example</c>, lowercased, in
    /// order and without repeats.
    /// </summary>
    public static IReadOnlyList<string> Extract(params string?[] headers)
    {
        var found = new List<string>();
        foreach (var header in headers)
        {
            if (string.IsNullOrWhiteSpace(header))
            {
                continue;
            }

            foreach (Match match in AddressRegex().Matches(header))
            {
                var address = match.Value.ToLowerInvariant();
                if (!found.Contains(address))
                {
                    found.Add(address);
                }
            }
        }

        return found;
    }

    private static (string Local, string Domain) Split(string value)
    {
        var at = value.LastIndexOf('@');
        return at < 0 ? (string.Empty, string.Empty) : (value[..at], value[(at + 1)..]);
    }

    [GeneratedRegex(@"^[^@\s<>,;""]*@[^@\s<>,;""]+\.[^@\s<>,;"".]+$")]
    private static partial Regex PatternRegex();

    [GeneratedRegex(@"[^\s<>,;""'()\[\]:@]+@[^\s<>,;""'()\[\]:@]+")]
    private static partial Regex AddressRegex();
}
