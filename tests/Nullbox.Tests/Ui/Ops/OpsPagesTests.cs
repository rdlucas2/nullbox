using System.Collections.Concurrent;
using Bunit;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MudBlazor.Services;
using Nullbox.Core.Cleanup;
using Nullbox.Core.Gmail;
using Nullbox.Core.Inbox;
using Nullbox.Core.Jobs;
using Nullbox.Core.Options;
using Nullbox.Core.Pipeline;
using Nullbox.Core.Rules;
using Nullbox.Jev;
using Nullbox.Tests.Inbox;
using Nullbox.Tests.Pipeline;
using Nullbox.Web.Cleanup;
using Nullbox.Web.Inbox;
using Nullbox.Web.Jobs;
using Nullbox.Web.Pipeline;
using Nullbox.Web.Settings;
using AuditPage = Nullbox.Web.Components.Pages.Audit;
using CleanupPage = Nullbox.Web.Components.Pages.Cleanup;
using JobsPage = Nullbox.Web.Components.Pages.Jobs;
using SettingsPage = Nullbox.Web.Components.Pages.Settings;

namespace Nullbox.Tests.Ui.Ops;

/// <summary>A bUnit context with MudBlazor and a migrated database, shared by the ops page tests.</summary>
public abstract class OpsPageTest : IAsyncLifetime
{
    protected PipelineDb Db { get; } = new();

    protected BunitContext Ctx { get; } = new();

    protected FakeInboxGmail Gmail { get; } = new();

    protected NullboxOptions Options { get; } = new();

    protected OpsPageTest()
    {
        Ctx.Services.AddMudServices();
        Ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        Ctx.Services.AddSingleton<TimeProvider>(Db.Time);
        Ctx.Services.AddSingleton<IOptions<NullboxOptions>>(Microsoft.Extensions.Options.Options.Create(Options));
        Ctx.Services.AddSingleton(new RuntimeSettingsFile(RuntimeSettingsFile.In(Db.Directory)));
    }

    protected RuntimeSettingsFile SavedSettings => Ctx.Services.GetRequiredService<RuntimeSettingsFile>();

    public Task InitializeAsync() => Db.InitializeAsync();

    public async Task DisposeAsync()
    {
        await Ctx.DisposeAsync();
        await Db.DisposeAsync();
    }
}

public sealed class AuditPageTests : OpsPageTest
{
    public AuditPageTests()
    {
        Ctx.Services.AddTransient<IDecisionLog>(_ => Db.Log());
        Ctx.Services.AddSingleton<IGmailService>(Gmail);
    }

    private static DecisionRecord Record(string messageId, RuleSetMode mode, DecisionOutcome outcome, params string[] actions)
        => new(messageId, "promo-cleanup@v2", mode, outcome, ["Archive confident promos"], actions, [], "jev", """{"category":{"choice":"promo"}}""",
            new DateTimeOffset(2026, 11, 27, 8, 0, 0, TimeSpan.Zero))
        {
            Labels = [GmailLabels.Inbox, GmailLabels.Unread],
            Confidence = 0.9,
        };

    [Fact]
    public async Task A_live_decision_is_undone_in_gmail_only_after_confirming()
    {
        await Db.Log().RecordAsync(Record("m1", RuleSetMode.Live, DecisionOutcome.Applied, "label:nullbox/promo", "mark_read", "archive"));
        var id = (await Db.Log().ListAsync("m1")).Single().Id;
        var cut = Ctx.Render<AuditPage>();

        cut.Find($"[data-testid=audit-row-{id}]").Click();
        Assert.Contains("label:nullbox/promo, mark_read, archive", cut.Find("[data-testid=audit-detail-actions]").TextContent, StringComparison.Ordinal);
        cut.Find("[data-testid=undo]").Click();
        Assert.Empty(Gmail.Changes);

        cut.Find("[data-testid=confirm-undo-yes]").Click();

        cut.WaitForElement("[data-testid=undo-done]");
        Assert.Equal(["label:nullbox/promo", "modify:m1:+UNREAD,INBOX:-Label_nullbox/promo"], Gmail.Changes);
    }

