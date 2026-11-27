using System.Net;
using System.Text;
using System.Text.Json;
using Google.Apis.Gmail.v1;
using Google.Apis.Http;
using Google.Apis.Services;

namespace Nullbox.Tests.Gmail;

/// <summary>
/// In-process stand-in for the Gmail REST API. Routes are matched on "METHOD /path" (after /gmail/v1/users/me)
/// and every request is recorded for assertions. No network, no credentials.
/// </summary>
public sealed class FakeGmailApi : HttpMessageHandler
{
    private readonly List<(string Route, Func<RecordedRequest, HttpResponseMessage> Respond)> _routes = [];

    public List<RecordedRequest> Requests { get; } = [];

    public FakeGmailApi On(string route, Func<RecordedRequest, HttpResponseMessage> respond)
    {
        _routes.Add((route, respond));
        return this;
    }

    public FakeGmailApi OnJson(string route, object body) => On(route, _ => Json(body));

    public IEnumerable<RecordedRequest> To(string route) => Requests.Where(r => r.Route == route);

    public GmailService CreateService() => new(new BaseClientService.Initializer
    {
        ApplicationName = "nullbox-tests",
        HttpClientFactory = new Factory(this),
        DefaultExponentialBackOffPolicy = ExponentialBackOffPolicy.None,
        GZipEnabled = false,
    });

    public static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
    };

    public static HttpResponseMessage Error(HttpStatusCode status, string? reason = null) => Json(new
    {
        error = new
        {
            code = (int)status,
            message = $"fake {(int)status}",
            errors = reason is null ? [] : new[] { new { reason, message = reason, domain = "usageLimits" } },
        },
    }, status);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        const string prefix = "/gmail/v1/users/me";
        var route = $"{request.Method.Method} {(path.StartsWith(prefix, StringComparison.Ordinal) ? path[prefix.Length..] : path)}";
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var recorded = new RecordedRequest(route, request.RequestUri, body);
        Requests.Add(recorded);

        var match = _routes.LastOrDefault(r => r.Route == route);
        if (match.Respond is null)
        {
            // Prefix routes like "POST /messages/*/trash".
            match = _routes.LastOrDefault(r => r.Route.Contains('*', StringComparison.Ordinal) && Matches(r.Route, route));
        }

        return match.Respond?.Invoke(recorded) ?? Error(HttpStatusCode.NotFound);
    }

    private static bool Matches(string pattern, string route)
    {
        var parts = pattern.Split('*');
        return route.StartsWith(parts[0], StringComparison.Ordinal) && route.EndsWith(parts[^1], StringComparison.Ordinal);
    }

    private sealed class Factory(HttpMessageHandler handler) : HttpClientFactory
    {
        protected override HttpMessageHandler CreateHandler(CreateHttpClientArgs args) => handler;
    }
}

public sealed record RecordedRequest(string Route, Uri Uri, string? Body)
{
    public JsonElement Json => JsonDocument.Parse(Body ?? "{}").RootElement;

    public string? Query(string name)
        => System.Web.HttpUtility.ParseQueryString(Uri.Query)[name];

    public IReadOnlyList<string> JsonStrings(string property)
        => Json.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
            ? [.. value.EnumerateArray().Select(v => v.GetString()!)]
            : [];
}
