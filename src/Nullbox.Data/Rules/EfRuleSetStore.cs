using Microsoft.EntityFrameworkCore;
using Nullbox.Core.Rules;

namespace Nullbox.Data.Rules;

/// <summary><see cref="IRuleSetStore"/> over EF Core. Versions are only ever inserted, never updated.</summary>
public sealed class EfRuleSetStore(NullboxDbContext db, TimeProvider time) : IRuleSetStore
{
    public async Task<IReadOnlyList<RuleSetSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        var rows = await db.RuleSets.AsNoTracking().OrderBy(r => r.Name).Include(r => r.Versions).ToListAsync(cancellationToken);
        return [.. rows.Select(Summarize)];
    }

    public async Task<RuleSetSummary?> GetAsync(string name, CancellationToken cancellationToken = default)
    {
        var row = await db.RuleSets.AsNoTracking().Include(r => r.Versions).SingleOrDefaultAsync(r => r.Name == name, cancellationToken);
        return row is null ? null : Summarize(row);
    }

    public async Task<RuleSetVersion?> GetVersionAsync(string name, int? version = null, CancellationToken cancellationToken = default)
    {
        var set = await db.RuleSets.AsNoTracking().SingleOrDefaultAsync(r => r.Name == name, cancellationToken);
        var wanted = version ?? set?.ActiveVersion;
        if (set is null || wanted is null)
        {
            return null;
        }

        var row = await db.RuleSetVersions.AsNoTracking()
            .SingleOrDefaultAsync(v => v.RuleSetId == set.Id && v.Version == wanted, cancellationToken);
        return row is null ? null : ToVersion(set, row);
    }

    public async Task<IReadOnlyList<RuleSetVersion>> ListVersionsAsync(string name, CancellationToken cancellationToken = default)
    {
        var set = await db.RuleSets.AsNoTracking().SingleOrDefaultAsync(r => r.Name == name, cancellationToken);
        if (set is null)
        {
            return [];
        }

        var rows = await db.RuleSetVersions.AsNoTracking()
            .Where(v => v.RuleSetId == set.Id)
            .OrderByDescending(v => v.Version)
            .ToListAsync(cancellationToken);
        return [.. rows.Select(v => ToVersion(set, v))];
    }

    public async Task<RuleSetVersion> SaveAsync(RuleSetDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        var errors = RuleSetValidator.Validate(document);
        if (errors.Count > 0)
        {
            throw new RuleSetValidationException(errors);
        }

        // Round-trip through the schema too, so stored JSON always passes the same checks an import would.
        var json = RuleJson.Serialize(document);
        var stored = RuleJson.ParseOrThrow(json);
        var hash = RuleJson.Hash(stored);
        var now = time.GetUtcNow().UtcDateTime;

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var set = await db.RuleSets.SingleOrDefaultAsync(r => r.Name == document.Name, cancellationToken);
        if (set is null)
        {
            set = new RuleSetRow { Name = document.Name, CreatedUtc = now };
            db.RuleSets.Add(set);
        }
        else
        {
            var latest = await db.RuleSetVersions.AsNoTracking()
                .Where(v => v.RuleSetId == set.Id)
                .OrderByDescending(v => v.Version)
                .FirstOrDefaultAsync(cancellationToken);
            if (latest?.Hash == hash)
            {
                return ToVersion(set, latest);
            }
        }

        var next = set.Id == 0 ? 1 : 1 + (await db.RuleSetVersions.Where(v => v.RuleSetId == set.Id).MaxAsync(v => (int?)v.Version, cancellationToken) ?? 0);
        var row = new RuleSetVersionRow { Version = next, Json = json, Hash = hash, CreatedUtc = now };
        set.Versions.Add(row);
        set.Description = document.Description;
        set.UpdatedUtc = now;
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return ToVersion(set, row);
    }

    public Task<RuleSetVersion> ImportAsync(string json, CancellationToken cancellationToken = default)
        => SaveAsync(RuleJson.ParseOrThrow(json), cancellationToken);

    public async Task ActivateAsync(string name, int version, CancellationToken cancellationToken = default)
    {
        var set = await db.RuleSets.SingleOrDefaultAsync(r => r.Name == name, cancellationToken)
            ?? throw new KeyNotFoundException($"No rule set named '{name}'.");
        if (!await db.RuleSetVersions.AnyAsync(v => v.RuleSetId == set.Id && v.Version == version, cancellationToken))
        {
            throw new KeyNotFoundException($"Rule set '{name}' has no version {version}.");
        }

        set.ActiveVersion = version;
        set.UpdatedUtc = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default)
        => await db.RuleSets.Where(r => r.Name == name).ExecuteDeleteAsync(cancellationToken) > 0;

    private static RuleSetSummary Summarize(RuleSetRow row)
    {
        var active = row.Versions.SingleOrDefault(v => v.Version == row.ActiveVersion);
        return new RuleSetSummary(
            row.Name,
            row.Description,
            row.Versions.Count == 0 ? 0 : row.Versions.Max(v => v.Version),
            row.ActiveVersion,
            active is null ? null : RuleJson.ParseOrThrow(active.Json).Mode,
            row.UpdatedUtc);
    }

    private static RuleSetVersion ToVersion(RuleSetRow set, RuleSetVersionRow row)
        => new(set.Name, row.Version, RuleJson.ParseOrThrow(row.Json), row.Json, row.Hash, row.CreatedUtc, set.ActiveVersion == row.Version);
}
