using MudBlazor.Services;
using Nullbox.Core.Rules;
using Nullbox.Web.Auth;
using Nullbox.Web.Components;
using Nullbox.Web.Ui.Audit;
using Nullbox.Web.Ui.Jobs;
using Nullbox.Web.Ui.Rules;

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
        services.AddScoped<RuleSetCatalog>();
        services.AddScoped<DecisionUndo>();
        services.AddScoped<JobControls>();
        services.AddHostedService<StoredPollSchedule>(); // after AddNullboxData's migrations, which start first
        return services;
    }

    /// <summary>
    /// Every UI page sits behind the admin login; static assets (CSS, JS) do not. Also maps
    /// <c>GET /api/rule-sets/{name}/export?version=</c>, which downloads a version's JSON (the active one by default,
    /// else the latest).
    /// </summary>
    public static IEndpointRouteBuilder MapNullboxUi(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/rule-sets/{name}/export", async (string name, int? version, IRuleSetStore store, CancellationToken ct) =>
        {
            var found = await store.GetVersionAsync(name, version, ct)
                ?? (version is null ? (await store.ListVersionsAsync(name, ct)).FirstOrDefault() : null);
            return found is null
                ? Results.NotFound()
                : Results.File(System.Text.Encoding.UTF8.GetBytes(found.Json), "application/json", $"{found.Name}-v{found.Version}.json");
        }).RequireAuthorization(AdminAuth.PolicyName);

        endpoints.MapStaticAssets();
        endpoints.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode()
            .RequireAuthorization(AdminAuth.PolicyName);
        return endpoints;
    }
}
