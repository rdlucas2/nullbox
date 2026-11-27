using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nullbox.Core.Rules;

namespace Nullbox.Data;

/// <summary>
/// Applies pending migrations, converts stored rule sets that predate the Archive | Delete switch, and, on a database
/// with no rule sets yet, adds the example rule sets.
/// </summary>
public sealed class DatabaseInitializer(IServiceScopeFactory scopes, ILogger<DatabaseInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NullboxDbContext>();
        await db.Database.MigrateAsync(cancellationToken);
        await UpgradeLegacyActionsAsync(db, cancellationToken);

        if (await db.RuleSets.AnyAsync(cancellationToken))
        {
            return;
        }

        var store = scope.ServiceProvider.GetRequiredService<IRuleSetStore>();
        foreach (var seed in RuleSetSeeds.All())
        {
            var saved = await store.SaveAsync(seed, cancellationToken);
            await store.ActivateAsync(saved.Name, saved.Version, cancellationToken);
            logger.LogInformation("Added example rule set {RuleSet} (dry run)", saved.Key);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// The one place a stored version is rewritten: <c>archive</c>/<c>trash</c> actions became the <c>remove</c> switch,
    /// and old JSON would no longer load. Both convert to <c>"remove": "archive"</c>, so nothing starts deleting.
    /// </summary>
    private async Task UpgradeLegacyActionsAsync(NullboxDbContext db, CancellationToken cancellationToken)
    {
        var sets = await db.RuleSets.Include(r => r.Versions).ToListAsync(cancellationToken);
        var upgraded = 0;
        foreach (var (set, row) in sets.SelectMany(s => s.Versions.Select(v => (s, v))))
        {
            if (RuleSetUpgrade.UpgradeLegacyActions(row.Json) is not { } json)
            {
                continue;
            }

            if (RuleJson.Parse(json, out var errors) is not { } document)
            {
                logger.LogError(
                    "Rule set {RuleSet}@v{Version} uses the old archive/trash actions and still fails validation after converting them: {Errors}",
                    set.Name,
                    row.Version,
                    string.Join("; ", errors.Select(e => $"{e.Path} {e.Message}")));
                continue;
            }

            row.Json = json;
            row.Hash = RuleJson.Hash(document);
            upgraded++;
            logger.LogWarning(
                "Rule set {RuleSet}@v{Version}: converted archive/trash actions to \"remove\": \"archive\" (switch rules to delete in the editor once you trust them)",
                set.Name,
                row.Version);
        }

        if (upgraded > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}
