using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.Options;
using Nullbox.Core.Cleanup;
using Nullbox.Core.Options;
using Nullbox.Gmail.Cleanup;
using Nullbox.Web.Auth;

namespace Nullbox.Web.Cleanup;

/// <summary>A cleanup file as the admin API shows it: each rule with the exact Gmail search it runs.</summary>
public sealed record CleanupConfigView(string Id, string? Label, IReadOnlyList<CleanupRuleView> Rules, string? Error);

public sealed record CleanupRuleView(string Name, string Actions, string Query);

public static class CleanupHosting
{
    /// <summary>Cleanup rule files under <c>{data}/cleanup/</c>, the runner, and the run job (M1-06).</summary>
    public static IServiceCollection AddNullboxCleanup(this IServiceCollection services)
    {
        services.AddSingleton<ICleanupConfigStore>(sp => new FileCleanupConfigStore(Path.Combine(
            Path.GetFullPath(sp.GetRequiredService<IOptions<NullboxOptions>>().Value.DataDirectory),
            FileCleanupConfigStore.FolderName)));
        services.AddTransient<CleanupRunner>();
        services.AddTransient<RunCleanupJob>();
        services.AddScoped<CleanupLauncher>();
        return services;
    }

    /// <summary>
    /// Admin-only: <c>GET /cleanup/configs</c>, <c>GET /cleanup/runs</c>, <c>GET /cleanup/runs/{id}</c>, and
    /// <c>POST /cleanup/configs/{id}/run?dryRun=true&amp;sample=0</c> (with an antiforgery token). Runs are dry runs
    /// unless <c>dryRun=false</c> is passed.
    /// </summary>
    public static IEndpointRouteBuilder MapCleanupEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/cleanup").RequireAuthorization(AdminAuth.PolicyName);

        group.MapGet("/configs", (ICleanupConfigStore configs) => Results.Ok(configs.List().Select(View)));

        group.MapPost("/configs/{id}/run", async (string id, bool? dryRun, int? sample, HttpContext http, IAntiforgery antiforgery, ICleanupConfigStore configs, CleanupLauncher launcher) =>
        {
            // This endpoint binds no form, so the antiforgery middleware would not reject a missing token by itself.
            if (!await antiforgery.IsRequestValidAsync(http))
            {
                return Results.BadRequest("Missing or invalid antiforgery token.");
            }

            var entry = configs.Get(id);
            if (entry is null)
            {
                return Results.NotFound($"There is no cleanup file named {id}.json.");
            }

            if (entry.Error is { } error)
            {
                return Results.BadRequest(error);
            }

            var run = await launcher.StartAsync(id, dryRun ?? true, sample ?? 0, http.RequestAborted);
            return Results.Accepted($"/cleanup/runs/{run.Id}", run);
        });

        group.MapGet("/runs", async (int? limit, ICleanupRunLog runs, CancellationToken ct) => Results.Ok(await runs.ListAsync(limit ?? 20, ct)));

        group.MapGet("/runs/{id:long}", async (long id, ICleanupRunLog runs, CancellationToken ct)
            => await runs.GetAsync(id, ct) is { } run ? Results.Ok(run) : Results.NotFound());

        return endpoints;
    }

    private static CleanupConfigView View(CleanupConfigEntry entry) => entry.Config is { } config
        ? new CleanupConfigView(entry.Id, config.Label, [.. config.Rules.Select(r => new CleanupRuleView(r.Name, r.Describe(), CleanupQuery.Build(r, config.Keep)))], null)
        : new CleanupConfigView(entry.Id, null, [], entry.Error);
}
