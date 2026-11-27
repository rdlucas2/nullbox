using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nullbox.Core.Jev;

namespace Nullbox.Jev;

/// <summary>
/// Wraps the HTTP client with a result cache (message id + rule-set version + model) and a cost guard
/// (daily UTC input-token budget, hourly request cap). Over budget it throws <see cref="JevBudgetExceededException"/>
/// before any paid call, so the caller can defer the work.
/// </summary>
public sealed class GuardedJevClient(
    IJevClient inner,
    IJevStore store,
    IOptions<JevOptions> options,
    ILogger<GuardedJevClient> logger,
    TimeProvider? time = null) : IJevClient, IJevBudget
{
    private readonly JevOptions _options = options.Value;
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public async Task<JevResult> EvaluateAsync(JevRequest request, JevCallContext? context = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var key = _options.CacheEnabled && context is not null ? CacheKey(context, request.Model) : null;

        if (key is not null && await store.GetCachedAsync(key, cancellationToken) is { } cached)
        {
            var hit = JsonSerializer.Deserialize<JevResult>(cached, JevJson.Options);
            if (hit is not null)
            {
                logger.LogDebug("Jev cache hit for message {MessageId}", context!.MessageId);
                return hit with { FromCache = true };
            }
        }

        var now = _time.GetUtcNow();
        var status = await GetStatusAsync(now, cancellationToken);
        if (status.Tokens.InputTokens >= _options.MaxInputTokensPerDay)
        {
            throw new JevBudgetExceededException(
                $"Daily Jev budget of {_options.MaxInputTokensPerDay:N0} input tokens is used up",
                NextUtcMidnight(now));
        }

        if (status.Hour.Requests >= _options.MaxRequestsPerHour)
        {
            throw new JevBudgetExceededException(
                $"Hourly cap of {_options.MaxRequestsPerHour:N0} Jev requests reached",
                (status.Hour.Oldest ?? now) + TimeSpan.FromHours(1));
        }

        var result = await inner.EvaluateAsync(request, context, cancellationToken);

        await store.RecordUsageAsync(_time.GetUtcNow(), result.Usage.InputTokens, result.Model, cancellationToken);
        if (key is not null)
        {
            await store.PutCachedAsync(key, result.Model, JsonSerializer.Serialize(result, JevJson.Options), _time.GetUtcNow(), cancellationToken);
        }

        return result;
    }

    public async Task<JevBudgetStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var (tokens, hour) = await GetStatusAsync(_time.GetUtcNow(), cancellationToken);
        return new JevBudgetStatus(tokens.InputTokens, _options.MaxInputTokensPerDay, hour.Requests, _options.MaxRequestsPerHour);
    }

    /// <summary>Cache key: SHA-256 of message id, rule-set version and requested model.</summary>
    public static string CacheKey(JevCallContext context, string model)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{context.MessageId}\n{context.RuleSetVersion}\n{model}"));
        return Convert.ToHexStringLower(bytes);
    }

    private static DateTimeOffset NextUtcMidnight(DateTimeOffset now)
        => new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero).AddDays(1);

    private async Task<(JevUsageWindow Tokens, JevUsageWindow Hour)> GetStatusAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var day = await store.GetUsageSinceAsync(new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero), cancellationToken);
        var hour = await store.GetUsageSinceAsync(now - TimeSpan.FromHours(1), cancellationToken);
        return (day, hour);
    }
}
