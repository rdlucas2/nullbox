using Hangfire;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.Options;
using Nullbox.Core.Filters;
using Nullbox.Core.Gmail;
using Nullbox.Core.Options;
using Nullbox.Gmail;
using Nullbox.Gmail.Auth;
using Nullbox.Gmail.Bulk;
using Nullbox.Gmail.Filters;
using Nullbox.Web.Auth;

namespace Nullbox.Web.Bulk;

/// <summary>A started job: its Hangfire id and the dashboard page that shows its result.</summary>
public sealed record StartedJob(string JobId, string Dashboard);

public sealed record FilterDefinitionsView(bool FileExists, IReadOnlyList<FilterDefinitionView> Filters, string? Error);

public sealed record FilterDefinitionView(string Label, string Query, bool MarkRead, string Summary);

public static class BulkHosting
{
    /// <summary>Bulk jobs and the filter sync over the filters token (<c>token_filters.json</c>).</summary>
    public static IServiceCollection AddNullboxBulk(this IServiceCollection services)
    {
        services.AddSingleton(sp => new FilterDefinitionsFile(Path.Combine(
            Path.GetFullPath(sp.GetRequiredService<IOptions<NullboxOptions>>().Value.DataDirectory),
            FilterDefinitionsFile.FileName)));
        services.AddSingleton<IGmailFilterApi>(sp => new GmailFilterApi(
            sp.GetRequiredService<IGmailClientFactory>().Create(GmailTokenKind.Filters),
            new GmailRetry(sp.GetRequiredService<IOptions<GmailOptions>>().Value)));
        services.AddTransient<GmailFilterSync>();
        services.AddTransient<BulkActions>();
        services.AddTransient<BulkJobs>();
        return services;
    }

    /// <summary>
    /// Admin-only, all with an antiforgery token and all dry runs unless <c>dryRun=false</c>:
    /// <c>POST /bulk/archive?query=in:inbox&amp;label=</c>, <c>POST /bulk/mark-read</c>,
    /// <c>POST /bulk/delete?query=&amp;cap=1500</c>, <c>POST /gmail/filters/sync</c>; plus <c>GET /gmail/filters</c>.
    /// Each POST answers 202 with the Hangfire job, whose page shows the result.
    /// </summary>
    public static IEndpointRouteBuilder MapBulkEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var bulk = endpoints.MapGroup("/bulk").RequireAuthorization(AdminAuth.PolicyName);
        bulk.MapPost("/archive", (string? query, string? label, bool? dryRun, HttpContext http) => StartAsync(
            http,
            jobs => jobs.Enqueue<BulkJobs>(j => j.ArchiveAsync(string.IsNullOrWhiteSpace(query) ? BulkActions.InboxQuery : query, label, dryRun ?? true, CancellationToken.None))));
        bulk.MapPost("/mark-read", (bool? dryRun, HttpContext http) => StartAsync(
            http,
            jobs => jobs.Enqueue<BulkJobs>(j => j.MarkAllReadAsync(dryRun ?? true, CancellationToken.None))));
        bulk.MapPost("/delete", (string? query, int? cap, bool? dryRun, HttpContext http) => string.IsNullOrWhiteSpace(query)
            ? Task.FromResult(Results.BadRequest("A delete needs a query; an empty one would match every message."))
            : StartAsync(http, jobs => jobs.Enqueue<BulkJobs>(j => j.DeleteAsync(query, cap ?? BulkActions.DefaultDeleteCap, dryRun ?? true, CancellationToken.None))));

        var filters = endpoints.MapGroup("/gmail/filters").RequireAuthorization(AdminAuth.PolicyName);
        filters.MapGet(string.Empty, (FilterDefinitionsFile file) => Results.Ok(View(file)));
        filters.MapPost("/sync", (bool? dryRun, HttpContext http) => StartAsync(
            http,
            jobs => jobs.Enqueue<BulkJobs>(j => j.SyncFiltersAsync(dryRun ?? true, CancellationToken.None))));

        return endpoints;
    }

    private static async Task<IResult> StartAsync(HttpContext http, Func<IBackgroundJobClient, string> enqueue)
    {
        // These endpoints bind no form, so the antiforgery middleware would not reject a missing token by itself.
        if (!await http.RequestServices.GetRequiredService<IAntiforgery>().IsRequestValidAsync(http))
        {
            return Results.BadRequest("Missing or invalid antiforgery token.");
        }

        var id = enqueue(http.RequestServices.GetRequiredService<IBackgroundJobClient>());
        var dashboard = http.RequestServices.GetRequiredService<IOptions<NullboxOptions>>().Value.Hangfire.DashboardPath.TrimEnd('/');
        return Results.Accepted(value: new StartedJob(id, $"{dashboard}/jobs/details/{id}"));
    }

    private static FilterDefinitionsView View(FilterDefinitionsFile file)
    {
        if (!file.Exists)
        {
            return new FilterDefinitionsView(false, [], null);
        }

        try
        {
            return new FilterDefinitionsView(true, [.. file.Load().Select(f => new FilterDefinitionView(f.Label, f.Query, f.MarkRead, f.Summary))], null);
        }
        catch (FilterDefinitionsException ex)
        {
            return new FilterDefinitionsView(true, [], ex.Message);
        }
    }
}
