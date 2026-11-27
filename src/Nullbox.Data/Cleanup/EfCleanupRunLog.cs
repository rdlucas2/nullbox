using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nullbox.Core.Cleanup;

namespace Nullbox.Data.Cleanup;

/// <summary><see cref="ICleanupRunLog"/> over EF Core (<c>cleanup_runs</c>).</summary>
public sealed class EfCleanupRunLog(NullboxDbContext db, TimeProvider time) : ICleanupRunLog
{
    /// <summary>Error text is cut to this length; it is for people, not for parsing.</summary>
    public const int MaxErrorLength = 2000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<CleanupRun> QueueAsync(string configId, bool dryRun, int sampleSize, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configId);
        var row = new CleanupRunRow
        {
            ConfigId = configId,
            DryRun = dryRun,
            SampleSize = sampleSize,
            Status = CleanupRunStatus.Queued,
            CreatedUtc = time.GetUtcNow().UtcDateTime,
            RulesJson = "[]",
        };
        db.CleanupRuns.Add(row);
        await db.SaveChangesAsync(cancellationToken);
        return ToRun(row);
    }

    public Task MarkRunningAsync(long id, CancellationToken cancellationToken = default)
        => UpdateAsync(id, row => row.Status = CleanupRunStatus.Running, cancellationToken);

    public Task CompleteAsync(long id, CleanupResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        return UpdateAsync(
            id,
            row =>
            {
                row.Status = CleanupRunStatus.Completed;
                row.FinishedUtc = time.GetUtcNow().UtcDateTime;
                row.Label = result.Label;
                row.Matched = result.Matched;
                row.Changed = result.Changed;
                row.RulesJson = JsonSerializer.Serialize(result.Rules, Json);
            },
            cancellationToken);
    }

    public Task FinishWithoutResultAsync(long id, CleanupRunStatus status, string error, CancellationToken cancellationToken = default)
    {
        if (status is not (CleanupRunStatus.Failed or CleanupRunStatus.Blocked))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "Use Failed or Blocked.");
        }

        return UpdateAsync(
            id,
            row =>
            {
                row.Status = status;
                row.FinishedUtc = time.GetUtcNow().UtcDateTime;
                row.Error = error.Length > MaxErrorLength ? error[..MaxErrorLength] : error;
            },
            cancellationToken);
    }

    public async Task<CleanupRun?> GetAsync(long id, CancellationToken cancellationToken = default)
        => await db.CleanupRuns.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, cancellationToken) is { } row ? ToRun(row) : null;

    public async Task<IReadOnlyList<CleanupRun>> ListAsync(int limit = 20, CancellationToken cancellationToken = default)
    {
        var rows = await db.CleanupRuns.AsNoTracking().OrderByDescending(r => r.Id).Take(Math.Clamp(limit, 1, 200)).ToListAsync(cancellationToken);
        return [.. rows.Select(ToRun)];
    }

    private async Task UpdateAsync(long id, Action<CleanupRunRow> change, CancellationToken cancellationToken)
    {
        var row = await db.CleanupRuns.SingleOrDefaultAsync(r => r.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException($"No cleanup run {id}.");
        change(row);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static CleanupRun ToRun(CleanupRunRow row) => new(
        row.Id,
        row.ConfigId,
        row.DryRun,
        row.SampleSize,
        row.Status,
        new DateTimeOffset(DateTime.SpecifyKind(row.CreatedUtc, DateTimeKind.Utc)),
        row.FinishedUtc is { } finished ? new DateTimeOffset(DateTime.SpecifyKind(finished, DateTimeKind.Utc)) : null,
        row.Label,
        row.Matched,
        row.Changed,
        JsonSerializer.Deserialize<List<CleanupRuleResult>>(row.RulesJson, Json) ?? [],
        row.Error);
}
