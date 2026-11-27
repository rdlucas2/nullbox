using System.Net;
using System.Text.Json;
using Nullbox.Tests.Infrastructure;
using Nullbox.Tests.Pipeline;

namespace Nullbox.Tests.Bulk;

[Collection(AppCollection.Name)]
public sealed class BulkEndpointsTests : IDisposable
{
    private readonly PipelineAppFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private async Task<HttpClient> SignedInAsync()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        using var login = await LoginHelper.LoginAsync(client, NullboxAppFactory.AdminPassword);
        return client;
    }

    private static async Task<FormUrlEncodedContent> TokenAsync(HttpClient client) => new([await LoginHelper.GetAntiforgeryFieldAsync(client)]);

    [Theory]
    [InlineData("/bulk/mark-read")]
    [InlineData("/gmail/filters")]
    public async Task Bulk_endpoints_need_the_admin_login(string path)
    {
        using var client = _factory.CreateClient(new() { AllowAutoRedirect = false });

        using var response = path.StartsWith("/bulk", StringComparison.Ordinal)
            ? await client.PostAsync(path, new FormUrlEncodedContent([]))
            : await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    [Fact]
    public async Task Starting_a_job_needs_an_antiforgery_token_and_a_delete_needs_a_query()
    {
        using var client = await SignedInAsync();

        using var noToken = await client.PostAsync("/bulk/mark-read", new FormUrlEncodedContent([]));
        using var noQuery = await client.PostAsync("/bulk/delete", await TokenAsync(client));

        Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, noQuery.StatusCode);
    }

    [Fact]
    public async Task A_bulk_archive_is_a_dry_run_unless_asked_and_points_at_its_job_page()
    {
        _factory.Gmail.InboxSearchResults.Add("m1");
        using var client = await SignedInAsync();

        using var started = await client.PostAsync("/bulk/archive", await TokenAsync(client));

        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        var job = JsonDocument.Parse(await started.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal($"/hangfire/jobs/details/{job.GetProperty("jobId").GetString()}", job.GetProperty("dashboard").GetString());

        for (var i = 0; i < 100 && !_factory.Gmail.Calls.Contains("search:in:inbox"); i++)
        {
            await Task.Delay(100);
        }

        Assert.Contains("search:in:inbox", _factory.Gmail.Calls);
        Assert.Empty(_factory.Gmail.Changes);
    }

    [Fact]
    public async Task Filter_definitions_are_listed_from_filters_json()
    {
        using var client = await SignedInAsync();
        var missing = JsonDocument.Parse(await client.GetStringAsync("/gmail/filters")).RootElement;

        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Filters", "filters.json"), Path.Combine(_factory.DataDirectory, "filters.json"));
        var present = JsonDocument.Parse(await client.GetStringAsync("/gmail/filters")).RootElement;

        Assert.False(missing.GetProperty("fileExists").GetBoolean());
        Assert.True(present.GetProperty("fileExists").GetBoolean());
        Assert.Equal(14, present.GetProperty("filters").GetArrayLength());
        Assert.Equal("from:(nextdoor.com) -> Neighborhood, skip inbox", present.GetProperty("filters")[0].GetProperty("summary").GetString());
    }
}
