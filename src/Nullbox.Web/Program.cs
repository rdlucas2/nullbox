using Nullbox.Core;
using Nullbox.Web;

if (args is [HealthProbe.Flag, ..])
{
    var uri = args.Length > 1
        ? new Uri(args[1])
        : HealthProbe.DefaultUri(Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS"));
    return await HealthProbe.RunAsync(uri);
}

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHealthChecks();

var app = builder.Build();

app.MapHealthChecks("/healthz");
app.MapGet("/", () => Results.Ok(new { name = AppInfo.Name, version = AppInfo.Version }));

await app.RunAsync();
return 0;

/// <summary>Exposed so integration tests can host the app with WebApplicationFactory.</summary>
public partial class Program;
