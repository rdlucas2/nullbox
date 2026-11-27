using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nullbox.Core.Cleanup;
using Nullbox.Core.Gmail;
using Nullbox.Core.Options;
using Nullbox.Gmail.Cleanup;
using Nullbox.Tests.Inbox;
using Nullbox.Tests.Pipeline;
using Nullbox.Web.Cleanup;

namespace Nullbox.Tests.Cleanup;

public sealed class FileCleanupConfigStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nullbox-cleanup-tests", Guid.NewGuid().ToString("N"));

    public FileCleanupConfigStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Cleanup", name));

    [Fact]
    public void Emailblaster_files_can_be_dropped_in_as_they_are()
    {
        File.WriteAllText(Path.Combine(_dir, "cleanup_rules.json"), Fixture("cleanup_rules.example.json"));
        File.WriteAllText(Path.Combine(_dir, "broken.json"), """{"rules":[]}""");
        File.WriteAllText(Path.Combine(_dir, "notes.txt"), "ignored");

        var entries = new FileCleanupConfigStore(_dir).List();

        Assert.Equal(["broken", "cleanup_rules"], entries.Select(e => e.Id));
        Assert.Null(entries[0].Config);
        Assert.Contains("rules", entries[0].Error, StringComparison.Ordinal);
        Assert.Equal(3, entries[1].Config!.Rules.Count);
    }

    [Theory]
    [InlineData("../secrets")]
    [InlineData("a/b")]
    [InlineData(".hidden")]
    [InlineData("missing")]
    public void Unknown_or_unsafe_ids_are_not_found(string id)
    {
        File.WriteAllText(Path.Combine(_dir, ".hidden.json"), Fixture("cleanup_all_read.json"));

        Assert.Null(new FileCleanupConfigStore(_dir).Get(id));
    }

    [Fact]
    public void A_missing_folder_lists_nothing()
        => Assert.Empty(new FileCleanupConfigStore(Path.Combine(_dir, "nope")).List());
}

public sealed class RunCleanupJobTests : IAsyncLifetime
{
    private readonly PipelineDb _db = new();
    private readonly FakeInboxGmail _gmail = new();
    private readonly StubConfigs _configs = new();
    private readonly NullboxOptions _options = new();

    public Task InitializeAsync() => _db.InitializeAsync();

    public Task DisposeAsync() => _db.DisposeAsync();

    private RunCleanupJob Job() => new(
        _configs,
        _db.CleanupRuns(),
        _db.Log(),
        Options.Create(_options),
        new CleanupRunner(_gmail, _db.Time, NullLogger<CleanupRunner>.Instance),
        NullLogger<RunCleanupJob>.Instance);

    private async Task<CleanupRun> RunAsync(bool dryRun, int sample = 0)
    {
        var queued = await _db.CleanupRuns().QueueAsync("rules", dryRun, sample);
        await Job().ExecuteAsync(queued.Id, default);
        return (await _db.CleanupRuns().GetAsync(queued.Id))!;
    }

    private void Configure(string json = """{"label":"cleanup_unread","keep":{"starred":false,"important":false},"rules":[{"name":"Old","query":"older_than:1y"}]}""")
        => _configs.Entries["rules"] = new CleanupConfigEntry("rules", CleanupConfig.Parse(json), null);

    [Fact]
    public async Task A_dry_run_records_counts_and_sender_samples_and_changes_nothing()
    {
        Configure();
        _gmail.InboxSearchResults.AddRange(["m1", "m2"]);
        _gmail.Add("m1");
        _gmail.Add("m2");

        var run = await RunAsync(dryRun: true, sample: 5);

        Assert.Equal((CleanupRunStatus.Completed, "cleanup_unread", 2, 0), (run.Status, run.Label, run.Matched, run.Changed));
        var rule = Assert.Single(run.Rules);
        Assert.Equal(("Old", "mark read + archive", "in:inbox is:unread (older_than:1y)"), (rule.Name, rule.Actions, rule.Query));
        Assert.Equal([new SenderCount("Shop <deals@shop.example>", 2)], rule.TopSenders);
        Assert.NotNull(run.FinishedUtc);
        Assert.Empty(_gmail.Changes);
    }

