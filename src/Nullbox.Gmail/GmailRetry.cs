using System.Net;
using Google;
using Nullbox.Core.Gmail;

namespace Nullbox.Gmail;

/// <summary>Exponential backoff with full jitter for transient Gmail API failures.</summary>
public sealed class GmailRetry(GmailOptions options, Func<TimeSpan, CancellationToken, Task>? delay = null, Random? random = null)
{
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;
    private readonly Random _random = random ?? Random.Shared;

    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await action(cancellationToken);
            }
            catch (Exception ex) when (attempt < options.MaxRetries && IsTransient(ex, cancellationToken))
            {
                await _delay(DelayFor(attempt), cancellationToken);
            }
        }
    }

    public Task ExecuteAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken)
        => ExecuteAsync<bool>(async ct =>
        {
            await action(ct);
            return true;
        }, cancellationToken);

    /// <summary>Delay before retry number <paramref name="attempt"/> (0-based): random in [0, min(max, base * 2^attempt)].</summary>
    public TimeSpan DelayFor(int attempt)
    {
        var ceiling = Math.Min(options.RetryMaxDelay.TotalMilliseconds, options.RetryBaseDelay.TotalMilliseconds * Math.Pow(2, attempt));
        return TimeSpan.FromMilliseconds(_random.NextDouble() * ceiling);
    }

    public static bool IsTransient(Exception ex, CancellationToken cancellationToken) => ex switch
    {
        GoogleApiException api => IsTransientStatus(api),
        HttpRequestException => true,
        TaskCanceledException => !cancellationToken.IsCancellationRequested, // HTTP timeout, not our cancellation
        _ => false,
    };

    private static bool IsTransientStatus(GoogleApiException api)
    {
        var status = (int)api.HttpStatusCode;
        if (status == 429 || status >= 500)
        {
            return true;
        }

        // Gmail reports per-user quota exhaustion as 403 with a rate-limit reason.
        return api.HttpStatusCode == HttpStatusCode.Forbidden
            && api.Error?.Errors?.Any(e => e.Reason is "rateLimitExceeded" or "userRateLimitExceeded") == true;
    }
}
