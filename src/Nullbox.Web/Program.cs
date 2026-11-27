using Nullbox.Core;
using Nullbox.Core.Rules;
using Nullbox.Data;
using Nullbox.Jev;
using Nullbox.Web;
using Nullbox.Web.Auth;
using Nullbox.Web.Backup;
using Nullbox.Web.Hosting;
using Nullbox.Web.Inbox;
using Nullbox.Web.Pipeline;
using Nullbox.Web.Settings;
using Nullbox.Web.Ui;

if (args is [HealthProbe.Flag, ..])
{
    var uri = args.Length > 1
        ? new Uri(args[1])
        : HealthProbe.DefaultUri(Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS"));
    return await HealthProbe.RunAsync(uri);
}

if (args is [DataBackup.Flag, ..])
{
    return await DataBackup.RunCliAsync(args, Environment.GetEnvironmentVariable, Console.Out, Console.Error, DateTimeOffset.UtcNow);
}

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddNullboxOptions(builder.Configuration);
builder.Services.AddNullboxDataProtection(builder.Configuration);
builder.Services.AddHealthChecks();
builder.Services.AddNullboxForwardedHeaders(builder.Configuration);
builder.Services.AddAdminAuth();
builder.Services.AddNullboxJobs();
builder.Services.AddNullboxGmail(builder.Configuration);
builder.Services.AddNullboxData(NullboxHosting.ResolveDataDirectory(builder.Configuration));
builder.Services.AddNullboxInbox();
builder.Services.AddNullboxJev(builder.Configuration, NullboxHosting.ResolveDataDirectory(builder.Configuration));
builder.Services.AddNullboxUi();

var app = builder.Build();

app.UseNullboxForwardedHeaders();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapHealthChecks("/healthz");
app.MapGet("/api/info", () => Results.Ok(new { name = AppInfo.Name, version = AppInfo.Version }));
app.MapAdminAuthEndpoints();
app.MapNullboxDashboard();
app.MapGmailSettings();
app.MapGet("/schemas/ruleset.schema.json", () => Results.Text(RuleSetSchema.Text, "application/schema+json"));
app.MapInboxEndpoints();
app.MapPipelineEndpoints();
app.MapNullboxUi();

app.WarnIfAdminPasswordMissing();

await app.RunAsync();
return 0;

/// <summary>Exposed so integration tests can host the app with WebApplicationFactory.</summary>
public partial class Program;
