namespace Nullbox.Core;

/// <summary>Basic identity of the application, shared by every layer.</summary>
public static class AppInfo
{
    public const string Name = "nullbox";

    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetName().Version?.ToString() ?? "0.0.0";
}
