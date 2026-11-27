using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Nullbox.Core.Gmail;
using Nullbox.Core.Jev;
using Nullbox.Data;
using Nullbox.Jev;
using Nullbox.Web.Auth;

namespace Nullbox.Web.Hosting;

/// <summary>
/// <c>/healthz</c> is liveness (the database answers) and drives the container HEALTHCHECK, so a missing Gmail token
/// never marks the container unhealthy. <c>/healthz/ready</c> (admin) reports every component as JSON.
/// </summary>
public static class NullboxHealth
{
    public const string Live = "live";

    public static IServiceCollection AddNullboxHealthChecks(this IServiceCollection services)
    {
        services.AddHealthChecks()
            .AddCheck<DatabaseHealthCheck>("database", tags: [Live])
            .AddCheck<GmailHealthCheck>("gmail")
            .AddCheck<JevHealthCheck>("jev");
        return services;
    }

    public static IEndpointRouteBuilder MapNullboxHealthChecks(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHealthChecks("/healthz", new HealthCheckOptions { Predicate = c => c.Tags.Contains(Live) });
        endpoints.MapHealthChecks("/healthz/ready", new HealthCheckOptions
        {
            ResponseWriter = WriteJsonAsync,
            ResultStatusCodes =
            {
                [HealthStatus.Healthy] = StatusCodes.Status200OK,
                [HealthStatus.Degraded] = StatusCodes.Status200OK,
                [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable,
            },
        }).RequireAuthorization(AdminAuth.PolicyName);
        return endpoints;
    }

    private static Task WriteJsonAsync(HttpContext http, HealthReport report)
    {
        http.Response.ContentType = "application/json";
        var body = new
        {
            status = report.Status.ToString(),
            checks = report.Entries.ToDictionary(e => e.Key, e => new { status = e.Value.Status.ToString(), description = e.Value.Description }),
        };
        return http.Response.WriteAsync(JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }
}

/// <summary>The SQLite database opens and answers a query.</summary>
public sealed class DatabaseHealthCheck(IServiceScopeFactory scopes) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<NullboxDbContext>();
            await db.AppSettings.AsNoTracking().AnyAsync(cancellationToken);
            return HealthCheckResult.Healthy("answers queries");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy($"database error ({ex.GetType().Name})");
        }
    }
}

/// <summary>The mail token: connected is healthy, missing is degraded, rejected or invalid is unhealthy.</summary>
public sealed class GmailHealthCheck(IServiceProvider services) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var status = services.GetService<IGmailCredentialStore>()?.GetStatus(GmailTokenKind.Primary);
        return Task.FromResult(status?.State switch
        {
            GmailAuthState.Ready => HealthCheckResult.Healthy("connected"),
            GmailAuthState.ReauthorizationRequired => HealthCheckResult.Unhealthy("needs reauthorizing"),
            GmailAuthState.Invalid => HealthCheckResult.Unhealthy("token file is invalid"),
            _ => HealthCheckResult.Degraded("not connected"),
        });
    }
}

/// <summary>Jev: a key and budget left is healthy; no key or over budget is degraded (messages wait).</summary>
public sealed class JevHealthCheck(IServiceProvider services) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(services.GetService<IOptions<JevOptions>>()?.Value.ApiKey))
        {
            return HealthCheckResult.Degraded("no API key");
        }

        return services.GetService<IJevBudget>() is { } budget && (await budget.GetStatusAsync(cancellationToken)).OverBudget
            ? HealthCheckResult.Degraded("over today's budget")
            : HealthCheckResult.Healthy("configured");
    }
}
