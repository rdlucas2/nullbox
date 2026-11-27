using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nullbox.Core.Pipeline;
using Nullbox.Core.Rules;

namespace Nullbox.Data.Pipeline;

/// <summary><see cref="IDecisionLog"/> and <see cref="IPipelineSettingsStore"/> over EF Core.</summary>
public sealed class EfDecisionLog(NullboxDbContext db) : IDecisionLog, IPipelineSettingsStore
{
    public const string KillSwitchKey = "pipeline.kill_switch";

    /// <summary>Error text is cut to this length; it is for people, not for parsing.</summary>
    public const int MaxErrorLength = 2000;

    /// <summary>Largest page <see cref="QueryAsync"/> returns.</summary>
    public const int MaxPageSize = 500;

    public async Task RecordAsync(DecisionRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        RuleSetVersion.TryParseKey(record.RuleSetKey, out var name, out var version);
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
            RuleSetName = name,
            RuleSetVersion = version,
            InputsHash = record.InputsHash,
            StateJson = record.StateJson,
            LabelsJson = JsonSerializer.Serialize(record.Labels),
            Confidence = record.Confidence,
            Error = record.Error is null ? null : Cut(record.Error),
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    public Task<bool> HasRecordAsync(string messageId, string ruleSetKey, CancellationToken cancellationToken = default)
        => db.Decisions.AnyAsync(
            d => d.MessageId == messageId && d.RuleSetKey == ruleSetKey && d.Outcome != DecisionOutcome.Error,
            cancellationToken);

    public async Task<IReadOnlyList<DecisionRecord>> ListAsync(string? messageId = null, int limit = 50, CancellationToken cancellationToken = default)
        => (await QueryAsync(new DecisionQuery { MessageId = messageId, Limit = limit }, cancellationToken)).Items;

    public async Task<DecisionPage> QueryAsync(DecisionQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var rows = db.Decisions.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query.MessageId))
        {
            rows = rows.Where(d => d.MessageId == query.MessageId);
        }

        if (!string.IsNullOrWhiteSpace(query.RuleSet))
        {
            rows = rows.Where(d => d.RuleSetName == query.RuleSet);
            if (query.Version is { } version)
            {
                rows = rows.Where(d => d.RuleSetVersion == version);
            }
        }

        if (!string.IsNullOrWhiteSpace(query.Action))
        {
            // Actions are stored as a JSON array of strings, so an element starting with the action contains its
            // JSON-encoded form minus the closing quote.
            var encoded = JsonSerializer.Serialize(query.Action.Trim());
            var needle = encoded[..^1];
            rows = rows.Where(d => d.ActionsJson.Contains(needle));
        }

        if (query.Mode is { } mode)
        {
            rows = rows.Where(d => d.Mode == mode);
        }

        if (query.Outcome is { } outcome)
        {
            rows = rows.Where(d => d.Outcome == outcome);
        }

        if (query.MinConfidence is { } min)
        {
            rows = rows.Where(d => d.Confidence != null && d.Confidence >= min);
        }

        if (query.MaxConfidence is { } max)
        {
            rows = rows.Where(d => d.Confidence != null && d.Confidence <= max);
        }

        if (query.Before is { } before)
        {
            rows = rows.Where(d => d.Id < before);
        }

        var limit = Math.Clamp(query.Limit, 1, MaxPageSize);
        var page = await rows.OrderByDescending(d => d.Id).Take(limit + 1).ToListAsync(cancellationToken);
        var items = page.Take(limit).Select(ToRecord).ToList();
        return new DecisionPage(items, page.Count > limit ? items[^1].Id : null);
    }

    public Task<int> PurgeAsync(DateTimeOffset before, CancellationToken cancellationToken = default)
    {
        var cutoff = before.UtcDateTime;
        return db.Decisions.Where(d => d.CreatedUtc < cutoff).ExecuteDeleteAsync(cancellationToken);
    }

    public Task<int> CountAppliedSinceAsync(DateTimeOffset since, CancellationToken cancellationToken = default)
    {
        var from = since.UtcDateTime;
        return db.Decisions.Where(d => d.Outcome == DecisionOutcome.Applied && d.CreatedUtc >= from)
            .Select(d => d.MessageId).Distinct().CountAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<DecisionOutcome, int>> CountByOutcomeSinceAsync(DateTimeOffset since, CancellationToken cancellationToken = default)
    {
        var from = since.UtcDateTime;
        var counts = await db.Decisions.Where(d => d.CreatedUtc >= from)
            .GroupBy(d => d.Outcome)
            .Select(g => new { Outcome = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        return counts.ToDictionary(c => c.Outcome, c => c.Count);
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
        AsUtc(row.CreatedUtc))
    {
        Id = row.Id,
        InputsHash = row.InputsHash,
        StateJson = row.StateJson,
        Labels = row.LabelsJson is null ? [] : JsonSerializer.Deserialize<string[]>(row.LabelsJson) ?? [],
        Confidence = row.Confidence,
        Error = row.Error,
    };

    private static DateTimeOffset AsUtc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static string Cut(string text) => text.Length <= MaxErrorLength ? text : text[..MaxErrorLength];
}