    [Fact]
    public async Task An_undo_is_logged_and_after_a_reload_cannot_run_again()
    {
        await Db.Log().RecordAsync(Record("m1", RuleSetMode.Live, DecisionOutcome.Applied, "mark_read", "archive"));
        var id = (await Db.Log().ListAsync("m1")).Single().Id;
        var first = Ctx.Render<AuditPage>();
        first.Find($"[data-testid=audit-row-{id}]").Click();
        first.Find("[data-testid=undo]").Click();
        first.Find("[data-testid=confirm-undo-yes]").Click();
        first.WaitForElement("[data-testid=undo-done]");

        var undone = Assert.Single(await Db.Log().ListAsync("m1"), r => r.Outcome == DecisionOutcome.Undone);
        Assert.Equal(["Mark it unread", "Move it back to the inbox"], undone.Actions);

        var reloaded = Ctx.Render<AuditPage>();
        reloaded.Find($"[data-testid=audit-row-{id}]").Click();

        reloaded.WaitForElement("[data-testid=undo-already]");
        Assert.Contains("Move it back to the inbox", reloaded.Find("[data-testid=undo-already]").TextContent, StringComparison.Ordinal);
        Assert.Empty(reloaded.FindAll("[data-testid=undo]"));
        Assert.Single(Gmail.Changes);
    }

    [Fact]
    public async Task Cancelling_the_undo_changes_nothing()
    {
        await Db.Log().RecordAsync(Record("m1", RuleSetMode.Live, DecisionOutcome.Applied, "archive"));
        var cut = Ctx.Render<AuditPage>();

        cut.Find("[data-testid^=audit-row-]").Click();
        cut.Find("[data-testid=undo]").Click();
        cut.Find("[data-testid=confirm-undo-no]").Click();

        Assert.Empty(cut.FindAll("[data-testid=confirm-undo]"));
        Assert.Empty(Gmail.Changes);
    }

    [Fact]
    public async Task A_dry_run_offers_no_undo()
    {
        await Db.Log().RecordAsync(Record("m1", RuleSetMode.DryRun, DecisionOutcome.DryRun, "archive"));
        var cut = Ctx.Render<AuditPage>();

        cut.Find("[data-testid^=audit-row-]").Click();

        Assert.Equal(DecisionUndo.NotAppliedReason, cut.Find("[data-testid=undo-unavailable]").TextContent.Trim());
        Assert.Empty(cut.FindAll("[data-testid=undo]"));
    }

    [Fact]
    public async Task Filters_narrow_the_list_and_clear_resets_it()
    {
        await Db.Log().RecordAsync(Record("m1", RuleSetMode.DryRun, DecisionOutcome.DryRun, "archive"));
        await Db.Log().RecordAsync(Record("m2", RuleSetMode.Live, DecisionOutcome.Applied, "archive"));
        var cut = Ctx.Render<AuditPage>();
        Assert.Equal(2, cut.FindAll("[data-testid^=audit-row-]").Count);

        cut.Find("[data-testid=filter-outcome]").Change(nameof(DecisionOutcome.Applied));
        cut.Find("[data-testid=filter-apply]").Click();
        Assert.Contains("m2", Assert.Single(cut.FindAll("[data-testid^=audit-row-]")).TextContent, StringComparison.Ordinal);

        cut.Find("[data-testid=filter-message]").Change("nope");
        cut.Find("[data-testid=filter-apply]").Click();
        cut.WaitForElement("[data-testid=audit-empty]");

        cut.Find("[data-testid=filter-clear]").Click();
        Assert.Equal(2, cut.FindAll("[data-testid^=audit-row-]").Count);
    }

