using System.Security.Claims;
using System.Text.Encodings.Web;
using Hangfire.Dashboard;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;

namespace Nullbox.Web.Auth;

/// <summary>Single-admin cookie login. Gates the Hangfire dashboard now and the UI later.</summary>
public static class AdminAuth
{
    public const string PolicyName = "Admin";
    public const string LoginPath = "/login";

    /// <summary>Rate-limiter policy on <c>POST /login</c> (<see cref="Nullbox.Core.Options.LoginSettings"/>).</summary>
    public const string LoginRateLimitPolicy = "login";
    public const string LogoutPath = "/logout";
    private const string AdminName = "admin";

    public static IServiceCollection AddAdminAuth(this IServiceCollection services)
    {
        services.AddSingleton<AdminPasswordVerifier>();
        services.AddAntiforgery();

        services
            .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.LoginPath = LoginPath;
                options.LogoutPath = LogoutPath;
                options.Cookie.Name = "nullbox.auth";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Strict;
                // Plain HTTP on the LAN is supported (no proxy); the cookie is Secure whenever the request is HTTPS.
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.ExpireTimeSpan = TimeSpan.FromHours(8);
                options.SlidingExpiration = true;
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(PolicyName, policy => policy.RequireAuthenticatedUser());

        return services;
    }

    public static IEndpointRouteBuilder MapAdminAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(LoginPath, (HttpContext http, IAntiforgery antiforgery, AdminPasswordVerifier verifier, string? returnUrl) =>
            LoginPage(http, antiforgery, returnUrl, error: verifier.IsConfigured ? null : NotConfiguredMessage, StatusCodes.Status200OK));

        endpoints.MapPost(LoginPath, async (
            HttpContext http,
            IAntiforgery antiforgery,
            AdminPasswordVerifier verifier,
            ILoggerFactory loggers,
            [FromForm] string? password,
            [FromForm] string? returnUrl) =>
        {
            if (!verifier.Verify(password))
            {
                loggers.CreateLogger(typeof(AdminAuth)).LogWarning("Failed admin login from {RemoteIp}", http.Connection.RemoteIpAddress);
                var message = verifier.IsConfigured ? "Wrong password." : NotConfiguredMessage;
                return LoginPage(http, antiforgery, returnUrl, message, StatusCodes.Status401Unauthorized);
            }

            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, AdminName), new Claim(ClaimTypes.Role, PolicyName)],
                CookieAuthenticationDefaults.AuthenticationScheme);
            await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));

            return Results.LocalRedirect(SafeReturnUrl(returnUrl));
        }).RequireRateLimiting(LoginRateLimitPolicy);

        endpoints.MapPost(LogoutPath, async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.LocalRedirect(LoginPath);
        }).RequireAuthorization(PolicyName);

        return endpoints;
    }

    /// <summary>Only same-site relative paths are honoured, so the login can't be used as an open redirect.</summary>
    public static string SafeReturnUrl(string? returnUrl)
    {
        var isLocal = !string.IsNullOrEmpty(returnUrl)
            && returnUrl[0] == '/'
            && (returnUrl.Length == 1 || (returnUrl[1] != '/' && returnUrl[1] != '\\'));
        return isLocal ? returnUrl! : "/hangfire";
    }

    private const string NotConfiguredMessage = "Login is disabled: set ADMIN_PASSWORD.";

    private static IResult LoginPage(HttpContext http, IAntiforgery antiforgery, string? returnUrl, string? error, int statusCode)
    {
        var tokens = antiforgery.GetAndStoreTokens(http);
        var html = HtmlEncoder.Default;
        var errorHtml = error is null ? string.Empty : $"<p class=\"error\" role=\"alert\">{html.Encode(error)}</p>";

        var body = $$"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>nullbox · sign in</title>
              <style>
                body { font-family: system-ui, sans-serif; display: grid; place-items: center; min-height: 100vh; margin: 0; }
                form { display: grid; gap: .75rem; width: min(20rem, 90vw); }
                .error { color: #b00020; margin: 0; }
              </style>
            </head>
            <body>
              <form method="post" action="{{LoginPath}}">
                <h1>nullbox</h1>
                {{errorHtml}}
                <label for="password">Admin password</label>
                <input id="password" name="password" type="password" autocomplete="current-password" required autofocus>
                <input type="hidden" name="{{html.Encode(tokens.FormFieldName)}}" value="{{html.Encode(tokens.RequestToken ?? string.Empty)}}">
                <input type="hidden" name="returnUrl" value="{{html.Encode(SafeReturnUrl(returnUrl))}}">
                <button type="submit">Sign in</button>
              </form>
            </body>
            </html>
            """;

        return Results.Content(body, "text/html; charset=utf-8", statusCode: statusCode);
    }
}

/// <summary>Defence in depth for the dashboard: the endpoint already requires the admin policy.</summary>
public sealed class AuthenticatedUserDashboardFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context)
        => context.GetHttpContext().User.Identity?.IsAuthenticated == true;
}
