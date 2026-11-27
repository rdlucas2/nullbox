using Nullbox.Core.Rules;

namespace Nullbox.Core.SavedRules;

/// <summary>Saved rules in the order they run, with when each was created and last changed.</summary>
public interface ISavedRuleStore
{
    /// <summary>Every rule, in the order they run.</summary>
    Task<IReadOnlyList<StoredSavedRule>> ListAsync(CancellationToken cancellationToken = default);

    Task<StoredSavedRule?> GetAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates and stores <paramref name="rule"/>. With <paramref name="originalName"/> the rule called that is
    /// replaced (and renamed when the names differ), keeping its place; otherwise a rule with the same name is
    /// replaced, or the rule is added last. Throws <see cref="SavedRuleValidationException"/> when it is invalid or a
    /// rename would clash with another rule.
    /// </summary>
    Task<StoredSavedRule> SaveAsync(SavedRule rule, string? originalName = null, CancellationToken cancellationToken = default);

    /// <summary>False if there was no such rule. Its decision log is kept.</summary>
    Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Moves the rule <paramref name="by"/> places (negative is earlier). False if there was no such rule.</summary>
    Task<bool> MoveAsync(string name, int by, CancellationToken cancellationToken = default);

    /// <summary>Every rule as one <see cref="SavedRulesDocument"/> in JSON.</summary>
    Task<string> ExportAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks the whole document first, then replaces rules with the same name in place and adds the others last, in
    /// the document's order. Nothing is stored when any rule is invalid.
    /// </summary>
    Task<SavedRulesImport> ImportAsync(string json, CancellationToken cancellationToken = default);
}

public sealed record StoredSavedRule(SavedRule Rule, int Position, DateTime CreatedUtc, DateTime UpdatedUtc);

public sealed record SavedRulesImport(int Added, int Updated);

public sealed class SavedRuleValidationException(IReadOnlyList<RuleSetError> errors)
    : InvalidOperationException("The saved rule is not valid: " + string.Join("; ", errors.Select(e => e.ToString())))
{
    public IReadOnlyList<RuleSetError> Errors { get; } = errors;
}