    [Fact]
    public async Task The_message_id_query_starts_the_page_filtered()
    {
        await Db.Log().RecordAsync(Record("m1", RuleSetMode.DryRun, DecisionOutcome.DryRun, "archive"));
        await Db.Log().RecordAsync(Record("m2", RuleSetMode.DryRun, DecisionOutcome.DryRun, "archive"));
        Ctx.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo("/audit?messageId=m1");

        var cut = Ctx.Render<AuditPage>();

        Assert.Contains("m1", Assert.Single(cut.FindAll("[data-testid^=audit-row-]")).TextContent, StringComparison.Ordinal);
    }
}

public sealed class JobsPageTests : OpsPageTest
{
    private readonly FakeScheduler _scheduler = new();
    private readonly FakeJobsReader _reader = new();

    public JobsPageTests()
    {
        Ctx.Services.AddSingleton<IJobScheduler>(_scheduler);
        Ctx.Services.AddSingleton<IRecurringJobsReader>(_reader);
        Ctx.Services.AddTransient<IPipelineSettingsStore>(_ => Db.Log());
        Ctx.Services.AddSingleton<IInboxStateStore>(_ => new SqliteInboxStateStore(Path.Combine(Db.Directory, "inbox.db")));
        _reader.Jobs.Add(new RecurringJobView(AuditPurgeJob.JobId, "0 3 * * *", null, null, null, null));
        _reader.Jobs.Add(new RecurringJobView(InboxPollJob.JobId, "*/5 * * * *", new DateTime(2026, 11, 27, 8, 55, 0, DateTimeKind.Utc), "Succeeded", null, null));
    }

    [Fact]
    public async Task Pausing_is_immediate_but_resuming_asks_first()
    {
        var cut = Ctx.Render<JobsPage>();
        cut.Find("[data-testid=kill-switch-off]");

        cut.Find("[data-testid=pause]").Click();
        cut.WaitForElement("[data-testid=kill-switch-on]");
        Assert.True(await Db.Log().GetKillSwitchAsync());

        cut.Find("[data-testid=resume]").Click();
        Assert.True(await Db.Log().GetKillSwitchAsync());
        cut.Find("[data-testid=confirm-resume-yes]").Click();

        cut.WaitForElement("[data-testid=kill-switch-off]");
        Assert.False(await Db.Log().GetKillSwitchAsync());
    }

    [Fact]
    public void A_kill_switch_set_in_configuration_cannot_be_turned_off_here()
    {
        Options.Pipeline.KillSwitch = true;

        var cut = Ctx.Render<JobsPage>();

        cut.Find("[data-testid=kill-switch-config]");
        Assert.Empty(cut.FindAll("[data-testid=resume]"));
    }

    [Fact]
    public void A_bad_schedule_is_refused_and_a_good_one_is_applied_and_saved()
    {
        var cut = Ctx.Render<JobsPage>();
        Assert.Equal("*/5 * * * *", cut.Find("[data-testid=poll-cron]").GetAttribute("value"));

        cut.Find("[data-testid=poll-cron]").Change("whenever");
        cut.Find("[data-testid=poll-cron-save]").Click();
        cut.WaitForElement("[data-testid=jobs-error]");
        Assert.Empty(_scheduler.Updated);

        cut.Find("[data-testid=poll-cron]").Change("*/10 * * * *");
        cut.Find("[data-testid=poll-cron-save]").Click();

        cut.WaitForElement("[data-testid=jobs-message]");
        Assert.Equal([$"{InboxPollJob.JobId}={"*/10 * * * *"}"], _scheduler.Updated);
        Assert.Equal("*/10 * * * *", SavedSettings.Load()[RuntimeSettingsFile.InboxPollCron]);
    }

