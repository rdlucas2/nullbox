using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nullbox.Core.Pipeline;

namespace Nullbox.Data.Pipeline;

/// <summary><see cref="IDecisionLog"/> and <see cref="IPipelineSettingsStore"/> over EF Core.</summary>
public sealed class EfDecisionLog(NullboxDbContext db) : IDecisionLog, IPipelineSettingsStore
{
    public const string KillSwitchKey = "pipeline.kill_switch";

    /// <summary>Error text is cut to this length; it is for people, not for parsing.</summary>
    public const int MaxErrorLength = 2000;

    public async Task RecordAsync(DecisionRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        db.Decisions.Add(new DecisionRow
        {
            MessageId = record.MessageId,
            RuleSetKey = record.RuleSetKey,
            Mode = record.Mode,
            Outcome = record.Outcome,
            DecisionsJson = JsonSerializer.Serialize(record.Decisions),
            ActionsJson = JsonSerializer.Serialize(record.Actions),
            SkippedJson = JsonSerializer.Serialize(record.Skipped),
            Model = record.Model,
            AnswersJson = record.AnswersJson,
            CreatedUtc = record.At.UtcDateTime,
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    public Task<bool> HasRecordAsync(string messageId, string ruleSetKey, CancellationToken cancellationToken = default)
        => db.Decisions.AnyAsync(d => d.MessageId == messageId && d.RuleSetKey == ruleSetKey, cancellationToken);

    public async Task<IReadOnlyList<DecisionRecord>> ListAsync(string? messageId = null, int limit = 50, CancellationToken cancellationToken = default)
    {
        var query = db.Decisions.AsNoTracking();
        if (messageId is not null)
        {
            query = query.Where(d => d.MessageId == messageId);
        }

        var rows = await query.OrderByDescending(d => d.Id).Take(Math.Clamp(limit, 1, 500)).ToListAsync(cancellationToken);
        return [.. rows.Select(ToRecord)];
    }

    public Task<int> CountAppliedSinceAsync(DateTimeOffset since, CancellationToken cancellationToken = default)
    {
        var from = since.UtcDateTime;
        return db.Decisions.Where(d => d.Outcome == DecisionOutcome.Applied && d.CreatedUtc >= from)
            .Select(d => d.MessageId).Distinct().CountAsync(cancellationToken);
    }

    public async Task<int> RecordFailureAsync(string messageId, string error, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        var row = await db.MessageFailures.FindAsync([messageId], cancellationToken);
        if (row is null)
        {
            row = new MessageFailureRow { MessageId = messageId };
            db.MessageFailures.Add(row);
        }

        row.Attempts++;
        row.LastError = Cut(error);
        row.UpdatedUtc = at.UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
        return row.Attempts;
    }

    public async Task ClearFailuresAsync(string messageId, CancellationToken cancellationToken = default)
        => await db.MessageFailures.Where(f => f.MessageId == messageId && !f.Poison).ExecuteDeleteAsync(cancellationToken);

    public async Task MarkPoisonAsync(string messageId, string error, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        var row = await db.MessageFailures.FindAsync([messageId], cancellationToken);
        if (row is null)
        {
            row = new MessageFailureRow { MessageId = messageId, Attempts = 1 };
            db.MessageFailures.Add(row);
        }

        row.Poison = true;
        row.LastError = Cut(error);
        row.UpdatedUtc = at.UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PoisonMessage>> ListPoisonAsync(int limit = 50, CancellationToken cancellationToken = default)
    {
        var rows = await db.MessageFailures.AsNoTracking().Where(f => f.Poison)
            .OrderByDescending(f => f.UpdatedUtc).Take(Math.Clamp(limit, 1, 500)).ToListAsync(cancellationToken);
        return [.. rows.Select(f => new PoisonMessage(f.MessageId, f.Attempts, f.LastError ?? string.Empty, AsUtc(f.UpdatedUtc)))];
    }

    public async Task<bool> GetKillSwitchAsync(CancellationToken cancellationToken = default)
    {
        var row = await db.AppSettings.AsNoTracking().SingleOrDefaultAsync(a => a.Key == KillSwitchKey, cancellationToken);
        return row?.Value == "on";
    }

    public async Task SetKillSwitchAsync(bool on, CancellationToken cancellationToken = default)
    {
        var row = await db.AppSettings.FindAsync([KillSwitchKey], cancellationToken);
        if (row is null)
        {
            row = new AppSettingRow { Key = KillSwitchKey };
            db.AppSettings.Add(row);
        }

        row.Value = on ? "on" : "off";
        await db.SaveChangesAsync(cancellationToken);
    }

    private static DecisionRecord ToRecord(DecisionRow row) => new(
        row.MessageId,
        row.RuleSetKey,
        row.Mode,
        row.Outcome,
        JsonSerializer.Deserialize<string[]>(row.DecisionsJson) ?? [],
        JsonSerializer.Deserialize<string[]>(row.ActionsJson) ?? [],
        JsonSerializer.Deserialize<string[]>(row.SkippedJson) ?? [],
        row.Model ?? string.Empty,
        row.AnswersJson,
        AsUtc(row.CreatedUtc));

    private static DateTimeOffset AsUtc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static string Cut(string text) => text.Length <= MaxErrorLength ? text : text[..MaxErrorLength];
}
