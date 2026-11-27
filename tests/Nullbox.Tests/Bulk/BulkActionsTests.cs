using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Nullbox.Core.Bulk;
using Nullbox.Core.Filters;
using Nullbox.Core.Gmail;
using Nullbox.Core.Options;
using Nullbox.Gmail.Bulk;
using Nullbox.Gmail.Filters;
using Nullbox.Tests.Inbox;
using Nullbox.Tests.Pipeline;
using Nullbox.Web.Bulk;

namespace Nullbox.Tests.Bulk;

public sealed class BulkActionsTests
{
    private readonly FakeInboxGmail _gmail = new();
    private readonly GmailOptions _options = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 27, 9, 5, 7, TimeSpan.Zero));

    private BulkActions Bulk() => new(_gmail, Options.Create(_options), _time, NullLogger<BulkActions>.Instance);

    [Fact]
    public async Task Archive_dry_run_counts_the_inbox_and_changes_nothing()
    {
        _gmail.InboxSearchResults.AddRange(["m1", "m2"]);

        var result = await Bulk().ArchiveAsync("", null, dryRun: true);

        Assert.Equal(new BulkResult(BulkAction.Archive, "in:inbox", true, 2, 0, "archive_20261127_090507"), result);
        Assert.Equal(["search:in:inbox"], _gmail.Calls);
        Assert.Empty(_gmail.Changes);
    }

    [Fact]
    public async Task Archive_removes_inbox_and_adds_the_label()
    {
        _gmail.InboxSearchResults.AddRange(["m1", "m2"]);

        var result = await Bulk().ArchiveAsync("from:shop.example", "old_shop", dryRun: false);

        Assert.Equal(["label:old_shop", "modify:m1,m2:+Label_old_shop:-INBOX"], _gmail.Changes);
        Assert.Equal(("from:shop.example", 2, 2), (result.Query, result.Matched, result.Changed));
    }

    [Fact]
    public async Task Archive_with_no_matches_creates_no_label()
    {
        var result = await Bulk().ArchiveAsync("in:inbox", null, dryRun: false);

        Assert.Empty(_gmail.Changes);
        Assert.Equal(0, result.Matched);
    }

    [Fact]
    public async Task Mark_all_read_removes_unread_from_every_unread_message()
    {
        _gmail.InboxSearchResults.AddRange(["m1", "m2"]);

        var dry = await Bulk().MarkAllReadAsync(dryRun: true);
        var applied = await Bulk().MarkAllReadAsync(dryRun: false);

        Assert.Equal((2, 0), (dry.Matched, dry.Changed));
        Assert.Equal((2, 2), (applied.Matched, applied.Changed));
        Assert.Equal(["modify:m1,m2:+:-UNREAD"], _gmail.Changes);
        Assert.All(_gmail.Calls, c => Assert.Equal("search:is:unread", c));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Delete_is_refused_before_any_gmail_call_unless_both_settings_are_on(bool allowDelete, bool allowPermanent)
    {
        (_options.AllowDelete, _options.AllowPermanentDelete) = (allowDelete, allowPermanent);
        _gmail.InboxSearchResults.Add("m1");

        var result = await Bulk().DeleteAsync("label:old", BulkActions.DefaultDeleteCap, dryRun: false);

        Assert.Equal(BulkActions.DeleteLockedReason, result.BlockedReason);
        Assert.Empty(_gmail.Calls);
        Assert.Empty(_gmail.Changes);
    }

    [Fact]
    public async Task A_delete_dry_run_counts_up_to_the_cap_even_while_locked()
    {
        _gmail.InboxSearchResults.AddRange(["m1", "m2", "m3"]);

        var result = await Bulk().DeleteAsync("label:old", cap: 2, dryRun: true);

        Assert.Equal((2, 0, null), (result.Matched, result.Changed, result.BlockedReason));
        Assert.Empty(_gmail.Changes);
    }

    [Fact]
    public async Task Delete_removes_at_most_the_cap_once_both_settings_are_on()
    {
        (_options.AllowDelete, _options.AllowPermanentDelete) = (true, true);
        _gmail.AllowPermanentDelete = true;
        _gmail.InboxSearchResults.AddRange(["m1", "m2", "m3"]);

        var result = await Bulk().DeleteAsync("label:old", cap: 2, dryRun: false);

        Assert.Equal(["delete:m1,m2"], _gmail.Changes);
        Assert.Equal((2, 2), (result.Matched, result.Changed));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task Delete_needs_a_search(string query)
        => await Assert.ThrowsAsync<ArgumentException>(() => Bulk().DeleteAsync(query, 10, dryRun: true));
}

public sealed class BulkJobsTests : IAsyncLifetime, IDisposable
{
    private readonly PipelineDb _db = new();
    private readonly FakeInboxGmail _gmail = new();
    private readonly GmailOptions _gmailOptions = new();
    private readonly NullboxOptions _options = new();
    private readonly string _filtersPath = Path.Combine(Path.GetTempPath(), "nullbox-bulk-tests", Guid.NewGuid().ToString("N"), "filters.json");
    private readonly EmptyFilterApi _filterApi = new();

    public Task InitializeAsync() => _db.InitializeAsync();

    public Task DisposeAsync() => _db.DisposeAsync();

    public void Dispose()
    {
        if (Directory.Exists(Path.GetDirectoryName(_filtersPath)))
        {
            Directory.Delete(Path.GetDirectoryName(_filtersPath)!, recursive: true);
        }
    }

    private BulkJobs Jobs() => new(
        new BulkActions(_gmail, Options.Create(_gmailOptions), _db.Time, NullLogger<BulkActions>.Instance),
        new GmailFilterSync(_filterApi, NullLogger<GmailFilterSync>.Instance),
        new FilterDefinitionsFile(_filtersPath),
        _db.Log(),
        Options.Create(_options),
        Options.Create(_gmailOptions));

    private void WriteFilters()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filtersPath)!);
        File.WriteAllText(_filtersPath, """{"filters":[{"label":"News","query":"from:(a.example)"}]}""");
    }

    [Fact]
    public async Task The_kill_switch_blocks_applying_but_not_dry_runs()
    {
        _gmail.InboxSearchResults.Add("m1");
        await _db.Log().SetKillSwitchAsync(true);

        var archive = await Jobs().ArchiveAsync("in:inbox", null, dryRun: false, default);
        var read = await Jobs().MarkAllReadAsync(dryRun: false, default);
        var delete = await Jobs().DeleteAsync("label:x", 10, dryRun: false, default);
        var dry = await Jobs().ArchiveAsync("in:inbox", null, dryRun: true, default);

        Assert.All([archive, read, delete], r => Assert.Equal(BulkJobs.KillSwitchReason, r.BlockedReason));
        Assert.Equal((1, null), (dry.Matched, dry.BlockedReason));
        Assert.Empty(_gmail.Changes);
    }

    [Fact]
    public async Task Filter_sync_needs_its_setting_to_apply_but_not_for_a_dry_run()
    {
        WriteFilters();

        var blocked = await Jobs().SyncFiltersAsync(dryRun: false, default);
        var dry = await Jobs().SyncFiltersAsync(dryRun: true, default);
        _gmailOptions.AllowFilterSync = true;
        var applied = await Jobs().SyncFiltersAsync(dryRun: false, default);

        Assert.Equal(BulkJobs.FilterSyncDisabledReason, blocked.BlockedReason);
        Assert.Equal(FilterSyncStatus.WouldCreate, Assert.Single(dry.Filters).Status);
        Assert.Equal(FilterSyncStatus.Created, Assert.Single(applied.Filters).Status);
        Assert.Equal(1, _filterApi.Created);
    }

    [Fact]
    public async Task Filter_sync_respects_the_kill_switch()
    {
        WriteFilters();
        _gmailOptions.AllowFilterSync = true;
        _options.Pipeline.KillSwitch = true;

        Assert.Equal(BulkJobs.KillSwitchReason, (await Jobs().SyncFiltersAsync(dryRun: false, default)).BlockedReason);
        Assert.Equal(0, _filterApi.Created);
    }

    [Fact]
    public async Task Filter_sync_without_filters_json_says_where_to_put_it()
        => Assert.Contains("filters.json", (await Assert.ThrowsAsync<FileNotFoundException>(() => Jobs().SyncFiltersAsync(dryRun: true, default))).Message, StringComparison.Ordinal);

    private sealed class EmptyFilterApi : IGmailFilterApi
    {
        public int Created { get; private set; }

        public Task<Dictionary<string, string>> ListLabelsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new Dictionary<string, string>());

        public Task<string> CreateLabelAsync(string name, CancellationToken cancellationToken = default) => Task.FromResult("Label_" + name);

        public Task<IReadOnlyList<GmailFilterSpec>> ListFiltersAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<GmailFilterSpec>>([]);

        public Task CreateFilterAsync(GmailFilterSpec filter, CancellationToken cancellationToken = default)
        {
            Created++;
            return Task.CompletedTask;
        }
    }
}
