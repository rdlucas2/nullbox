using Nullbox.Core;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHealthChecks();

var app = builder.Build();

app.MapHealthChecks("/healthz");
app.MapGet("/", () => Results.Ok(new { name = AppInfo.Name, version = AppInfo.Version }));

app.Run();

/// <summary>Exposed so integration tests can host the app with WebApplicationFactory.</summary>
public partial class Program;
