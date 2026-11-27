using Microsoft.Extensions.Logging.Abstractions;
using Nullbox.Core.Filters;
using Nullbox.Core.Gmail;
using Nullbox.Gmail;
using Nullbox.Gmail.Filters;
using Nullbox.Tests.Gmail;

namespace Nullbox.Tests.Bulk;

public sealed class FilterDefinitionsTests
{
    private static string Fixture() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Filters", "filters.json"));

    [Fact]
    public void Emailblaster_filters_json_reads_as_it_is()
    {
        var filters = FilterDefinitions.Parse(Fixture());

        Assert.Equal(14, filters.Count);
        Assert.Equal(new FilterDefinition("Neighborhood", "from:(nextdoor.com)", false), filters[0]);
        Assert.Equal(new FilterDefinition("Deals & Promos", "from:(email.dunhamssports.com)", true), filters[4]);
        Assert.Equal("from:(email.dunhamssports.com) -> Deals & Promos, skip inbox, mark read", filters[4].Summary);
        Assert.Equal([GmailLabels.Inbox, GmailLabels.Unread], filters[4].RemoveLabelIds);
        Assert.Equal([GmailLabels.Inbox], filters[0].RemoveLabelIds);
    }

    [Theory]
    [InlineData("""{"filters":[{"label":"L","query":"q","mark_read":1}]}""", true)]
    [InlineData("""{"filters":[{"label":"L","query":"q","mark_read":"yes"}]}""", true)]
    [InlineData("""{"filters":[{"label":"L","query":"q","mark_read":null}]}""", false)]
    [InlineData("""{"filters":[{"label":"L","query":"q","mark_read":false}]}""", false)]
    [InlineData("""{"filters":[{"label":"L","query":"q"}]}""", false)]
    public void Mark_read_follows_python_truthiness_and_is_off_when_missing(string json, bool expected)
        => Assert.Equal(expected, Assert.Single(FilterDefinitions.Parse(json)).MarkRead);

    [Theory]
    [InlineData("""{"rules":[]}""", "\"filters\"")]
    [InlineData("""{"filters":[{"query":"q"}]}""", "filters[0].label")]
    [InlineData("""{"filters":[{"label":"L"}]}""", "filters[0].query")]
    [InlineData("""{"filters":[1]}""", "filters[0]")]
    [InlineData("""nope""", "JSON")]
    public void Invalid_files_name_the_problem(string json, string expected)
        => Assert.Contains(expected, Assert.Throws<FilterDefinitionsException>(() => FilterDefinitions.Parse(json)).Message, StringComparison.Ordinal);
}

public sealed class GmailFilterSyncTests
{
    private readonly FakeFilterApi _api = new();

    private GmailFilterSync Sync() => new(_api, NullLogger<GmailFilterSync>.Instance);

    private static readonly FilterDefinition[] Definitions =
    [
        new("Newsletters", "from:(a.example)", false),
        new("Newsletters", "from:(b.example)", false),
        new("Deals", "from:(c.example)", true),
    ];

    [Fact]
    public async Task A_dry_run_reports_what_it_would_create_and_changes_nothing()
    {
        var result = await Sync().SyncAsync(Definitions, dryRun: true);

        Assert.Equal(["Newsletters", "Deals"], result.LabelsCreated);
        Assert.All(result.Filters, f => Assert.Equal(FilterSyncStatus.WouldCreate, f.Status));
        Assert.Equal((3, 0), (result.Created, result.Existing));
        Assert.Empty(_api.Changes);
    }

    [Fact]
    public async Task Applying_creates_each_label_once_then_the_filters()
    {
        var result = await Sync().SyncAsync(Definitions, dryRun: false);

        Assert.Equal(
            [
                "label:Newsletters",
                "filter:from:(a.example) +Label_Newsletters -INBOX",
                "filter:from:(b.example) +Label_Newsletters -INBOX",
                "label:Deals",
                "filter:from:(c.example) +Label_Deals -INBOX,UNREAD",
            ],
            _api.Changes);
        Assert.All(result.Filters, f => Assert.Equal(FilterSyncStatus.Created, f.Status));
    }

    [Fact]
    public async Task Running_again_creates_nothing()
    {
        await Sync().SyncAsync(Definitions, dryRun: false);
        _api.Changes.Clear();

        var second = await Sync().SyncAsync(Definitions, dryRun: false);

        Assert.Empty(_api.Changes);
        Assert.Equal((0, 3), (second.Created, second.Existing));
        Assert.Empty(second.LabelsCreated);
    }

