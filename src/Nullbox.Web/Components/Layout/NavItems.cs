using MudBlazor;

namespace Nullbox.Web.Components.Layout;

public sealed record NavItem(string Title, string Href, string Icon);

/// <summary>The app's main sections, in nav order.</summary>
public static class NavItems
{
    public static IReadOnlyList<NavItem> All { get; } =
    [
        new("Dashboard", "/", Icons.Material.Filled.Dashboard),
        new("Rule sets", "/rules", Icons.Material.Filled.Rule),
        new("Saved rules", "/saved-rules", Icons.Material.Filled.PlaylistAddCheck),
        new("Test", "/test", Icons.Material.Filled.Science),
        new("Audit", "/audit", Icons.Material.Filled.History),
        new("Cleanup", "/cleanup", Icons.Material.Filled.CleaningServices),
        new("Jobs", "/jobs", Icons.Material.Filled.Schedule),
        new("Settings", "/settings", Icons.Material.Filled.Settings),
    ];
}
