using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nullbox.Core.Cleanup;
using Nullbox.Core.Gmail;
using Nullbox.Core.Jobs;
using Nullbox.Core.Options;
using Nullbox.Core.Pipeline;
using Nullbox.Core.Rules;
using Nullbox.Core.Settings;
using Nullbox.Gmail;
using Nullbox.Tests.Gmail;
using Nullbox.Tests.Inbox;
using Nullbox.Tests.Infrastructure;
using Nullbox.Tests.Pipeline;
using Nullbox.Web.Cleanup;
using Nullbox.Web.Inbox;
using Nullbox.Web.Ui.Audit;
using Nullbox.Web.Ui.Cleanup;
using Nullbox.Web.Ui.Jobs;

namespace Nullbox.Tests.Ui.Operations;

/// <summary>Scheduler stand-in: records crons and triggers; refuses crons that are not 5 or 6 fields, like Hangfire.</summary>
public sealed class FakeScheduler : IJobScheduler
{
    public Dictionary<string, string> Crons { get; } = [];

    public List<string> Triggered { get; } = [];

    public void AddOrUpdateRecurring<TJob>(string jobId, string cron)
        where TJob : IRecurringJob
    {
        if (cron.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length is not (5 or 6))
        {
            throw new ArgumentException("CRON expression is invalid.", nameof(cron));
        }

        Crons[jobId] = cron;
    }

    public void RemoveRecurring(string jobId) => Crons.Remove(jobId);

    public string? TriggerRecurring(string jobId)
    {
        Triggered.Add(jobId);
        return "job-" + Triggered.Count;
    }
}

/// <summary>Records what would be enqueued instead of running it.</summary>
public sealed class RecordingJobClient : IBackgroundJobClient
{
    public List<Job> Jobs { get; } = [];

    public string Create(Job job, IState state)
    {
        Jobs.Add(job);
        return Jobs.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public bool ChangeState(string jobId, IState state, string expectedState) => true;
}

public static class AuditSamples
{
    /// <summary>Stores <paramref name="record"/> and returns it with its id.</summary>
    public static async Task<DecisionRecord> StoreAsync(PipelineDb db, DecisionRecord record)
    {
        await db.Log().RecordAsync(record);
        return (await db.Log().QueryAsync(new DecisionQuery { MessageId = record.MessageId, Limit = 1 })).Items[0];
    }

    public static DecisionRecord Applied(string messageId, IReadOnlyList<string> actions, IReadOnlyList<string> labels, DateTimeOffset at)
        => new(messageId, "promo-cleanup@v1", RuleSetMode.Live, DecisionOutcome.Applied, ["Archive confident promos"], actions, [], "jev-fake", "{}", at) { Labels = labels };
}

public sealed class DecisionUndoTests : IAsyncLifetime
{
    private readonly PipelineDb _db = new();
    private readonly FakeInboxGmail _gmail = new();

    public Task InitializeAsync() => _db.InitializeAsync();

    public Task DisposeAsync() => _db.DisposeAsync();

    private DecisionUndo Undo() => new(_gmail, _db.Log(), _db.Time);