    [Fact]
    public async Task Applying_marks_read_and_archives()
    {
        Configure();
        _gmail.InboxSearchResults.Add("m1");

        var run = await RunAsync(dryRun: false);

        Assert.Equal((CleanupRunStatus.Completed, 1), (run.Status, run.Changed));
        Assert.Equal(["label:cleanup_unread", "modify:m1:+Label_cleanup_unread:-UNREAD,INBOX"], _gmail.Changes);
    }

    [Fact]
    public async Task The_runtime_kill_switch_blocks_applying_but_not_dry_runs()
    {
        Configure();
        _gmail.InboxSearchResults.Add("m1");
        await _db.Log().SetKillSwitchAsync(true);

        var blocked = await RunAsync(dryRun: false);
        var dryRun = await RunAsync(dryRun: true);

        Assert.Equal((CleanupRunStatus.Blocked, RunCleanupJob.KillSwitchReason), (blocked.Status, blocked.Error));
        Assert.Equal(CleanupRunStatus.Completed, dryRun.Status);
        Assert.Empty(_gmail.Changes);
    }

    [Fact]
    public async Task The_configured_kill_switch_blocks_applying()
    {
        Configure();
        _options.Pipeline.KillSwitch = true;

        Assert.Equal(CleanupRunStatus.Blocked, (await RunAsync(dryRun: false)).Status);
        Assert.Empty(_gmail.Calls);
    }

    [Fact]
    public async Task A_missing_or_broken_file_fails_the_run_with_the_reason()
    {
        var missing = await RunAsync(dryRun: true);
        _configs.Entries["rules"] = new CleanupConfigEntry("rules", null, "rules[0].query is missing.");
        var broken = await RunAsync(dryRun: true);

        Assert.Equal((CleanupRunStatus.Failed, "There is no cleanup file named rules."), (missing.Status, missing.Error));
        Assert.Equal((CleanupRunStatus.Failed, "rules[0].query is missing."), (broken.Status, broken.Error));
    }

    [Fact]
    public async Task A_gmail_failure_is_recorded_and_rethrown()
    {
        Configure();
        _gmail.Search = _ => throw new GmailReauthorizationRequiredException(GmailTokenKind.Primary, "invalid_grant");
        var queued = await _db.CleanupRuns().QueueAsync("rules", dryRun: true, sampleSize: 0);

        await Assert.ThrowsAsync<GmailReauthorizationRequiredException>(() => Job().ExecuteAsync(queued.Id, default));

        var run = (await _db.CleanupRuns().GetAsync(queued.Id))!;
        Assert.Equal(CleanupRunStatus.Failed, run.Status);
        Assert.StartsWith("GmailReauthorizationRequiredException:", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Runs_are_listed_newest_first()
    {
        var first = await _db.CleanupRuns().QueueAsync("a", true, 0);
        var second = await _db.CleanupRuns().QueueAsync("b", false, 0);

        var runs = await _db.CleanupRuns().ListAsync();

        Assert.Equal([second.Id, first.Id], runs.Select(r => r.Id));
        Assert.All(runs, r => Assert.Equal(CleanupRunStatus.Queued, r.Status));
    }

    private sealed class StubConfigs : ICleanupConfigStore
    {
        public Dictionary<string, CleanupConfigEntry> Entries { get; } = [];

        public IReadOnlyList<CleanupConfigEntry> List() => [.. Entries.Values];

        public CleanupConfigEntry? Get(string id) => Entries.GetValueOrDefault(id);

        public CleanupConfigEntry Save(string id, string json) => Entries[id] = new CleanupConfigEntry(id, CleanupConfig.Parse(json), null);
    }
}