    [Fact]
    public async Task Pausing_the_poll_is_stored()
    {
        var cut = Ctx.Render<JobsPage>();

        cut.Find("[data-testid=poll-pause]").Click();

        cut.WaitForAssertion(() => Assert.Contains("Polling is paused", cut.Find("[data-testid=inbox-last-poll]").TextContent, StringComparison.Ordinal));
        Assert.True((await Ctx.Services.GetRequiredService<IInboxStateStore>().GetAsync()).Paused);
    }

    [Fact]
    public void Jobs_that_change_things_ask_before_running_now()
    {
        var cut = Ctx.Render<JobsPage>();

        cut.Find($"[data-testid=job-{InboxPollJob.JobId}] [data-testid=run-now]").Click();
        Assert.Empty(_scheduler.Triggered);
        cut.Find("[data-testid=confirm-run-yes]").Click();

        Assert.Equal([InboxPollJob.JobId], _scheduler.Triggered);
        cut.WaitForElement("[data-testid=jobs-message]");
    }

    [Fact]
    public void Jobs_with_nothing_to_remove_run_straight_away()
    {
        Options.Audit.RetentionDays = 0;
        var cut = Ctx.Render<JobsPage>();

        cut.Find($"[data-testid=job-{AuditPurgeJob.JobId}] [data-testid=run-now]").Click();

        Assert.Equal([AuditPurgeJob.JobId], _scheduler.Triggered);
        Assert.Empty(cut.FindAll("[data-testid=confirm-run]"));
    }

    private sealed class FakeScheduler : IJobScheduler
    {
        public List<string> Updated { get; } = [];

        public List<string> Triggered { get; } = [];

        public void AddOrUpdateRecurring<TJob>(string jobId, string cron)
            where TJob : IRecurringJob => Updated.Add($"{jobId}={cron}");

        public void RemoveRecurring(string jobId) => throw new NotSupportedException();

        public string? TriggerRecurring(string jobId)
        {
            Triggered.Add(jobId);
            return "42";
        }
    }

    private sealed class FakeJobsReader : IRecurringJobsReader
    {
        public List<RecurringJobView> Jobs { get; } = [];

        public IReadOnlyList<RecurringJobView> List() => [.. Jobs];
    }
}

public sealed class SettingsPageTests : OpsPageTest
{
    private readonly FakeCredentials _credentials = new();

    public SettingsPageTests()
    {
        Ctx.Services.AddSingleton<IOptions<GmailOptions>>(Microsoft.Extensions.Options.Options.Create(new GmailOptions()));
        Ctx.Services.AddSingleton<IOptions<JevOptions>>(Microsoft.Extensions.Options.Options.Create(new JevOptions()));
        Ctx.Services.AddSingleton<IGmailCredentialStore>(_credentials);
    }

    private static IRenderedComponent<SettingsPage> Change(IRenderedComponent<SettingsPage> cut, string shortKey, object value)
    {
        cut.Find($"[data-testid=setting-{shortKey}] [data-testid=setting-input]").Change(value);
        return cut;
    }

