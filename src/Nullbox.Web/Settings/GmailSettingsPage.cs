using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Nullbox.Core.Gmail;
using Nullbox.Web.Auth;

namespace Nullbox.Web.Settings;

/// <summary>
/// Placeholder Gmail settings page (the real UI comes in M3): token state, scopes, expiry, last refresh, and an
/// upload form that validates a token.json with <c>users.getProfile</c> before saving it.
/// </summary>
public static class GmailSettingsPage
{
    public const string Path = "/settings/gmail";
    public const int MaxTokenBytes = 64 * 1024;

    public static IEndpointRouteBuilder MapGmailSettings(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(Path, (HttpContext http, IAntiforgery antiforgery, IGmailCredentialStore store, string? saved, string? kind) =>
        {
            var notice = saved is null ? null : $"Saved the {kind ?? "Gmail"} token for {saved}.";
            return Page(http, antiforgery, store, notice, error: null, StatusCodes.Status200OK);
        }).RequireAuthorization(AdminAuth.PolicyName);

        endpoints.MapPost(Path, async (
            HttpContext http,
            IAntiforgery antiforgery,
            IGmailCredentialStore store,
            [FromForm] string? kind,
            [FromForm] string? tokenJson,
            IFormFile? tokenFile,
            CancellationToken cancellationToken) =>
        {
            var tokenKind = string.Equals(kind, "filters", StringComparison.OrdinalIgnoreCase) ? GmailTokenKind.Filters : GmailTokenKind.Primary;
            string? json = tokenJson;
            if (tokenFile is { Length: > 0 })
            {
                if (tokenFile.Length > MaxTokenBytes)
                {
                    return Page(http, antiforgery, store, null, "That file is too large to be a token.json.", StatusCodes.Status400BadRequest);
                }

                using var reader = new StreamReader(tokenFile.OpenReadStream(), Encoding.UTF8);
                json = await reader.ReadToEndAsync(cancellationToken);
            }

            if (string.IsNullOrWhiteSpace(json))
            {
                return Page(http, antiforgery, store, null, "Choose a token.json file or paste its contents.", StatusCodes.Status400BadRequest);
            }

            if (Encoding.UTF8.GetByteCount(json) > MaxTokenBytes)
            {
                return Page(http, antiforgery, store, null, "That is too large to be a token.json.", StatusCodes.Status400BadRequest);
            }

            try
            {
                var email = await store.ReplaceTokenAsync(tokenKind, json, cancellationToken);
                var label = tokenKind == GmailTokenKind.Filters ? "filters" : "mail";
                return Results.Redirect($"{Path}?saved={Uri.EscapeDataString(email)}&kind={label}");
            }
            catch (GmailCredentialFormatException ex)
            {
                return Page(http, antiforgery, store, null, ex.Message, StatusCodes.Status400BadRequest);
            }
        }).RequireAuthorization(AdminAuth.PolicyName);

        return endpoints;
    }

    private static IResult Page(HttpContext http, IAntiforgery antiforgery, IGmailCredentialStore store, string? notice, string? error, int statusCode)
    {
        var tokens = antiforgery.GetAndStoreTokens(http);
        var html = HtmlEncoder.Default;
        var csrf = $"<input type=\"hidden\" name=\"{html.Encode(tokens.FormFieldName)}\" value=\"{html.Encode(tokens.RequestToken ?? string.Empty)}\">";

        var sections = new StringBuilder();
        foreach (var kind in new[] { GmailTokenKind.Primary, GmailTokenKind.Filters })
        {
            var status = store.GetStatus(kind);
            var title = kind == GmailTokenKind.Primary ? "Mail (token.json)" : "Filters (token_filters.json, optional)";
            var scopes = status.Scopes.Count == 0 ? "none" : string.Join("<br>", status.Scopes.Select(s => html.Encode(s)));
            sections.Append(CultureInfo.InvariantCulture, $$"""
                <section>
                  <h2>{{html.Encode(title)}}</h2>
                  <dl>
                    <dt>State</dt><dd class="state-{{status.State}}">{{html.Encode(Describe(status.State))}}</dd>
                    <dt>Scopes</dt><dd>{{scopes}}</dd>
                    <dt>Access token expires</dt><dd>{{Format(status.AccessTokenExpiry)}}</dd>
                    <dt>Last refresh or save</dt><dd>{{Format(status.LastRefresh)}}</dd>
                  </dl>
                  {{(status.Message is null ? string.Empty : $"<p class=\"message\">{html.Encode(status.Message)}</p>")}}
                  <form method="post" action="{{Path}}" enctype="multipart/form-data">
                    {{csrf}}
                    <input type="hidden" name="kind" value="{{(kind == GmailTokenKind.Filters ? "filters" : "primary")}}">
                    <label>Upload or replace <input type="file" name="tokenFile" accept=".json,application/json"></label>
                    <label>or paste it <textarea name="tokenJson" rows="4" spellcheck="false" autocomplete="off"></textarea></label>
                    <button type="submit">Test and save</button>
                  </form>
                </section>
                """);
        }

        var body = $$"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>nullbox · Gmail</title>
              <style>
                body { font-family: system-ui, sans-serif; max-width: 44rem; margin: 2rem auto; padding: 0 1rem; }
                dl { display: grid; grid-template-columns: max-content 1fr; gap: .25rem 1rem; }
                dt { font-weight: 600; }
                form { display: grid; gap: .5rem; margin-top: .75rem; }
                textarea { width: 100%; font-family: ui-monospace, monospace; }
                .error { color: #b00020; } .notice { color: #1b5e20; }
                .state-Ready { color: #1b5e20; } .state-ReauthorizationRequired, .state-Invalid { color: #b00020; }
              </style>
            </head>
            <body>
              <h1>Gmail connection</h1>
              {{(notice is null ? string.Empty : $"<p class=\"notice\" role=\"status\">{html.Encode(notice)}</p>")}}
              {{(error is null ? string.Empty : $"<p class=\"error\" role=\"alert\">{html.Encode(error)}</p>")}}
              <p>Copy <code>credentials.json</code> and <code>token.json</code> from emailBlaster into <code>data/google/</code>, or upload a token here. See docs/gmail-auth.md.</p>
              {{sections}}
            </body>
            </html>
            """;

        return Results.Content(body, "text/html; charset=utf-8", statusCode: statusCode);
    }

    private static string Describe(GmailAuthState state) => state switch
    {
        GmailAuthState.Ready => "Connected",
        GmailAuthState.NotConfigured => "Not connected",
        GmailAuthState.ReauthorizationRequired => "Needs re-authorization",
        _ => "Invalid file",
    };

    private static string Format(DateTimeOffset? at)
        => at is { } value ? value.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture) : "unknown";
}
