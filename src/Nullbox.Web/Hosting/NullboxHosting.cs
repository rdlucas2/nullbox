using System.Text.RegularExpressions;
using Hangfire;
using Hangfire.Storage.SQLite;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Nullbox.Core.Gmail;
using Nullbox.Core.Jobs;
using Nullbox.Core.Options;
using Nullbox.Web.Auth;
using Nullbox.Web.Jobs;

namespace Nullbox.Web.Hosting;

public static partial class NullboxHosting
{
    /// <summary>Binds <see cref="NullboxOptions"/>, honouring the container's plain env vars as fallbacks.</summary>
    public static IServiceCollection AddNullboxOptions(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<NullboxOptions>()
            .Bind(configuration.GetSection(NullboxOptions.SectionName))
            .PostConfigure(options => ApplyEnvironmentFallbacks(options, configuration))
            .Validate(o => o.Hangfire.WorkerCount >= 1, "Nullbox:Hangfire:WorkerCount must be at least 1.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.DataDirectory), "Nullbox:DataDirectory is required.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.Jobs.HeartbeatCron), "Nullbox:Jobs:HeartbeatCron is required.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.Jobs.InboxPollCron), "Nullbox:Jobs:InboxPollCron is required.")
            .Validate(o => o.Jobs.MessageWorkers >= 1, "Nullbox:Jobs:MessageWorkers must be at least 1.")
            .Validate(o => ResyncWindowPattern().IsMatch(o.Jobs.InboxResyncWindow ?? string.Empty), "Nullbox:Jobs:InboxResyncWindow must look like 2d, 12h or 1m (Gmail newer_than:).")
            .ValidateOnStart();
        return services;
    }

    /// <summary>
    /// <c>ADMIN_PASSWORD</c> and <c>NULLBOX_DATA_DIR</c> (as set by docker-compose) apply when the
    /// <c>Nullbox:</c> keys are not set explicitly.
    /// </summary>
    public static void ApplyEnvironmentFallbacks(NullboxOptions options, IConfiguration configuration)
    {
        if (string.IsNullOrEmpty(configuration[$"{NullboxOptions.SectionName}:{nameof(NullboxOptions.AdminPassword)}"]))
        {
            var password = configuration["ADMIN_PASSWORD"];
            if (!string.IsNullOrEmpty(password))
            {
                options.AdminPassword = password;
            }
        }

        if (string.IsNullOrEmpty(configuration[$"{NullboxOptions.SectionName}:{nameof(NullboxOptions.DataDirectory)}"]))
        {
            var dataDir = configuration["NULLBOX_DATA_DIR"];
            if (!string.IsNullOrEmpty(dataDir))
            {
                options.DataDirectory = dataDir;
            }
        }
    }

    /// <summary>Resolves the data directory the same way options binding does, for start-up wiring.</summary>
    public static string ResolveDataDirectory(IConfiguration configuration)
    {
        var options = new NullboxOptions();
        configuration.GetSection(NullboxOptions.SectionName).Bind(options);
        ApplyEnvironmentFallbacks(options, configuration);
        return Path.GetFullPath(options.DataDirectory);
    }

    /// <summary>
    /// Keeps ASP.NET Core data-protection keys (login cookies, antiforgery) in <c>{data}/keys</c> so sign-ins
    /// survive container restarts.
    /// </summary>
    public static IServiceCollection AddNullboxDataProtection(this IServiceCollection services, IConfiguration configuration)
    {
        var keys = DataDirectoryAccess.EnsureWritable(Path.Combine(ResolveDataDirectory(configuration), "keys"));
        services.AddDataProtection()
            .SetApplicationName(Nullbox.Core.AppInfo.Name)
            .PersistKeysToFileSystem(keys);
        return services;
    }

    public static IServiceCollection AddNullboxJobs(this IServiceCollection services)
    {
        services.AddHangfire((sp, config) =>
        {
            var options = sp.GetRequiredService<IOptions<NullboxOptions>>().Value;
            config
                .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
                .UseSimpleAssemblyNameTypeSerializer()
                .UseRecommendedSerializerSettings();

            switch (options.Hangfire.Storage)
            {
                case HangfireStorageKind.InMemory:
                    config.UseInMemoryStorage();
                    break;
                case HangfireStorageKind.SQLite:
                    config.UseSQLiteStorage(SqlitePath(options));
                    break;
                default:
                    throw new InvalidOperationException($"Unknown Hangfire storage '{options.Hangfire.Storage}'.");
            }
        });

        services.AddHangfireServer((sp, server) =>
        {
            var options = sp.GetRequiredService<IOptions<NullboxOptions>>().Value;
            server.WorkerCount = options.Hangfire.WorkerCount;
            server.ServerName = "nullbox";
        });

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<HeartbeatState>();
        services.AddTransient<HeartbeatJob>();
        services.AddSingleton<IJobScheduler, HangfireJobScheduler>();
        services.AddHostedService<RecurringJobRegistrar>();
        return services;
    }

    /// <summary>Full path of the Hangfire SQLite file; creates the data directory if needed.</summary>
    public static string SqlitePath(NullboxOptions options)
    {
        var dataDir = Path.GetFullPath(options.DataDirectory);
        Directory.CreateDirectory(dataDir);
        return Path.Combine(dataDir, options.Hangfire.SqliteFileName);
    }

    public static IEndpointRouteBuilder MapNullboxDashboard(this IEndpointRouteBuilder endpoints)
    {
        var options = endpoints.ServiceProvider.GetRequiredService<IOptions<NullboxOptions>>().Value;
        endpoints.MapHangfireDashboard(options.Hangfire.DashboardPath, new DashboardOptions
        {
            Authorization = [new AuthenticatedUserDashboardFilter()],
            DisplayStorageConnectionString = false,
            DashboardTitle = "nullbox jobs",
            AppPath = "/",
        }).RequireAuthorization(AdminAuth.PolicyName);
        return endpoints;
    }

    /// <summary>Warns once at startup when login is impossible because no admin password is set.</summary>
    public static void WarnIfAdminPasswordMissing(this WebApplication app)
    {
        if (!app.Services.GetRequiredService<AdminPasswordVerifier>().IsConfigured)
        {
            app.Logger.LogWarning("ADMIN_PASSWORD is not set: the dashboard and UI cannot be signed in to.");
        }
    }

    /// <summary>Says at startup whether mail can be moved to Trash or deleted (the app-wide delete lock).</summary>
    public static void LogDeleteLock(this WebApplication app)
    {
        var gmail = app.Services.GetService<IOptions<GmailOptions>>()?.Value ?? new GmailOptions();
        if (gmail.AllowDelete)
        {
            app.Logger.LogWarning(
                "Delete lock is OFF: rules and jobs may move mail to Trash{Permanent}",
                gmail.AllowPermanentDelete ? " and permanently delete it" : string.Empty);
        }
        else
        {
            app.Logger.LogInformation("Delete lock is on: nothing will be moved to Trash or deleted (Nullbox:Gmail:AllowDelete=false)");
        }

        if (gmail.ApiBaseUri is { Length: > 0 } || gmail.TokenEndpoint is { Length: > 0 })
        {
            app.Logger.LogWarning(
                "Gmail calls are redirected for testing (ApiBaseUri {ApiBaseUri}, TokenEndpoint {TokenEndpoint}); never set these in production",
                gmail.ApiBaseUri ?? "-",
                gmail.TokenEndpoint ?? "-");
        }
    }

    [GeneratedRegex("^[1-9][0-9]{0,3}[dmy]$|^[1-9][0-9]{0,4}h$")]
    private static partial Regex ResyncWindowPattern();
}
