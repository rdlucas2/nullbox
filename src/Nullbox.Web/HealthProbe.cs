namespace Nullbox.Web;

/// <summary>
/// Container HEALTHCHECK helper: <c>dotnet Nullbox.Web.dll --healthcheck [url]</c> calls the app's own
/// <c>/healthz</c> and exits 0 when healthy, 1 otherwise. The runtime image ships no curl/wget, so the
/// app probes itself instead.
/// </summary>
public static class HealthProbe
{
    public const string Flag = "--healthcheck";

    private const int DefaultPort = 8080;

    /// <summary>Builds the probe URL from <c>ASPNETCORE_HTTP_PORTS</c> (first port wins), defaulting to 8080.</summary>
    public static Uri DefaultUri(string? httpPorts)
    {
        var first = httpPorts?
            .Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

        var port = int.TryParse(first, out var parsed) && parsed is > 0 and <= 65535 ? parsed : DefaultPort;
        return new Uri($"http://localhost:{port}/healthz");
    }

    /// <summary>Returns the exit code for the probe: 0 when the endpoint answers 2xx, otherwise 1.</summary>
    public static async Task<int> RunAsync(Uri uri, HttpMessageHandler? handler = null, TextWriter? output = null)
    {
        output ??= Console.Out;
        using var client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        client.Timeout = TimeSpan.FromSeconds(5);

        try
        {
            using var response = await client.GetAsync(uri);
            if (response.IsSuccessStatusCode)
            {
                await output.WriteLineAsync($"healthy: {(int)response.StatusCode}");
                return 0;
            }

            await output.WriteLineAsync($"unhealthy: {(int)response.StatusCode}");
            return 1;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            await output.WriteLineAsync($"unhealthy: {ex.GetType().Name}");
            return 1;
        }
    }
}
