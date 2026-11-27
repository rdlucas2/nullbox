using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nullbox.Core.Cleanup;
using Nullbox.Tests.Infrastructure;
using Nullbox.Tests.Pipeline;

namespace Nullbox.Tests.Cleanup;

[Collection(AppCollection.Name)]
public sealed class CleanupEndpointsTests : IDisposable
{
    private readonly PipelineAppFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private void WriteConfig(string id, string fixture)
    {
        var dir = Path.Combine(_factory.DataDirectory, "cleanup");
        Directory.CreateDirectory(dir);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Cleanup", fixture), Path.Combine(dir, id + ".json"), overwrite: true);
    }

    private async Task<HttpClient> SignedInAsync()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        using var login = await LoginHelper.LoginAsync(client, NullboxAppFactory.AdminPassword);
        return client;
    }

    [Fact]
    public async Task Cleanup_endpoints_need_the_admin_login()
    {
        using var client = _factory.CreateClient(new() { AllowAutoRedirect = false });

        using var response = await client.GetAsync("/cleanup/configs");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    [Fact]
    public async Task Configs_show_the_exact_gmail_search_of_each_rule()
    {
        WriteConfig("cleanup_all_read", "cleanup_all_read.json");
        using var client = await SignedInAsync();

        var json = JsonDocument.Parse(await client.GetStringAsync("/cleanup/configs")).RootElement;

        var config = Assert.Single(json.EnumerateArray());
        Assert.Equal("cleanup_all_read", config.GetProperty("id").GetString());
        var rule = Assert.Single(config.GetProperty("rules").EnumerateArray());
        Assert.Equal("in:inbox (is:read)", rule.GetProperty("query").GetString());
        Assert.Equal("archive", rule.GetProperty("actions").GetString());
    }

    [Fact]
    public async Task Starting_a_run_needs_an_antiforgery_token_and_a_known_file()
    {
        WriteConfig("rules", "cleanup_rules.example.json");
        using var client = await SignedInAsync();

        using var noToken = await client.PostAsync("/cleanup/configs/rules/run", new FormUrlEncodedContent([]));
        using var unknown = await client.PostAsync("/cleanup/configs/nope/run", new FormUrlEncodedContent([await LoginHelper.GetAntiforgeryFieldAsync(client)]));

        Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task A_run_is_a_dry_run_unless_asked_and_its_result_is_kept()
    {
        WriteConfig("rules", "cleanup_rules.example.json");
        _factory.Gmail.InboxSearchResults.Add("m1");
        using var client = await SignedInAsync();

        using var started = await client.PostAsync("/cleanup/configs/rules/run?sample=1", new FormUrlEncodedContent([await LoginHelper.GetAntiforgeryFieldAsync(client)]));

        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        var id = JsonDocument.Parse(await started.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetInt64();
        Assert.Equal($"/cleanup/runs/{id}", started.Headers.Location?.OriginalString);

        var run = await WaitForFinishAsync(id);
        Assert.Equal((CleanupRunStatus.Completed, true), (run.Status, run.DryRun));
        Assert.Equal(1, run.Matched); // every rule sees m1, but only the first counts it
        Assert.Empty(_factory.Gmail.Changes);

        var listed = JsonDocument.Parse(await client.GetStringAsync("/cleanup/runs")).RootElement;
        Assert.Equal(id, listed[0].GetProperty("id").GetInt64());
    }

    private async Task<CleanupRun> WaitForFinishAsync(long id)
    {
        for (var i = 0; i < 100; i++)
        {
            await using var scope = _factory.Services.CreateAsyncScope();
            var run = await scope.ServiceProvider.GetRequiredService<ICleanupRunLog>().GetAsync(id);
            if (run is { Status: not (CleanupRunStatus.Queued or CleanupRunStatus.Running) })
            {
                return run;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"Cleanup run {id} did not finish.");
    }
}
