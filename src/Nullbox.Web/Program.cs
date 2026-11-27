using Nullbox.Core;
using Nullbox.Web.Auth;
using Nullbox.Web.Hosting;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddNullboxOptions(builder.Configuration);
builder.Services.AddHealthChecks();
builder.Services.AddAdminAuth();
builder.Services.AddNullboxJobs();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapHealthChecks("/healthz");
app.MapGet("/", () => Results.Ok(new { name = AppInfo.Name, version = AppInfo.Version }));
app.MapAdminAuthEndpoints();
app.MapNullboxDashboard();

app.WarnIfAdminPasswordMissing();

await app.RunAsync();

/// <summary>Exposed so integration tests can host the app with WebApplicationFactory.</summary>
public partial class Program;
