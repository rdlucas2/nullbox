using MudBlazor.Services;
using Nullbox.Web.Auth;
using Nullbox.Web.Components;

namespace Nullbox.Web.Ui;

public static class UiHosting
{
    /// <summary>Blazor (interactive server) with MudBlazor, plus the dashboard's status reader.</summary>
    public static IServiceCollection AddNullboxUi(this IServiceCollection services)
    {
        services.AddRazorComponents().AddInteractiveServerComponents();
        services.AddMudServices();
        services.AddCascadingAuthenticationState();
        services.AddScoped<DashboardStatusProvider>();
        return services;
    }

    /// <summary>Every UI page sits behind the admin login; static assets (CSS, JS) do not.</summary>
    public static IEndpointRouteBuilder MapNullboxUi(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapStaticAssets();
        endpoints.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode()
            .RequireAuthorization(AdminAuth.PolicyName);
        return endpoints;
    }
}
