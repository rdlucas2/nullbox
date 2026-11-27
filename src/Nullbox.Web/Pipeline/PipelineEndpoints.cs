using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.Options;
using Nullbox.Core.Gmail;
using Nullbox.Core.Options;
using Nullbox.Core.Pipeline;
using Nullbox.Core.Rules;
using Nullbox.Web.Auth;

namespace Nullbox.Web.Pipeline;

/// <summary>Pipeline state for admins, and the kill switch.</summary>
public sealed record PipelineStatus(bool KillSwitch, bool KillSwitchFromConfig, int MaxMessagesPerHour, int ChangedLastHour);

public static class PipelineEndpoints
{
    /// <summary>
    /// Admin-only: <c>GET /pipeline/status</c>, <c>GET /pipeline/decisions</c> (filters and paging, see
    /// <see cref="DecisionQuery"/>), <c>GET /pipeline/poison</c>, and, with an antiforgery token,
    /// <c>POST /pipeline/kill-switch/on|off</c> and <c>POST /pipeline/replay</c>.
    /// </summary>
    public static IEndpointRouteBuilder MapPipelineEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/pipeline").RequireAuthorization(AdminAuth.PolicyName);

        group.MapGet("/status", async (IPipelineSettingsStore settings, IDecisionLog log, IOptions<NullboxOptions> options, TimeProvider time, CancellationToken ct)
            => Results.Ok(await StatusAsync(settings, log, options.Value.Pipeline, time, ct)));

        group.MapGet("/decisions", DecisionsAsync);

        group.MapGet("/poison", async (int? limit, IDecisionLog log, CancellationToken ct)
            => Results.Ok(await log.ListPoisonAsync(limit ?? 50, ct)));

        group.MapPost("/kill-switch/on", (HttpContext http, IAntiforgery antiforgery) => SetKillSwitchAsync(http, antiforgery, true));
        group.MapPost("/kill-switch/off", (HttpContext http, IAntiforgery antiforgery) => SetKillSwitchAsync(http, antiforgery, false));
        group.MapPost("/replay", ReplayAsync);

        return endpoints;
    }

    public static async Task<PipelineStatus> StatusAsync(
        IPipelineSettingsStore settings, IDecisionLog log, PipelineSettings limits, TimeProvider time, CancellationToken cancellationToken)
        => new(
            limits.KillSwitch || await settings.GetKillSwitchAsync(cancellationToken),
            limits.KillSwitch,
            limits.MaxMessagesPerHour,
            await log.CountAppliedSinceAsync(time.GetUtcNow().AddHours(-1), cancellationToken));

    /// <summary>
    /// <c>?messageId=&amp;ruleSet=&amp;version=&amp;action=&amp;mode=&amp;outcome=&amp;minConfidence=&amp;maxConfidence=&amp;before=&amp;limit=</c>.
    /// Mode and outcome take their JSON names (<c>dry_run</c>, <c>cap_reached</c>). Returns <c>{ items, next }</c>; pass
    /// <c>next</c> as <c>before</c> for the following page.
    /// </summary>
    private static async Task<IResult> DecisionsAsync(
        IDecisionLog log,
        CancellationToken ct,
        string? messageId = null,
        string? ruleSet = null,
        int? version = null,
        string? action = null,
        string? mode = null,
        string? outcome = null,
        double? minConfidence = null,
        double? maxConfidence = null,
        long? before = null,
        int? limit = null)
    {
        RuleSetMode? parsedMode = null;
        if (!string.IsNullOrWhiteSpace(mode))
        {
            if (ParseName<RuleSetMode>(mode) is not { } m)
            {
                return Results.BadRequest($"Unknown mode '{mode}'.");
            }

            parsedMode = m;
        }

        DecisionOutcome? parsedOutcome = null;
        if (!string.IsNullOrWhiteSpace(outcome))
        {
            if (ParseName<DecisionOutcome>(outcome) is not { } o)
            {
                return Results.BadRequest($"Unknown outcome '{outcome}'.");
            }

            parsedOutcome = o;
        }

        return Results.Ok(await log.QueryAsync(
            new DecisionQuery
            {
                MessageId = messageId,
                RuleSet = ruleSet,
                Version = version,
                Action = action,
                Mode = parsedMode,
                Outcome = parsedOutcome,
                MinConfidence = minConfidence,
                MaxConfidence = maxConfidence,
                Before = before,
                Limit = limit ?? 50,
            },
            ct));
    }

    /// <summary>
    /// Body: <c>{ "rule_set": { draft rule set }, "sample": { "rule_set", "version", "message_ids", "limit" } }</c>
    /// (sample optional). Replays the draft over stored records and returns the diff; Gmail is not touched.
    /// </summary>
    private static async Task<IResult> ReplayAsync(HttpContext http, IAntiforgery antiforgery, DecisionReplayer replayer, IOptions<GmailOptions> gmail)
    {
        // A JSON body binds no form, so the antiforgery middleware would not check the token by itself.
        if (!await antiforgery.IsRequestValidAsync(http))
        {
            return Results.BadRequest("Missing or invalid antiforgery token.");
        }

        JsonDocument body;
        try
        {
            body = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted);
        }
        catch (JsonException ex)
        {
            return Results.BadRequest($"The body is not JSON: {ex.Message}");
        }

        using (body)
        {
            if (body.RootElement.ValueKind != JsonValueKind.Object
                || !body.RootElement.TryGetProperty("rule_set", out var draftJson)
                || draftJson.ValueKind != JsonValueKind.Object)
            {
                return Results.BadRequest("Expected { \"rule_set\": { ... }, \"sample\": { ... } }.");
            }

            var draft = RuleJson.Parse(draftJson.GetRawText(), out var errors);
            if (draft is null)
            {
                return Results.BadRequest(new { errors = errors.Select(e => e.ToString()) });
            }

            ReplaySample sample;
            try
            {
                sample = body.RootElement.TryGetProperty("sample", out var sampleJson) && sampleJson.ValueKind == JsonValueKind.Object
                    ? sampleJson.Deserialize<ReplaySample>(RuleJson.Options) ?? new ReplaySample()
                    : new ReplaySample();
            }
            catch (JsonException ex)
            {
                return Results.BadRequest($"Invalid sample: {ex.Message}");
            }

            var result = await replayer.ReplayAsync(draft, sample, gmail.Value.AllowDelete, http.RequestAborted);
            return Results.Ok(result);
        }
    }

    /// <summary>Parses an enum from its JSON name (<c>dry_run</c>) or its C# name (<c>DryRun</c>), ignoring case.</summary>
    private static T? ParseName<T>(string value)
        where T : struct, Enum
    {
        T parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value.Trim().ToLowerInvariant()));
        }
        catch (JsonException)
        {
            if (!Enum.TryParse(value.Trim(), ignoreCase: true, out parsed))
            {
                return null;
            }
        }

        return Enum.IsDefined(parsed) ? parsed : null;
    }

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