    [Fact]
    public async Task Existing_filters_match_on_query_and_label_sets_in_any_order()
    {
        _api.Labels["Deals"] = "Label_Deals";
        _api.Filters.Add(new GmailFilterSpec("from:(c.example)", ["Label_Deals"], [GmailLabels.Unread, GmailLabels.Inbox]));
        _api.Filters.Add(new GmailFilterSpec("from:(a.example)", ["Label_Other"], [GmailLabels.Inbox]));

        var result = await Sync().SyncAsync(Definitions, dryRun: true);

        Assert.Equal([FilterSyncStatus.WouldCreate, FilterSyncStatus.WouldCreate, FilterSyncStatus.Exists], result.Filters.Select(f => f.Status));
        Assert.Equal(["Newsletters"], result.LabelsCreated);
    }

    private sealed class FakeFilterApi : IGmailFilterApi
    {
        public Dictionary<string, string> Labels { get; } = new(StringComparer.Ordinal);

        public List<GmailFilterSpec> Filters { get; } = [];

        public List<string> Changes { get; } = [];

        public Task<Dictionary<string, string>> ListLabelsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new Dictionary<string, string>(Labels, StringComparer.Ordinal));

        public Task<string> CreateLabelAsync(string name, CancellationToken cancellationToken = default)
        {
            Changes.Add($"label:{name}");
            Labels[name] = "Label_" + name;
            return Task.FromResult(Labels[name]);
        }

        public Task<IReadOnlyList<GmailFilterSpec>> ListFiltersAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<GmailFilterSpec>>([.. Filters]);

        public Task CreateFilterAsync(GmailFilterSpec filter, CancellationToken cancellationToken = default)
        {
            Changes.Add($"filter:{filter.Query} +{string.Join(",", filter.AddLabelIds)} -{string.Join(",", filter.RemoveLabelIds)}");
            Filters.Add(filter);
            return Task.CompletedTask;
        }
    }
}

public sealed class GmailFilterApiTests
{
    private readonly FakeGmailApi _api = new();

    private GmailFilterApi Api() => new(_api.CreateService(), new GmailRetry(new GmailOptions { RetryBaseDelay = TimeSpan.FromMilliseconds(1) }));

    [Fact]
    public async Task Labels_and_filters_use_the_gmail_settings_endpoints()
    {
        _api.OnJson("GET /labels", new { labels = new[] { new { id = "Label_1", name = "Deals" } } });
        _api.OnJson("POST /labels", new { id = "Label_2", name = "News" });
        _api.OnJson("GET /settings/filters", new
        {
            filter = new[] { new { id = "f1", criteria = new { query = "from:(a.example)" }, action = new { addLabelIds = new[] { "Label_1" }, removeLabelIds = new[] { "INBOX" } } } },
        });
        _api.OnJson("POST /settings/filters", new { id = "f2" });

        var labels = await Api().ListLabelsAsync();
        var created = await Api().CreateLabelAsync("News");
        var filters = await Api().ListFiltersAsync();
        await Api().CreateFilterAsync(new GmailFilterSpec("from:(b.example)", ["Label_2"], ["INBOX", "UNREAD"]));

        Assert.Equal("Label_1", labels["Deals"]);
        Assert.Equal("Label_2", created);
        Assert.Equal("News", _api.To("POST /labels").Single().Json.GetProperty("name").GetString());
        var existing = Assert.Single(filters);
        Assert.Equal(("from:(a.example)", "Label_1", "INBOX"), (existing.Query, existing.AddLabelIds.Single(), existing.RemoveLabelIds.Single()));
        var body = _api.To("POST /settings/filters").Single().Json;
        Assert.Equal("from:(b.example)", body.GetProperty("criteria").GetProperty("query").GetString());
        Assert.Equal(["INBOX", "UNREAD"], body.GetProperty("action").GetProperty("removeLabelIds").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task An_empty_mailbox_has_no_labels_or_filters()
    {
        _api.OnJson("GET /labels", new { });
        _api.OnJson("GET /settings/filters", new { });

        Assert.Empty(await Api().ListLabelsAsync());
        Assert.Empty(await Api().ListFiltersAsync());
    }
}
