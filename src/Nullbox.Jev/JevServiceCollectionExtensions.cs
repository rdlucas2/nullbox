using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nullbox.Core.Jev;

namespace Nullbox.Jev;

public static class JevServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IJevClient"/> (HTTP client behind the cache and cost guard) and <see cref="IJevBudget"/>.
    /// The key comes from <c>Nullbox:Jev:ApiKey</c>, else <c>JEV_API_KEY</c>, else <c>TYPESAFE_API_KEY</c>.
    /// The SQLite store defaults to <c>{dataDirectory}/jev.db</c>.
    /// </summary>
    public static IServiceCollection AddNullboxJev(this IServiceCollection services, IConfiguration configuration, string dataDirectory)
    {
        services.AddOptions<JevOptions>()
            .Bind(configuration.GetSection(JevOptions.SectionName))
            .PostConfigure(o =>
            {
                if (string.IsNullOrWhiteSpace(o.ApiKey))
                {
                    o.ApiKey = FirstNonEmpty(configuration["JEV_API_KEY"], configuration["TYPESAFE_API_KEY"]);
                }

                o.DatabasePath ??= Path.Combine(dataDirectory, "jev.db");
            });

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IJevStore>(sp => new SqliteJevStore(sp.GetRequiredService<IOptions<JevOptions>>().Value.DatabasePath!));

        // The pipeline owns retries and per-attempt timeouts, so the HttpClient's own timeout is off.
        // The client lives in a singleton, so connections are recycled by the handler instead of the factory.
        services.AddHttpClient<HttpJevClient>(c => c.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
            .SetHandlerLifetime(Timeout.InfiniteTimeSpan);

        services.TryAddSingleton(sp => new GuardedJevClient(
            sp.GetRequiredService<HttpJevClient>(),
            sp.GetRequiredService<IJevStore>(),
            sp.GetRequiredService<IOptions<JevOptions>>(),
            sp.GetRequiredService<ILogger<GuardedJevClient>>(),
            sp.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<IJevClient>(sp => sp.GetRequiredService<GuardedJevClient>());
        services.TryAddSingleton<IJevBudget>(sp => sp.GetRequiredService<GuardedJevClient>());
        return services;
    }

    private static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}
