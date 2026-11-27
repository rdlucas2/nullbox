using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace Nullbox.Web.Hosting;

/// <summary>
/// nullbox runs on plain HTTP on the LAN with no reverse proxy, so it never redirects to HTTPS. If a proxy
/// (Caddy, Tailscale Serve) is added later, turn on <c>Nullbox:ForwardedHeaders:Enabled</c> and list the
/// proxy's address in <c>KnownProxies</c> so the app sees the original scheme and client IP.
/// </summary>
public static class ProxyHosting
{
    public const string SectionName = "Nullbox:ForwardedHeaders";

    public static bool IsEnabled(IConfiguration configuration)
        => configuration.GetValue<bool>($"{SectionName}:Enabled");

    public static IServiceCollection AddNullboxForwardedHeaders(this IServiceCollection services, IConfiguration configuration)
    {
        if (!IsEnabled(configuration))
        {
            return services;
        }

        var proxies = configuration.GetSection($"{SectionName}:KnownProxies").Get<string[]>() ?? [];
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
            options.ForwardLimit = 1;
            foreach (var proxy in proxies)
            {
                if (!IPAddress.TryParse(proxy, out var address))
                {
                    throw new InvalidOperationException($"{SectionName}:KnownProxies has '{proxy}', which is not an IP address.");
                }

                options.KnownProxies.Add(address);
            }
        });
        return services;
    }

    /// <summary>Applies forwarded headers when enabled; must run before authentication.</summary>
    public static WebApplication UseNullboxForwardedHeaders(this WebApplication app)
    {
        if (IsEnabled(app.Configuration))
        {
            app.UseForwardedHeaders();
        }

        return app;
    }
}
