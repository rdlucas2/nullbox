using System.Net;
using System.Text;
using System.Text.Json;
using Google.Apis.Http;

namespace Nullbox.Tests.Gmail.Auth;

/// <summary>Fake Google token endpoint and Gmail profile endpoint. Records every request; no network.</summary>
public sealed class FakeGoogle : HttpMessageHandler
{
    public List<(Uri Uri, string? Body, string? Authorization)> Requests { get; } = [];

    /// <summary>Response for POST oauth2.googleapis.com/token. Default: a fresh access token, no new refresh token.</summary>
    public Func<string, HttpResponseMessage> Token { get; set; } = _ => Json(new
    {
        access_token = "new-access-token",
        expires_in = 3599,
        scope = "https://www.googleapis.com/auth/gmail.modify",
        token_type = "Bearer",
    });

    public Func<HttpResponseMessage> Profile { get; set; } = () => Json(new { emailAddress = "ryan@example.com", messagesTotal = 10 });

    public IEnumerable<string?> TokenRequests => Requests.Where(r => r.Uri.AbsolutePath == "/token").Select(r => r.Body);

    public IEnumerable<string?> ProfileAuthorizations => Requests.Where(r => r.Uri.AbsolutePath.EndsWith("/profile", StringComparison.Ordinal)).Select(r => r.Authorization);

    public Google.Apis.Http.IHttpClientFactory Factory => new HandlerFactory(this);

    public static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
    };

    public static HttpResponseMessage TokenError(string error) => Json(new { error, error_description = "fake" }, HttpStatusCode.BadRequest);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request.RequestUri!, body, request.Headers.Authorization?.ToString()));
        return request.RequestUri!.AbsolutePath switch
        {
            "/token" => Token(body ?? string.Empty),
            var p when p.EndsWith("/users/me/profile", StringComparison.Ordinal) => Profile(),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
    }

    private sealed class HandlerFactory(HttpMessageHandler handler) : HttpClientFactory
    {
        protected override HttpMessageHandler CreateHandler(CreateHttpClientArgs args) => handler;
    }
}

internal static class TokenSamples
{
    public const string ClientId = "123-abc.apps.googleusercontent.com";

    /// <summary>The shape Python google-auth writes with creds.to_json() (emailBlaster's token.json).</summary>
    public static string EmailBlasterToken(DateTimeOffset expiry, bool withClient = true) => $$"""
        {"token": "old-access-token", "refresh_token": "the-refresh-token", "token_uri": "https://oauth2.googleapis.com/token",
         {{(withClient ? $"\"client_id\": \"{ClientId}\", \"client_secret\": \"shh\"," : string.Empty)}}
         "scopes": ["https://www.googleapis.com/auth/gmail.modify"], "universe_domain": "googleapis.com", "account": "",
         "expiry": "{{expiry.UtcDateTime:yyyy-MM-ddTHH:mm:ss.ffffff}}Z"}
        """;

    public const string ClientFile = $$$"""
        {"installed": {"client_id": "{{{ClientId}}}", "project_id": "p", "auth_uri": "https://accounts.google.com/o/oauth2/auth",
         "token_uri": "https://oauth2.googleapis.com/token", "client_secret": "shh", "redirect_uris": ["http://localhost"]}}
        """;
}
