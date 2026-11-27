using System.Net;

namespace Nullbox.Core.Jev;

/// <summary>Jev answered with an error status.</summary>
public class JevApiException(HttpStatusCode statusCode, string message, string? responseBody = null)
    : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;

    /// <summary>Raw error body from Jev (never contains our API key).</summary>
    public string? ResponseBody { get; } = responseBody;
}

/// <summary>400 <c>max_tokens_exceeded</c>: the state is too big even after truncation.</summary>
public sealed class JevMaxTokensExceededException(string? responseBody)
    : JevApiException(HttpStatusCode.BadRequest, "Jev rejected the request: max_tokens_exceeded.", responseBody);

/// <summary>401: the API key is missing or wrong.</summary>
public sealed class JevUnauthorizedException(string? responseBody)
    : JevApiException(HttpStatusCode.Unauthorized, "Jev rejected the API key (401). Check JEV_API_KEY.", responseBody);

/// <summary>422: the request failed Jev's validation; <see cref="Details"/> carries its <c>detail[]</c> messages.</summary>
public sealed class JevValidationException(IReadOnlyList<string> details, string? responseBody)
    : JevApiException((HttpStatusCode)422, "Jev rejected the request (422): " + string.Join("; ", details), responseBody)
{
    public IReadOnlyList<string> Details { get; } = details;
}

/// <summary>The configured daily token or hourly request budget is used up. Jobs should defer until <see cref="RetryAfter"/>.</summary>
public sealed class JevBudgetExceededException(string reason, DateTimeOffset retryAfter)
    : Exception($"Jev budget exceeded: {reason}. Deferring until {retryAfter:u}.")
{
    public string Reason { get; } = reason;

    public DateTimeOffset RetryAfter { get; } = retryAfter;
}
