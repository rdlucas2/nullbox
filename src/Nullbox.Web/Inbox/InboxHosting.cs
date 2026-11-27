using Hangfire;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Nullbox.Core.Gmail;
using Nullbox.Core.Inbox;
using Nullbox.Core.Options;
using Nullbox.Core.Pipeline;
using Nullbox.Web.Auth;
using Nullbox.Web.Pipeline;

namespace Nullbox.Web.Inbox;

public static class InboxHosting
{
    public const string DatabaseFileName = "nullbox.db";

    /// <summary>
    /// Inbox poll and message processing: state in <c>{data}/nullbox.db</c>, a second Hangfire server for the
    /// inbox queue with <see cref="JobSettings.MessageWorkers"/> workers. <see cref="IGmailService"/> is optional;
    /// without it the poll records "Gmail not configured" and does nothing.
    /// </summary>
    public static IServiceCollection AddNullboxInbox(this IServiceCollection services)
    {
        services.TryAddSingleton<IInboxStateStore>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<NullboxOptions>>().Value;
            return new SqliteInboxStateStore(Path.Combine(Path.GetFullPath(options.DataDirectory), DatabaseFileName));
        });
        services.TryAddScoped<IMessageProcessor, MessagePipeline>();
        services.TryAddScoped<DecisionReplayer>();
        services.AddTransient<AuditPurgeJob>();
        services.TryAddSingleton<IMessageJobQueue, HangfireMessageJobQueue>();
        services.AddTransient(sp => new InboxPoller(
            sp.GetService<IGmailService>(),
            sp.GetRequiredService<IInboxStateStore>(),
            sp.GetRequiredService<IMessageJobQueue>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<IOptions<NullboxOptions>>().Value.Jobs.InboxResyncWindow,
            sp.GetRequiredService<ILogger<InboxPoller>>()));
        services.AddTransient<InboxPollJob>();
        services.AddTransient<ProcessMessageJob>();

        services.AddHangfireServer((sp, server) =>
        {
            var options = sp.GetRequiredService<IOptions<NullboxOptions>>().Value;
            server.ServerName = "nullbox-inbox";
            server.Queues = [ProcessMessageJob.QueueName];
            server.WorkerCount = options.Jobs.MessageWorkers;
        });
        return services;
    }

    /// <summary>Admin-only status (GET) and pause/resume (POST with an antiforgery token) for the inbox poll.</summary>
    public static IEndpointRouteBuilder MapInboxEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/inbox").RequireAuthorization(AdminAuth.PolicyName);

        group.MapGet("/status", async (IInboxStateStore store, CancellationToken ct) => Results.Ok(await store.GetAsync(ct)));

        group.MapPost("/pause", (HttpContext http, IAntiforgery antiforgery, IInboxStateStore store) => SetPausedAsync(http, antiforgery, store, true));
        group.MapPost("/resume", (HttpContext http, IAntiforgery antiforgery, IInboxStateStore store) => SetPausedAsync(http, antiforgery, store, false));

        return endpoints;
    }

    private static async Task<IResult> SetPausedAsync(HttpContext http, IAntiforgery antiforgery, IInboxStateStore store, bool paused)
    {
        // These endpoints bind no form, so the antiforgery middleware would not reject a missing token by itself.
        if (!await antiforgery.IsRequestValidAsync(http))
        {
            return Results.BadRequest("Missing or invalid antiforgery token.");
        }

        var ct = http.RequestAborted;
        await store.SetPausedAsync(paused, ct);
        return Results.Ok(await store.GetAsync(ct));
    }
}
