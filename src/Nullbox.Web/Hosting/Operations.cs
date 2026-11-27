using System.Threading.RateLimiting;
using Hangfire;
using Microsoft.Extensions.Options;
using Nullbox.Core.Cleanup;
using Nullbox.Core.Jev;
using Nullbox.Core.Options;
using Nullbox.Core.Pipeline;
using Nullbox.Web.Auth;

namespace Nullbox.Web.Hosting;

/// <summary>Login rate limiting and the admin metrics endpoint (M4-18).</summary>
public static class Operations
{
    /// <summary>Jev's list price per million input tokens (docs/jev-api.md); output tokens are free.</summary>
    public const decimal JevUsdPerMillionInputTokens = 0.042m;

    public static IServiceCollection AddNullboxLoginRateLimit(this IServiceCollection services)
    {
        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.AddPolicy(AdminAuth.LoginRateLimitPolicy, http =>
            {
                var settings = http.RequestServices.GetRequiredService<IOptions<NullboxOptions>>().Value.Login;
                return RateLimitPartition.GetFixedWindowLimiter(
                    http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = Math.Max(1, settings.AttemptsPerWindow),
                        Window = TimeSpan.FromSeconds(Math.Max(1, settings.WindowSeconds)),
                        QueueLimit = 0,
                    });
            });
            limiter.OnRejected = (context, _) =>
            {
                context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Nullbox.Web.Auth")
                    .LogWarning("Too many sign-in attempts from {Address}; refused", context.HttpContext.Connection.RemoteIpAddress);
                return ValueTask.CompletedTask;
            };
        });
        return services;
    }

    /// <summary>
    /// Admin-only <c>GET /api/metrics</c>: decisions by outcome in the last 24 hours, messages changed in the last hour,
    /// Hangfire job counts, Jev usage with its estimated cost today, and recent cleanup runs by status. No message content.
    /// </summary>
    public static IEndpointRouteBuilder MapNullboxMetrics(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/metrics", async (IServiceProvider services, IDecisionLog log, ICleanupRunLog cleanup, TimeProvider time, CancellationToken ct) =>
        {
            var now = time.GetUtcNow();
            var byOutcome = await log.CountByOutcomeSinceAsync(now.AddHours(-24), ct);
            var jev = services.GetService<IJevBudget>() is { } budget ? await budget.GetStatusAsync(ct) : null;
            var stats = services.GetService<JobStorage>()?.GetMonitoringApi().GetStatistics();
            var runs = await cleanup.ListAsync(200, ct);
            return Results.Ok(new
            {
                at = now,
                decisions = new
                {
                    last24Hours = byOutcome.ToDictionary(p => p.Key.ToString(), p => p.Value),
                    messagesChangedLastHour = await log.CountAppliedSinceAsync(now.AddHours(-1), ct),
                },
                jobs = stats is null ? null : new { stats.Enqueued, stats.Processing, stats.Scheduled, stats.Succeeded, stats.Failed, stats.Recurring },
                jev = jev is null ? null : new
                {
                    jev.InputTokensToday,
                    jev.MaxInputTokensPerDay,
                    jev.RequestsLastHour,
                    jev.MaxRequestsPerHour,
                    jev.OverBudget,
                    estimatedCostTodayUsd = decimal.Round(jev.InputTokensToday * JevUsdPerMillionInputTokens / 1_000_000m, 6),
                },
                cleanupRuns = runs.GroupBy(r => r.Status).ToDictionary(g => g.Key.ToString(), g => g.Count()),
            });
        }).RequireAuthorization(AdminAuth.PolicyName);
        return endpoints;
    }
}
