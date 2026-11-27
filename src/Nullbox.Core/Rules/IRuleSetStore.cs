namespace Nullbox.Core.Rules;

/// <summary>
/// Versioned rule-set storage. Every save validates the document and adds a new immutable version; the active version
/// (the one the pipeline uses) only changes through <see cref="ActivateAsync"/>.
/// </summary>
public interface IRuleSetStore
{
    Task<IReadOnlyList<RuleSetSummary>> ListAsync(CancellationToken cancellationToken = default);

    Task<RuleSetSummary?> GetAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>One version, or the active one when <paramref name="version"/> is null. Null if not found or none is active.</summary>
    Task<RuleSetVersion?> GetVersionAsync(string name, int? version = null, CancellationToken cancellationToken = default);

    /// <summary>Every version, newest first.</summary>
    Task<IReadOnlyList<RuleSetVersion>> ListVersionsAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates and stores <paramref name="document"/> as the next version of the rule set it names (creating the
    /// rule set if needed). Saving content identical to the latest version returns that version instead.
    /// Throws <see cref="RuleSetValidationException"/> when the document is invalid.
    /// </summary>
    Task<RuleSetVersion> SaveAsync(RuleSetDocument document, CancellationToken cancellationToken = default);

    /// <summary>Parses, validates and saves rule-set JSON (an export or a hand-written file).</summary>
    Task<RuleSetVersion> ImportAsync(string json, CancellationToken cancellationToken = default);

    /// <summary>Makes <paramref name="version"/> the one in use. Throws <see cref="KeyNotFoundException"/> if it does not exist.</summary>
    Task ActivateAsync(string name, int version, CancellationToken cancellationToken = default);

    /// <summary>Removes the rule set and all its versions. False if it did not exist.</summary>
    Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default);
}

public sealed record RuleSetSummary(
    string Name,
    string? Description,
    int LatestVersion,
    int? ActiveVersion,
    RuleSetMode? ActiveMode,
    DateTime UpdatedUtc);

public sealed record RuleSetVersion(string Name, int Version, RuleSetDocument Document, string Json, string Hash, DateTime CreatedUtc, bool IsActive)
{
    /// <summary>Stable id for caches and the audit log, e.g. <c>promo-cleanup@v3</c>.</summary>
    public string Key => KeyOf(Name, Version);

    public static string KeyOf(string name, int version) => $"{name}@v{version}";

    /// <summary>Splits a <see cref="Key"/> such as <c>promo-cleanup@v3</c> back into name and version.</summary>
    public static bool TryParseKey(string key, out string name, out int version)
    {
        ArgumentNullException.ThrowIfNull(key);
        var at = key.LastIndexOf("@v", StringComparison.Ordinal);
        if (at > 0 && int.TryParse(key.AsSpan(at + 2), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out version))
        {
            name = key[..at];
            return true;
        }

        name = key;
        version = 0;
        return false;
    }
}

public sealed class RuleSetValidationException(IReadOnlyList<RuleSetError> errors)
    : InvalidOperationException("The rule set is not valid: " + string.Join("; ", errors.Select(e => e.ToString())))
{
    public IReadOnlyList<RuleSetError> Errors { get; } = errors;
}

/// <summary>A problem at a JSON pointer-like path such as <c>/decisions/0/when</c>.</summary>
public sealed record RuleSetError(string Path, string Message)
{
    public override string ToString() => $"{(Path.Length == 0 ? "/" : Path)}: {Message}";
}
