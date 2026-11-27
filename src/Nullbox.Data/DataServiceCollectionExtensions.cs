using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nullbox.Core.Cleanup;
using Nullbox.Core.Pipeline;
using Nullbox.Core.Rules;
using Nullbox.Core.SavedRules;
using Nullbox.Data.Cleanup;
using Nullbox.Data.Pipeline;
using Nullbox.Data.Rules;
using Nullbox.Data.SavedRules;

namespace Nullbox.Data;

public static class DataServiceCollectionExtensions
{
    public const string DatabaseFileName = "nullbox.db";

    /// <summary>
    /// Registers <see cref="NullboxDbContext"/> on <c>{dataDirectory}/nullbox.db</c>, the rule-set store, the decision log, the cleanup run log, and the
    /// start-up migration and seeding.
    /// </summary>
    public static IServiceCollection AddNullboxData(this IServiceCollection services, string dataDirectory)
    {
        var path = Path.Combine(Path.GetFullPath(dataDirectory), DatabaseFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        services.AddDbContext<NullboxDbContext>(o => o.UseSqlite($"Data Source={path}"));
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IRuleSetStore, EfRuleSetStore>();
        services.AddScoped<EfDecisionLog>();
        services.AddScoped<IDecisionLog>(sp => sp.GetRequiredService<EfDecisionLog>());
        services.AddScoped<IPipelineSettingsStore>(sp => sp.GetRequiredService<EfDecisionLog>());
        services.AddScoped<ICleanupRunLog, EfCleanupRunLog>();
        services.AddScoped<ISavedRuleStore, EfSavedRuleStore>();
        services.AddHostedService<DatabaseInitializer>();
        return services;
    }
}
