using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.Options;
using Nullbox.Core.Options;
using Nullbox.Core.Pipeline;
using Nullbox.Web.Auth;

namespace Nullbox.Web.Pipeline;

/// <summary>Pipeline state for admins, and the kill switch.</summary>
public sealed record PipelineStatus(bool KillSwitch, bool KillSwitchFromConfig, int MaxMessagesPerHour, int ChangedLastHour);

public static class PipelineEndpoints
{
    /// <summary>
    /// Admin-only: <c>GET /pipeline/status</c>, <c>GET /pipeline/decisions?messageId=&amp;limit=</c>,
    /// <c>GET /pipeline/poison</c>, and <c>POST /pipeline/kill-switch/on|off</c> (with an antiforgery token).
    /// </summary>
    public static IEndpointRouteBuilder MapPipelineEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/pipeline").RequireAuthorization(AdminAuth.PolicyName);

        group.MapGet("/status", async (IPipelineSettingsStore settings, IDecisionLog log, IOptions<NullboxOptions> options, TimeProvider time, CancellationToken ct)
            => Results.Ok(await StatusAsync(settings, log, options.Value.Pipeline, time, ct)));

        group.MapGet("/decisions", async (string? messageId, int? limit, IDecisionLog log, CancellationToken ct)
            => Results.Ok(await log.ListAsync(string.IsNullOrWhiteSpace(messageId) ? null : messageId, limit ?? 50, ct)));

        group.MapGet("/poison", async (int? limit, IDecisionLog log, CancellationToken ct)
            => Results.Ok(await log.ListPoisonAsync(limit ?? 50, ct)));

        group.MapPost("/kill-switch/on", (HttpContext http, IAntiforgery antiforgery) => SetKillSwitchAsync(http, antiforgery, true));
        group.MapPost("/kill-switch/off", (HttpContext http, IAntiforgery antiforgery) => SetKillSwitchAsync(http, antiforgery, false));

        return endpoints;
    }

    public static async Task<PipelineStatus> StatusAsync(
        IPipelineSettingsStore settings, IDecisionLog log, PipelineSettings limits, TimeProvider time, CancellationToken cancellationToken)
        => new(
            limits.KillSwitch || await settings.GetKillSwitchAsync(cancellationToken),
            limits.KillSwitch,
            limits.MaxMessagesPerHour,
            await log.CountAppliedSinceAsync(time.GetUtcNow().AddHours(-1), cancellationToken));

    private static async Task<IResult> SetKillSwitchAsync(HttpContext http, IAntiforgery antiforgery, bool on)
    {
        // These endpoints bind no form, so the antiforgery middleware would not reject a missing token by itself.
        if (!await antiforgery.IsRequestValidAsync(http))
        {
            return Results.BadRequest("Missing or invalid antiforgery token.");
        }

        var services = http.RequestServices;
        var settings = services.GetRequiredService<IPipelineSettingsStore>();
        await settings.SetKillSwitchAsync(on, http.RequestAborted);
        return Results.Ok(await StatusAsync(
            settings,
            services.GetRequiredService<IDecisionLog>(),
            services.GetRequiredService<IOptions<NullboxOptions>>().Value.Pipeline,
            services.GetRequiredService<TimeProvider>(),
            http.RequestAborted));
    }
}
