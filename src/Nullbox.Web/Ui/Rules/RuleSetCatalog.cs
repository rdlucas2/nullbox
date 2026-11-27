using Microsoft.Extensions.Options;
using Nullbox.Core.Gmail;
using Nullbox.Core.Rules;

namespace Nullbox.Web.Ui.Rules;

/// <summary>A row on the rule-set list: the summary, and how many decisions of the version shown are set to Delete.</summary>
public sealed record RuleSetRow(RuleSetSummary Summary, int ShownVersion, int DeleteRules);

/// <summary>What the rule-set pages need beyond <see cref="IRuleSetStore"/>.</summary>
public sealed class RuleSetCatalog(IRuleSetStore store, IServiceProvider services)
{
    /// <summary>Whether the app-wide delete lock is lifted. Locked when Gmail isn't set up.</summary>
    public bool DeletesAllowed => services.GetService<IOptions<GmailOptions>>()?.Value.AllowDelete ?? false;

    /// <summary>Rule sets with their Delete-rule count, from the active version or else the latest.</summary>
    public async Task<IReadOnlyList<RuleSetRow>> ListAsync(CancellationToken cancellationToken = default)
    {
        var rows = new List<RuleSetRow>();
        foreach (var summary in await store.ListAsync(cancellationToken))
        {
            var shown = summary.ActiveVersion ?? summary.LatestVersion;
            var version = await store.GetVersionAsync(summary.Name, shown, cancellationToken);
            rows.Add(new RuleSetRow(summary, shown, version?.Document.Decisions.Count(d => d.Remove == RemoveMode.Delete) ?? 0));
        }

        return rows;
    }

    /// <summary>Saves a copy of the latest version under a free name (<c>name-copy</c>, <c>name-copy-2</c>, ...). Not activated.</summary>
    public async Task<RuleSetVersion> DuplicateAsync(string name, CancellationToken cancellationToken = default)
    {
        var latest = (await store.ListVersionsAsync(name, cancellationToken)).FirstOrDefault()
            ?? throw new KeyNotFoundException($"Rule set '{name}' does not exist.");
        var taken = (await store.ListAsync(cancellationToken)).Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        var copy = FreeName($"{name}-copy", taken);
        return await store.SaveAsync(latest.Document with { Name = copy }, cancellationToken);
    }

    /// <summary>
    /// Changes the active version's mode by saving it as a new version with <paramref name="mode"/> and activating
    /// that. Returns the new active version.
    /// </summary>
    public async Task<RuleSetVersion> SetModeAsync(string name, RuleSetMode mode, CancellationToken cancellationToken = default)
    {
        var active = await store.GetVersionAsync(name, null, cancellationToken)
            ?? throw new InvalidOperationException($"Rule set '{name}' has no active version; activate one first.");
        var saved = active.Document.Mode == mode ? active : await store.SaveAsync(active.Document with { Mode = mode }, cancellationToken);
        await store.ActivateAsync(name, saved.Version, cancellationToken);
        return saved with { IsActive = true };
    }

    public static string FreeName(string wanted, IReadOnlySet<string> taken)
    {
        const int maxLength = 64;
        var stem = wanted.Length > maxLength - 4 ? wanted[..(maxLength - 4)] : wanted;
        if (!taken.Contains(stem))
        {
            return stem;
        }

        for (var n = 2; ; n++)
        {
            var candidate = $"{stem}-{n}";
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
    }
}
