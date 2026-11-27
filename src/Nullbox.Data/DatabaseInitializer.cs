using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nullbox.Core.Rules;

namespace Nullbox.Data;

/// <summary>Applies pending migrations and, on a database with no rule sets yet, adds the example rule sets.</summary>
public sealed class DatabaseInitializer(IServiceScopeFactory scopes, ILogger<DatabaseInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NullboxDbContext>();
        await db.Database.MigrateAsync(cancellationToken);

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
}
