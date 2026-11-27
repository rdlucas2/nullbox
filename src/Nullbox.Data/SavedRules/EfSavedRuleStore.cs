using Microsoft.EntityFrameworkCore;
using Nullbox.Core.Rules;
using Nullbox.Core.SavedRules;

namespace Nullbox.Data.SavedRules;

/// <summary><see cref="ISavedRuleStore"/> over EF Core: one row per rule, ordered by <see cref="SavedRuleRow.Position"/>.</summary>
public sealed class EfSavedRuleStore(NullboxDbContext db, TimeProvider time) : ISavedRuleStore
{
    public async Task<IReadOnlyList<StoredSavedRule>> ListAsync(CancellationToken cancellationToken = default)
    {
        var rows = await db.SavedRules.AsNoTracking().OrderBy(r => r.Position).ThenBy(r => r.Id).ToListAsync(cancellationToken);
        return [.. rows.Select(ToStored)];
    }

    public async Task<StoredSavedRule?> GetAsync(string name, CancellationToken cancellationToken = default)
    {
        var row = await db.SavedRules.AsNoTracking().SingleOrDefaultAsync(r => r.Name == name, cancellationToken);
        return row is null ? null : ToStored(row);
    }

    public async Task<StoredSavedRule> SaveAsync(SavedRule rule, string? originalName = null, CancellationToken cancellationToken = default)
    {
        var json = Check(rule);
        var now = time.GetUtcNow().UtcDateTime;

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var row = await db.SavedRules.SingleOrDefaultAsync(r => r.Name == (originalName ?? rule.Name), cancellationToken);
        if (row is not null && row.Name != rule.Name && await db.SavedRules.AnyAsync(r => r.Name == rule.Name, cancellationToken))
        {
            throw new SavedRuleValidationException([new RuleSetError("/name", $"Another saved rule is already called '{rule.Name}'.")]);
        }

        if (row is null)
        {
            row = new SavedRuleRow { Name = rule.Name, Json = json, Position = await NextPositionAsync(cancellationToken), CreatedUtc = now };
            db.SavedRules.Add(row);
        }

        row.Name = rule.Name;
        row.Json = json;
        row.UpdatedUtc = now;
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return ToStored(row);
    }

    public async Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default)
        => await db.SavedRules.Where(r => r.Name == name).ExecuteDeleteAsync(cancellationToken) > 0;

    public async Task<bool> MoveAsync(string name, int by, CancellationToken cancellationToken = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var rows = await db.SavedRules.OrderBy(r => r.Position).ThenBy(r => r.Id).ToListAsync(cancellationToken);
        var from = rows.FindIndex(r => r.Name == name);
        if (from < 0)
        {
            return false;
        }

        var row = rows[from];
        rows.RemoveAt(from);
        rows.Insert(Math.Clamp(from + by, 0, rows.Count), row);
        for (var i = 0; i < rows.Count; i++)
        {
            rows[i].Position = i;
        }

        row.UpdatedUtc = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<string> ExportAsync(CancellationToken cancellationToken = default)
        => SavedRuleJson.Serialize(new SavedRulesDocument { Rules = [.. (await ListAsync(cancellationToken)).Select(r => r.Rule)] });

    public async Task<SavedRulesImport> ImportAsync(string json, CancellationToken cancellationToken = default)
    {
        var document = SavedRuleJson.Parse(json, out var errors) ?? throw new SavedRuleValidationException(errors);
        var now = time.GetUtcNow().UtcDateTime;

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var existing = await db.SavedRules.ToDictionaryAsync(r => r.Name, StringComparer.Ordinal, cancellationToken);
        var next = await NextPositionAsync(cancellationToken);
        int added = 0, updated = 0;
        foreach (var rule in document.Rules)
        {
            var ruleJson = SavedRuleJson.Serialize(rule);
            if (existing.TryGetValue(rule.Name, out var row))
            {
                row.Json = ruleJson;
                row.UpdatedUtc = now;
                updated++;
            }
            else
            {
                db.SavedRules.Add(new SavedRuleRow { Name = rule.Name, Json = ruleJson, Position = next++, CreatedUtc = now, UpdatedUtc = now });
                added++;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return new SavedRulesImport(added, updated);
    }

    /// <summary>Validates <paramref name="rule"/> and returns the JSON to store, which always parses back.</summary>
    private static string Check(SavedRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var errors = SavedRuleValidator.Validate(rule);
        if (errors.Count > 0)
        {
            throw new SavedRuleValidationException(errors);
        }

        var json = SavedRuleJson.Serialize(rule);
        _ = SavedRuleJson.ParseRule(json, out errors) ?? throw new SavedRuleValidationException(errors);
        return json;
    }

    private async Task<int> NextPositionAsync(CancellationToken cancellationToken)
        => 1 + (await db.SavedRules.MaxAsync(r => (int?)r.Position, cancellationToken) ?? -1);

    private static StoredSavedRule ToStored(SavedRuleRow row)
        => new(SavedRuleJson.ParseRule(row.Json, out _) ?? throw new InvalidOperationException($"Saved rule '{row.Name}' is stored with invalid JSON."), row.Position, row.CreatedUtc, row.UpdatedUtc);
}
