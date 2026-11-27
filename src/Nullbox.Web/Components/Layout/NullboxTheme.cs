using MudBlazor;

namespace Nullbox.Web.Components.Layout;

public static class NullboxTheme
{
    public static MudTheme Theme { get; } = new()
    {
        PaletteLight = new PaletteLight { Primary = "#3f51b5", AppbarBackground = "#3f51b5" },
        PaletteDark = new PaletteDark { Primary = "#8c9eff", AppbarBackground = "#1e1e2d" },
    };
}
