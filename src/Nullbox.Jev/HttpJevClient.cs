using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nullbox.Core.Jev;
using Polly;
using Polly.Retry;
using Polly.Timeout;

namespace Nullbox.Jev;

/// <summary>
/// Typed client for <c>POST /v1/systemone</c> (docs/jev-api.md). Retries 429/529 (honouring <c>retry-after</c>),
/// gateway errors, network errors and timeouts with jittered exponential backoff; maps error statuses to typed exceptions.
/// </summary>
public sealed class HttpJevClient : IJevClient
{
    public const string EndpointPath = "v1/systemone";
    private const HttpStatusCode Overloaded = (HttpStatusCode)529;

    private readonly HttpClient _http;
    private readonly JevOptions _options;
    private readonly ILogger<HttpJevClient> _logger;
    private readonly TimeProvider _time;
    private readonly ResiliencePipeline<HttpResponseMessage> _pipeline;

    public HttpJevClient(HttpClient http, IOptions<JevOptions> options, ILogger<HttpJevClient> logger, TimeProvider? time = null)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _pipeline = BuildPipeline();
    }

    /// <summary>Test hook: observes each retry's delay.</summary>
    internal Action<TimeSpan>? RetryObserver { get; set; }

    public async Task<JevResult> EvaluateAsync(JevRequest request, JevCallContext? context = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            throw new JevNotConfiguredException();
        }

        var toSend = JevStateTruncator.Truncate(request, _options.MaxRequestTokens, out var truncated);
        if (truncated)
        {
            _logger.LogInformation("Truncated Jev state for message {MessageId} to fit {MaxTokens} tokens", context?.MessageId, _options.MaxRequestTokens);
        }

        var body = JsonSerializer.SerializeToUtf8Bytes(toSend, JevJson.Options);

        HttpResponseMessage response;
        try
        {
            response = await _pipeline.ExecuteAsync(
                async ct =>
                {
                    using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(_options.BaseUrl, EndpointPath))
                    {
                        Content = new ByteArrayContent(body) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } },
                    };
                    message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
                    message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                    return await _http.SendAsync(message, HttpCompletionOption.ResponseContentRead, ct);
                },
                cancellationToken);
        }
        catch (TimeoutRejectedException ex)
        {
            throw new TimeoutException($"Jev did not answer within {_options.Timeout.TotalSeconds:0.#}s.", ex);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw MapError(response.StatusCode, text);
            }

            var result = JsonSerializer.Deserialize<JevResult>(text, JevJson.Options)
                ?? throw new JevApiException(response.StatusCode, "Jev returned an empty body.", text);
            _logger.LogInformation(
                "Jev answered with {Model}, {InputTokens} input tokens, {Answers} answers",
                result.Model,
                result.Usage.InputTokens,
                result.Answers.Count);
            return result;
        }
    }

    /// <summary>Delay a <c>retry-after</c> header asks for (delta seconds or HTTP date), if any.</summary>
    public static TimeSpan? RetryAfter(HttpResponseMessage response, DateTimeOffset now)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta)
        {
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        }

        if (header?.Date is { } date)
        {
            var wait = date - now;
            return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
        }

        return null;
    }

    public static bool IsRetryableStatus(HttpStatusCode status)
        => status is HttpStatusCode.TooManyRequests or Overloaded
            or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    internal static JevApiException MapError(HttpStatusCode status, string body) => status switch
    {
        HttpStatusCode.BadRequest when body.Contains("max_tokens_exceeded", StringComparison.OrdinalIgnoreCase)
            => new JevMaxTokensExceededException(body),
        HttpStatusCode.Unauthorized => new JevUnauthorizedException(body),
        HttpStatusCode.UnprocessableEntity => new JevValidationException(ValidationDetails(body), body),
        _ => new JevApiException(status, $"Jev returned {(int)status} {status}.", body),
    };

    /// <summary>Flattens a FastAPI-style <c>detail</c> (string or list of {loc, msg}) into readable lines.</summary>
    internal static IReadOnlyList<string> ValidationDetails(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("detail", out var detail))
            {
                return [body];
            }

            if (detail.ValueKind == JsonValueKind.String)
            {
                return [detail.GetString()!];
            }

            if (detail.ValueKind != JsonValueKind.Array)
            {
                return [detail.GetRawText()];
            }

            return [.. detail.EnumerateArray().Select(d =>
            {
                var msg = d.TryGetProperty("msg", out var m) ? m.ToString() : d.GetRawText();
                var loc = d.TryGetProperty("loc", out var l) && l.ValueKind == JsonValueKind.Array
                    ? string.Join('.', l.EnumerateArray().Select(x => x.ToString()))
                    : null;
                return loc is null ? msg : $"{loc}: {msg}";
            })];
        }
        catch (JsonException)
        {
            return [body];
        }
    }

    private ResiliencePipeline<HttpResponseMessage> BuildPipeline()
    {
        var builder = new ResiliencePipelineBuilder<HttpResponseMessage> { TimeProvider = _time };

        if (_options.MaxRetries > 0)
        {
            builder.AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                MaxRetryAttempts = _options.MaxRetries,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = _options.RetryBaseDelay,
                MaxDelay = _options.RetryMaxDelay,
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                    .Handle<HttpRequestException>()
                    .Handle<TimeoutRejectedException>()
                    .HandleResult(r => IsRetryableStatus(r.StatusCode)),
                DelayGenerator = args =>
                {
                    var serverDelay = args.Outcome.Result is { } r ? RetryAfter(r, _time.GetUtcNow()) : null;
                    TimeSpan? delay = serverDelay is { } d ? (d > _options.RetryMaxDelay ? _options.RetryMaxDelay : d) : null;
                    return ValueTask.FromResult(delay);
                },
                OnRetry = args =>
                {
                    _logger.LogWarning(
                        "Jev call failed ({Status}); retry {Attempt} in {Delay}",
                        args.Outcome.Result?.StatusCode.ToString() ?? args.Outcome.Exception?.GetType().Name,
                        args.AttemptNumber + 1,
                        args.RetryDelay);
                    RetryObserver?.Invoke(args.RetryDelay);
                    args.Outcome.Result?.Dispose();
                    return ValueTask.CompletedTask;
                },
            });
        }

        builder.AddTimeout(_options.Timeout);
        return builder.Build();
    }
}