    [Fact]
    public async Task Undo_reverses_only_what_the_rule_changed_and_is_recorded()
    {
        var record = await AuditSamples.StoreAsync(_db, AuditSamples.Applied(
            "m1",
            ["label:nullbox/promo", "label:nullbox/review/promo-cleanup/archive-confident-promos", "mark_read", "archive"],
            [GmailLabels.Inbox, GmailLabels.Unread, "Label_nullbox/promo"],
            _db.Time.GetUtcNow()));

        var result = await Undo().UndoAsync(record);

        Assert.True(result.Done);
        Assert.Contains("modify:m1:+UNREAD,INBOX:-Label_nullbox/review/promo-cleanup/archive-confident-promos", _gmail.Changes);
        Assert.Equal(["removed label nullbox/review/promo-cleanup/archive-confident-promos", "marked unread", "moved back to the inbox"], result.Reversed);
        var undone = Assert.Single((await _db.Log().QueryAsync(new DecisionQuery { Outcome = DecisionOutcome.Undone })).Items);
        Assert.Equal(("m1", "promo-cleanup@v1"), (undone.MessageId, undone.RuleSetKey));
        Assert.True(await Undo().IsUndoneAsync(record));

        var again = await Undo().UndoAsync(record);
        Assert.False(again.Done);
        Assert.Contains("already undone", again.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Undoing_a_delete_takes_it_out_of_trash_and_back_to_the_inbox()
    {
        var record = await AuditSamples.StoreAsync(_db, AuditSamples.Applied("m2", ["label:nullbox/review/x/y", "delete"], [GmailLabels.Inbox], _db.Time.GetUtcNow()));

        await Undo().UndoAsync(record);

        Assert.Equal(["label:nullbox/review/x/y", "untrash:m2", "modify:m2:+INBOX:-Label_nullbox/review/x/y"], _gmail.Changes);
    }

    [Theory]
    [InlineData(DecisionOutcome.DryRun)]
    [InlineData(DecisionOutcome.KillSwitch)]
    [InlineData(DecisionOutcome.NoChange)]
    public async Task Only_applied_decisions_can_be_undone(DecisionOutcome outcome)
    {
        var record = AuditSamples.Applied("m3", ["archive"], [GmailLabels.Inbox], _db.Time.GetUtcNow()) with { Outcome = outcome };

        Assert.False(DecisionUndo.CanUndo(record));
        Assert.False((await Undo().UndoAsync(record)).Done);
        Assert.Empty(_gmail.Changes);
    }
}

public sealed class JobControlsTests : IAsyncLifetime, IDisposable
{
    private readonly PipelineDb _db = new();
    private readonly TempStore _inbox = new();
    private readonly FakeScheduler _scheduler = new();
    private readonly NullboxOptions _options = new();

    public Task InitializeAsync() => _db.InitializeAsync();

    public Task DisposeAsync() => _db.DisposeAsync();

    public void Dispose() => _inbox.Dispose();

    private JobControls Controls() => new(_scheduler, _inbox.Store, _db.Log(), _db.AppSettings(), Options.Create(_options));

    [Fact]
    public async Task A_new_poll_schedule_is_applied_and_kept_and_can_be_reset()
    {
        Assert.Null(await Controls().SetPollCronAsync("*/15 * * * *"));

        var state = await Controls().GetAsync();
        Assert.Equal(("*/15 * * * *", true), (state.PollCron, state.PollCronFromSettings));
        Assert.Equal("*/15 * * * *", _scheduler.Crons[InboxPollJob.JobId]);
        Assert.Equal("*/15 * * * *", await _db.AppSettings().GetAsync(AppSettingKeys.InboxPollCron));

        await Controls().SetPollCronAsync(null);
        Assert.Equal((_options.Jobs.InboxPollCron, false), ((await Controls().GetAsync()).PollCron, (await Controls().GetAsync()).PollCronFromSettings));
        Assert.Null(await _db.AppSettings().GetAsync(AppSettingKeys.InboxPollCron));
    }

    [Fact]
    public async Task A_bad_cron_changes_nothing()
    {
        var problem = await Controls().SetPollCronAsync("every five minutes please");

        Assert.Contains("not a valid cron", problem, StringComparison.Ordinal);
        Assert.Empty(_scheduler.Crons);
        Assert.Null(await _db.AppSettings().GetAsync(AppSettingKeys.InboxPollCron));
    }

    [Fact]
    public async Task Pause_kill_switch_and_run_now_reach_their_stores()
    {
        await Controls().SetPollPausedAsync(true);
        await Controls().SetKillSwitchAsync(true);
        var id = Controls().RunPollNow();

        var state = await Controls().GetAsync();
        Assert.True(state.PollPaused);
        Assert.True(state.KillSwitch);
        Assert.False(state.KillSwitchFromConfig);
        Assert.Equal([InboxPollJob.JobId], _scheduler.Triggered);
        Assert.NotNull(id);
    }

    [Fact]
    public async Task The_stored_schedule_is_put_back_at_startup()
    {
        await _db.AppSettings().SetAsync(AppSettingKeys.InboxPollCron, "0 * * * *");
        using var services = new ServiceCollection().AddScoped<IAppSettingsStore>(_ => _db.AppSettings()).BuildServiceProvider();

        await new StoredPollSchedule(services.GetRequiredService<IServiceScopeFactory>(), _scheduler, NullLogger<StoredPollSchedule>.Instance).StartAsync(default);

        Assert.Equal("0 * * * *", _scheduler.Crons[InboxPollJob.JobId]);
    }
}

[Collection(AppCollection.Name)]
public sealed class HangfireCronValidationTests(NullboxAppFactory factory)
{
    [Fact]
    public void The_real_scheduler_refuses_a_bad_cron_with_an_argument_exception()
    {
        var scheduler = factory.Services.GetRequiredService<IJobScheduler>();

        Assert.ThrowsAny<ArgumentException>(() => scheduler.AddOrUpdateRecurring<InboxPollJob>("cron-validation-test", "every five minutes"));
    }
}

public sealed class CleanupEditingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nullbox-cleanup-edit-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Cleanup", name));

    [Theory]
    [InlineData("cleanup_rules.example.json")]
    [InlineData("cleanup_all_read.json")]
    [InlineData("cleanup_all_unread.json")]
    public void Emailblaster_files_survive_the_form_and_json_round_trip(string fixture)
    {
        var config = CleanupConfig.Parse(Fixture(fixture));

        var json = CleanupForm.From(config).TryToJson(out var problem);

        Assert.Null(problem);
        var again = CleanupConfig.Parse(json!);
        Assert.Equal(config.Label, again.Label);
        Assert.Equal(config.Keep.Senders, again.Keep.Senders);
        Assert.Equal((config.Keep.Starred, config.Keep.Important), (again.Keep.Starred, again.Keep.Important));
        Assert.Equal(config.Rules, again.Rules);
        Assert.Equal(config.Rules.Select(r => CleanupQuery.Build(r, config.Keep)), again.Rules.Select(r => CleanupQuery.Build(r, again.Keep)));
    }

    [Fact]
    public void A_form_with_a_blank_query_is_not_a_valid_file()
    {
        var form = CleanupForm.Starter();
        form.Rules[0].Query = " ";

        Assert.Null(form.TryToJson(out var problem));
        Assert.Contains("query", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Saving_validates_then_writes_owner_only()
    {
        var store = new FileCleanupConfigStore(_dir);

        Assert.Throws<CleanupConfigException>(() => store.Save("bad", """{"rules":[]}"""));
        Assert.Throws<ArgumentException>(() => store.Save("../escape", Fixture("cleanup_all_read.json")));
        var saved = store.Save("all_read", Fixture("cleanup_all_read.json"));

        Assert.NotNull(saved.Config);
        Assert.Equal(["all_read"], store.List().Select(e => e.Id));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(_dir, "all_read.json")));
        }
    }
}

public sealed class GmailUndoSupportTests
{
    [Fact]
    public async Task Untrash_restores_each_distinct_message_even_while_deletes_are_locked()
    {
        var api = new FakeGmailApi();
        api.On("POST /messages/*/untrash", req => FakeGmailApi.Json(new { id = req.Route.Split('/')[2] }));
        var service = new GmailApiService(api.CreateService(), Options.Create(new GmailOptions()), NullLogger<GmailApiService>.Instance);

        await service.UntrashAsync(["a", "b", "a"]);

        Assert.Equal(["POST /messages/a/untrash", "POST /messages/b/untrash"], api.Requests.Select(r => r.Route));
    }
}
