using Bunit;
using Hangfire;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MudBlazor.Services;
using Nullbox.Core.Cleanup;
using Nullbox.Core.Gmail;
using Nullbox.Core.Inbox;
using Nullbox.Core.Jobs;
using Nullbox.Core.Options;
using Nullbox.Core.Pipeline;
using Nullbox.Core.Settings;
using Nullbox.Gmail.Bulk;
using Nullbox.Gmail.Filters;
using Nullbox.Tests.Inbox;
using Nullbox.Tests.Pipeline;
using Nullbox.Web.Bulk;
using Nullbox.Web.Cleanup;
using Nullbox.Web.Components.Pages;
using Nullbox.Web.Inbox;
using Nullbox.Web.Ui;
using Nullbox.Web.Ui.Audit;
using Nullbox.Web.Ui.Jobs;
using CleanupPage = Nullbox.Web.Components.Pages.Cleanup;
using JobsPage = Nullbox.Web.Components.Pages.Jobs;

namespace Nullbox.Tests.Ui.Operations;

/// <summary>The Audit, Jobs, Cleanup and Settings pages over a real migrated database and fake Gmail.</summary>
public sealed class OperationsPagesTests : IAsyncLifetime
{
    private readonly PipelineDb _db = new();
    private readonly BunitContext _ctx = new();
    private readonly FakeInboxGmail _gmail = new();
    private readonly TempStore _inbox = new();
    private readonly FakeScheduler _scheduler = new();
    private readonly RecordingJobClient _jobs = new();
    private readonly FakeCredentials _credentials = new();
    private readonly FakeFilterApi _filterApi = new();
    private readonly GmailOptions _gmailOptions = new();
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "nullbox-ops-pages", Guid.NewGuid().ToString("N"));

    public OperationsPagesTests()
    {
        Directory.CreateDirectory(_dataDir);
        var services = _ctx.Services;
        services.AddMudServices();
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        services.AddSingleton<TimeProvider>(_db.Time);
        services.AddSingleton(Options.Create(new NullboxOptions()));
        services.AddSingleton(Options.Create(_gmailOptions));
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<IGmailService>(_gmail);
        services.AddSingleton<IGmailCredentialStore>(_credentials);
        services.AddSingleton<IJobScheduler>(_scheduler);
        services.AddSingleton<IBackgroundJobClient>(_jobs);
        services.AddSingleton<IInboxStateStore>(_inbox.Store);
        services.AddTransient<IDecisionLog>(_ => _db.Log());
        services.AddTransient<IPipelineSettingsStore>(_ => _db.Log());
        services.AddTransient<IAppSettingsStore>(_ => _db.AppSettings());
        services.AddTransient<ICleanupRunLog>(_ => _db.CleanupRuns());
        services.AddSingleton<ICleanupConfigStore>(new FileCleanupConfigStore(Path.Combine(_dataDir, "cleanup")));
        services.AddSingleton(new FilterDefinitionsFile(Path.Combine(_dataDir, "filters.json")));
        services.AddSingleton<IGmailFilterApi>(_filterApi);
        services.AddTransient<GmailFilterSync>();
        services.AddTransient<BulkActions>();
        services.AddTransient<BulkJobs>();
        services.AddTransient<CleanupLauncher>();
        services.AddTransient<DecisionUndo>();
        services.AddTransient<JobControls>();
        services.AddTransient<DashboardStatusProvider>();
    }

    public Task InitializeAsync() => _db.InitializeAsync();

    public async Task DisposeAsync()
    {
        await _ctx.DisposeAsync();
        await _db.DisposeAsync();
        _inbox.Dispose();
        Directory.Delete(_dataDir, recursive: true);
    }

    // ---- Audit ---------------------------------------------------------------------------------

    [Fact]
    public async Task Audit_lists_decisions_and_filters_by_outcome()
    {
        await AuditSamples.StoreAsync(_db, AuditSamples.Applied("m1", ["archive"], [GmailLabels.Inbox], _db.Time.GetUtcNow()));
        await AuditSamples.StoreAsync(_db, AuditSamples.Applied("m2", ["archive"], [GmailLabels.Inbox], _db.Time.GetUtcNow()) with { Outcome = DecisionOutcome.DryRun });

        var cut = _ctx.Render<Audit>();
        Assert.Equal(2, cut.FindAll("[data-testid^=decision-]").Count);

        cut.Find("[data-testid=filter-outcome]").Change(nameof(DecisionOutcome.DryRun));
        cut.Find("[data-testid=search]").Click();

        var row = Assert.Single(cut.FindAll("[data-testid^=decision-]"));
        Assert.Contains("m2", row.TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=undo]")); // a dry run has nothing to undo
    }

    [Fact]
    public async Task Audit_takes_the_rule_set_filter_from_the_query_string()
    {
        await AuditSamples.StoreAsync(_db, AuditSamples.Applied("m1", ["archive"], [GmailLabels.Inbox], _db.Time.GetUtcNow()));
        await AuditSamples.StoreAsync(_db, AuditSamples.Applied("m2", ["archive"], [GmailLabels.Inbox], _db.Time.GetUtcNow()) with { RuleSetKey = "newsletters@v2" });

        _ctx.Services.GetRequiredService<NavigationManager>().NavigateTo("/audit?ruleSet=newsletters");
        var cut = _ctx.Render<Audit>();

        var row = Assert.Single(cut.FindAll("[data-testid^=decision-]"));
        Assert.Contains("m2", row.TextContent, StringComparison.Ordinal);
        Assert.Equal("newsletters", cut.Find("[data-testid=filter-rule-set]").GetAttribute("value"));
    }

    [Fact]
    public async Task Undo_needs_confirming_and_then_reverses_the_decision()
    {
        await AuditSamples.StoreAsync(_db, AuditSamples.Applied("m1", ["mark_read", "archive"], [GmailLabels.Inbox, GmailLabels.Unread], _db.Time.GetUtcNow()));
        var cut = _ctx.Render<Audit>();

        cut.Find("[data-testid=undo]").Click();
        Assert.Empty(_gmail.Changes);
        cut.Find("[data-testid=confirm-undo-no]").Click();
        Assert.Empty(cut.FindAll("[data-testid=confirm-undo]"));

        cut.Find("[data-testid=undo]").Click();
        cut.Find("[data-testid=confirm-undo-yes]").Click();

        Assert.Equal(["modify:m1:+UNREAD,INBOX:-"], _gmail.Changes);
        cut.WaitForAssertion(() => Assert.Contains("Undone on m1", cut.Find("[data-testid=message]").TextContent, StringComparison.Ordinal));
        Assert.Contains("undone", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Details_show_what_was_held_back_and_jevs_answers()
    {
        await AuditSamples.StoreAsync(_db, AuditSamples.Applied("m1", ["archive"], [GmailLabels.Inbox], _db.Time.GetUtcNow()) with
        {
            Skipped = ["delete (Bin it): delete blocked by lock"],
            AnswersJson = """{"spam":{"noul":0.97}}""",
        });
        var cut = _ctx.Render<Audit>();

        cut.Find("[data-testid=details]").Click();

        Assert.Contains("delete blocked by lock", cut.Find("[data-testid=detail-skipped]").TextContent, StringComparison.Ordinal);
        Assert.Contains("0.97", cut.Find("[data-testid=detail-answers]").TextContent, StringComparison.Ordinal);
    }

    // ---- Jobs ----------------------------------------------------------------------------------

    [Fact]
    public async Task Turning_the_kill_switch_off_needs_confirming()
    {
        var cut = _ctx.Render<JobsPage>();

        cut.Find("[data-testid=kill-switch-on]").Click();
        cut.WaitForAssertion(() => Assert.Contains("On", cut.Find("[data-testid=kill-switch-state]").TextContent, StringComparison.Ordinal));
        Assert.True(await _db.Log().GetKillSwitchAsync());

        cut.Find("[data-testid=kill-switch-off]").Click();
        Assert.True(await _db.Log().GetKillSwitchAsync());
        cut.Find("[data-testid=confirm-kill-switch-off-yes]").Click();

        cut.WaitForAssertion(() => Assert.Equal("Off", cut.Find("[data-testid=kill-switch-state]").TextContent.Trim()));
        Assert.False(await _db.Log().GetKillSwitchAsync());
    }

    [Fact]
    public async Task The_poll_schedule_pause_and_run_now_work_from_the_page()
    {
        var cut = _ctx.Render<JobsPage>();

        cut.Find("[data-testid=poll-cron]").Input("not a cron");
        cut.Find("[data-testid=poll-cron-save]").Click();
        cut.WaitForAssertion(() => Assert.Contains("not a valid cron", cut.Find("[data-testid=message]").TextContent, StringComparison.Ordinal));

        cut.Find("[data-testid=poll-cron]").Input("*/10 * * * *");
        cut.Find("[data-testid=poll-cron-save]").Click();
        cut.WaitForAssertion(() => Assert.Equal("*/10 * * * *", _scheduler.Crons[InboxPollJob.JobId]));

        cut.Find("[data-testid=poll-pause]").Click();
        cut.WaitForAssertion(() => Assert.Equal("Paused", cut.Find("[data-testid=poll-state]").TextContent.Trim()));
        Assert.True((await _inbox.Store.GetAsync()).Paused);

        cut.Find("[data-testid=poll-run-now]").Click();
        Assert.Equal([InboxPollJob.JobId], _scheduler.Triggered);
    }

    // ---- Cleanup & filters ---------------------------------------------------------------------

    [Fact]
    public void A_new_cleanup_file_is_built_in_the_form_and_saved_in_emailblaster_format()
    {
        var cut = _ctx.Render<CleanupPage>();

        cut.Find("[data-testid=new-file]").Click();
        cut.Find("[data-testid=file-id]").Input("old_news");
        cut.Find("[data-testid=label]").Input("cleanup_news");
        cut.Find("[data-testid=rule-0] [data-testid=rule-query]").Input("category:updates older_than:6m");
        cut.Find("[data-testid=save]").Click();

        var saved = CleanupConfig.Parse(File.ReadAllText(Path.Combine(_dataDir, "cleanup", "old_news.json")));
        Assert.Equal("cleanup_news", saved.Label);
        Assert.Equal("category:updates older_than:6m", Assert.Single(saved.Rules).Query);
        Assert.NotNull(cut.Find("[data-testid=file-old_news]"));
    }

    [Fact]
    public void Editing_the_json_updates_the_form_and_bad_json_is_refused()
    {
        var cut = _ctx.Render<CleanupPage>();
        cut.Find("[data-testid=new-file]").Click();

        cut.Find("[data-testid=json]").Change("""{"rules":[{"name":"All read","query":"is:read","unread_only":false}]}""");
        Assert.Equal("All read", cut.Find("[data-testid=rule-0] [data-testid=rule-name]").GetAttribute("value"));

        cut.Find("[data-testid=json]").Change("""{"rules":[]}""");
        Assert.Contains("Not loaded", cut.Find("[data-testid=json-problem]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Applying_a_cleanup_needs_confirming_and_a_dry_run_does_not()
    {
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(_dataDir, "cleanup")).FullName, "rules.json"),
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Cleanup", "cleanup_rules.example.json")));
        var cut = _ctx.Render<CleanupPage>();
        cut.Find("[data-testid=file-rules]").Click();

        cut.Find("[data-testid=dry-run]").Click();
        var dry = Assert.Single(await _db.CleanupRuns().ListAsync());
        Assert.True(dry.DryRun);
        await _db.CleanupRuns().CompleteAsync(dry.Id, new CleanupResult("cleanup_unread", true, [new CleanupRuleResult("Old promos", "q", "mark read + archive", 12, 0, [])]));
        cut.Find("[data-testid=refresh-runs]").Click();

        cut.Find("[data-testid=apply]").Click();
        Assert.Single(await _db.CleanupRuns().ListAsync());
        cut.WaitForAssertion(() => Assert.Contains("matched 12 messages", cut.Find("[data-testid=confirm-apply]").TextContent, StringComparison.Ordinal));
        cut.Find("[data-testid=confirm-apply-yes]").Click();

        var runs = await _db.CleanupRuns().ListAsync();
        Assert.Equal([false, true], runs.Select(r => r.DryRun));
        Assert.Equal(2, _jobs.Jobs.Count);
    }

    [Fact]
    public void Creating_filters_needs_a_dry_run_then_confirming()
    {
        File.WriteAllText(Path.Combine(_dataDir, "filters.json"), """{"filters":[{"label":"News","query":"from:(a.example)"}]}""");
        _gmailOptions.AllowFilterSync = true;
        var cut = _ctx.Render<CleanupPage>();

        Assert.True(cut.Find("[data-testid=filters-apply]").HasAttribute("disabled"));
        cut.Find("[data-testid=filters-dry-run]").Click();
        cut.WaitForAssertion(() => Assert.Contains("would create", cut.Find("[data-testid=filter-results]").TextContent, StringComparison.Ordinal));
        Assert.Equal(0, _filterApi.Created);

        cut.Find("[data-testid=filters-apply]").Click();
        cut.Find("[data-testid=confirm-filters-yes]").Click();

        cut.WaitForAssertion(() => Assert.Equal(1, _filterApi.Created));
    }

    // ---- Settings ------------------------------------------------------------------------------

    [Fact]
    public void Disconnecting_a_token_needs_confirming()
    {
        var cut = _ctx.Render<Settings>();
        Assert.Contains("Connected", cut.Find("[data-testid=token-Primary] [data-testid=token-state]").TextContent, StringComparison.Ordinal);

        cut.Find("[data-testid=token-Primary] [data-testid=disconnect]").Click();
        Assert.Empty(_credentials.Removed);
        cut.Find("[data-testid=confirm-disconnect-no]").Click();
        Assert.Empty(_credentials.Removed);

        cut.Find("[data-testid=token-Primary] [data-testid=disconnect]").Click();
        cut.Find("[data-testid=confirm-disconnect-yes]").Click();

        Assert.Equal([GmailTokenKind.Primary], _credentials.Removed);
    }

    [Fact]
    public void Safety_switches_are_shown_but_not_editable()
    {
        _gmailOptions.AllowDelete = true;

        var cut = _ctx.Render<Settings>();

        Assert.Contains("lifted", cut.Find("[data-testid=flag-delete]").TextContent, StringComparison.Ordinal);
        Assert.Contains("off", cut.Find("[data-testid=flag-permanent]").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=safety] input, [data-testid=safety] button"));
    }

    private sealed class FakeCredentials : IGmailCredentialStore
    {
        public List<GmailTokenKind> Removed { get; } = [];

        public GmailCredentialStatus GetStatus(GmailTokenKind kind) => Removed.Contains(kind)
            ? new GmailCredentialStatus(kind, GmailAuthState.NotConfigured, [], null, null, null)
            : new GmailCredentialStatus(kind, GmailAuthState.Ready, ["https://mail.google.com/"], null, null, null);

        public Task<string> ReplaceTokenAsync(GmailTokenKind kind, string tokenJson, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public bool RemoveToken(GmailTokenKind kind)
        {
            Removed.Add(kind);
            return true;
        }
    }

    private sealed class FakeFilterApi : IGmailFilterApi
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