    [Fact]
    public void Turning_on_trash_asks_first_and_saves_only_on_yes()
    {
        var cut = Change(Ctx.Render<SettingsPage>(), "AllowDelete", true);

        cut.Find("[data-testid=settings-save]").Click();
        Assert.Contains("Trash", cut.Find("[data-testid=confirm-settings]").TextContent, StringComparison.Ordinal);
        Assert.Empty(SavedSettings.Load());

        cut.Find("[data-testid=confirm-settings-yes]").Click();

        cut.WaitForElement("[data-testid=settings-message]");
        Assert.Equal("true", SavedSettings.Load()[RuntimeSettingsFile.AllowDelete]);
        Assert.Contains("applies after restart", cut.Find("[data-testid=setting-AllowDelete]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Plain_numbers_save_without_a_question_and_bad_ones_are_refused()
    {
        var cut = Change(Ctx.Render<SettingsPage>(), "RetentionDays", "30");

        cut.Find("[data-testid=settings-save]").Click();
        Assert.Empty(cut.FindAll("[data-testid=confirm-settings]"));
        Assert.Equal("30", SavedSettings.Load()[RuntimeSettingsFile.AuditRetentionDays]);

        Change(cut, "MaxRequestsPerHour", "0");
        cut.Find("[data-testid=settings-save]").Click();

        cut.WaitForElement("[data-testid=settings-error]");
        Assert.False(SavedSettings.Load().ContainsKey(RuntimeSettingsFile.JevMaxRequestsPerHour));
    }

    [Fact]
    public void Disconnecting_a_token_asks_first()
    {
        _credentials.States[GmailTokenKind.Primary] = GmailAuthState.Ready;
        var cut = Ctx.Render<SettingsPage>();
        Assert.Equal("connected", cut.Find("[data-testid=token-Primary] [data-testid=token-state]").TextContent.Trim());
        Assert.Empty(cut.FindAll("[data-testid=token-Filters] [data-testid=token-disconnect]"));

        cut.Find("[data-testid=token-Primary] [data-testid=token-disconnect]").Click();
        Assert.Empty(_credentials.Disconnected);
        cut.Find("[data-testid=confirm-disconnect-yes]").Click();

        Assert.Equal([GmailTokenKind.Primary], _credentials.Disconnected);
        cut.WaitForAssertion(() => Assert.Equal("not connected", cut.Find("[data-testid=token-Primary] [data-testid=token-state]").TextContent.Trim()));
    }

    private sealed class FakeCredentials : IGmailCredentialStore
    {
        public Dictionary<GmailTokenKind, GmailAuthState> States { get; } = [];

        public List<GmailTokenKind> Disconnected { get; } = [];

        public GmailCredentialStatus GetStatus(GmailTokenKind kind)
            => new(kind, States.GetValueOrDefault(kind, GmailAuthState.NotConfigured), [], null, null, null);

        public Task<string> ReplaceTokenAsync(GmailTokenKind kind, string tokenJson, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public bool Disconnect(GmailTokenKind kind)
        {
            Disconnected.Add(kind);
            return States.Remove(kind);
        }
    }
}

public sealed class CleanupPageTests : OpsPageTest
{
    private readonly FakeJobClient _jobs = new();
    private readonly string _folder;

    public CleanupPageTests()
    {
        _folder = Path.Combine(Db.Directory, "cleanup");
        Ctx.Services.AddSingleton<ICleanupConfigStore>(new FileCleanupConfigStore(_folder));
        Ctx.Services.AddTransient<ICleanupRunLog>(_ => Db.CleanupRuns());
        Ctx.Services.AddSingleton<IBackgroundJobClient>(_jobs);
        Ctx.Services.AddTransient<CleanupLauncher>();
    }

    /// <summary>The run is re-read in the background, which can be slow when the whole suite runs.</summary>
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private IRenderedComponent<CleanupPage> Render()
        => Ctx.Render<CleanupPage>(p => p.Add(c => c.PollInterval, TimeSpan.FromMilliseconds(10)));

    private void Write(string id, string json)
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, id + ".json"), json);
    }

    [Fact]
    public void A_new_file_is_built_in_the_form_and_saved_in_emailblaster_format()
    {
        var cut = Render();

        cut.Find("[data-testid=cleanup-new-id]").Change("old-promos");
        cut.Find("[data-testid=cleanup-new]").Click();
        cut.Find("[data-testid=form-rule-0]");
        cut.Find("[data-testid=form-rule-0] [data-testid=rule-name]").Change("Old promos");
        cut.Find("[data-testid=form-rule-0] [data-testid=rule-query]").Change("category:promotions older_than:30d");
        cut.Find("[data-testid=form-senders]").Change("boss@example.com, example.org");
        Assert.Equal("in:inbox is:unread (category:promotions older_than:30d) -is:starred -is:important -from:(boss@example.com OR example.org)",
            cut.Find("[data-testid=rule-preview]").TextContent);

        cut.Find("[data-testid=form-save]").Click();

        cut.WaitForElement("[data-testid=cleanup-message]");
        var saved = CleanupConfig.Parse(File.ReadAllText(Path.Combine(_folder, "old-promos.json")));
        Assert.Equal("Old promos", Assert.Single(saved.Rules).Name);
        Assert.Equal(["boss@example.com", "example.org"], saved.Keep.Senders);
    }

    [Fact]
    public void A_rule_without_a_search_is_not_saved()
    {
        var cut = Render();
        cut.Find("[data-testid=cleanup-new-id]").Change("empty");
        cut.Find("[data-testid=cleanup-new]").Click();

        cut.Find("[data-testid=form-save]").Click();

        cut.WaitForElement("[data-testid=cleanup-error]");
        Assert.False(File.Exists(Path.Combine(_folder, "empty.json")));
    }

    [Fact]
    public void Invalid_json_is_refused_with_the_reason()
    {
        Write("rules", """{"rules":[{"name":"Old","query":"older_than:1y"}]}""");
        var cut = Render();
        cut.Find("[data-testid=cleanup-file]").Change("rules");

        cut.Find("[data-testid=cleanup-json]").Change("""{"rules":[]}""");
        cut.Find("[data-testid=json-save]").Click();

        Assert.Contains("rules", cut.WaitForElement("[data-testid=cleanup-error]").TextContent, StringComparison.Ordinal);
        Assert.Contains("older_than:1y", File.ReadAllText(Path.Combine(_folder, "rules.json")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Applying_waits_for_a_finished_dry_run_and_a_yes()
    {
        Write("rules", """{"label":"cleanup_old","rules":[{"name":"Old","query":"older_than:1y"}]}""");
        var cut = Render();
        cut.Find("[data-testid=cleanup-file]").Change("rules");
        Assert.True(cut.Find("[data-testid=cleanup-apply]").HasAttribute("disabled"));

        cut.Find("[data-testid=cleanup-dry-run]").Click();
        cut.WaitForAssertion(() => Assert.Single(_jobs.Created), Wait);
        var dry = Assert.Single(await Db.CleanupRuns().ListAsync());
        Assert.True(dry.DryRun);
        await Db.CleanupRuns().CompleteAsync(dry.Id, new CleanupResult("cleanup_old", true, [new CleanupRuleResult("Old", "q", "mark read + archive", 7, 0, [])]));

        cut.WaitForAssertion(() => Assert.Contains("7 matched", cut.Find("[data-testid=cleanup-result]").TextContent, StringComparison.Ordinal), Wait);
        cut.WaitForAssertion(() => Assert.False(cut.Find("[data-testid=cleanup-apply]").HasAttribute("disabled")), Wait);

        cut.Find("[data-testid=cleanup-apply]").Click();
        Assert.Contains("matched 7 message(s)", cut.Find("[data-testid=confirm-apply]").TextContent, StringComparison.Ordinal);
        Assert.Single(_jobs.Created);
        cut.Find("[data-testid=confirm-apply-yes]").Click();

        cut.WaitForAssertion(() => Assert.Equal(2, _jobs.Created.Count), Wait);
        var runs = await Db.CleanupRuns().ListAsync();
        Assert.False(runs[0].DryRun);
        Assert.Equal(0, runs[0].SampleSize);
    }

    [Fact]
    public void Without_a_filter_file_the_filter_sync_is_not_offered()
    {
        var cut = Render();

        Assert.Empty(cut.FindAll("[data-testid=filters-dry-run]"));
    }

    private sealed class FakeJobClient : IBackgroundJobClient
    {
        public ConcurrentQueue<Job> Created { get; } = new();

        public string Create(Job job, IState state)
        {
            Created.Enqueue(job);
            return Created.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        public bool ChangeState(string jobId, IState state, string expectedState) => true;
    }
}
