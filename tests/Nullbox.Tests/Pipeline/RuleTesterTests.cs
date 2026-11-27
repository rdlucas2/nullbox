using Microsoft.Extensions.Options;
using Nullbox.Core.Gmail;
using Nullbox.Core.Jev;
using Nullbox.Core.Options;
using Nullbox.Core.Pipeline;
using Nullbox.Core.Rules;
using Nullbox.Jev.Testing;
using Nullbox.Tests.Inbox;
using Nullbox.Web.Pipeline;

namespace Nullbox.Tests.Pipeline;

public sealed class FixedJevBudget(JevBudgetStatus status) : IJevBudget
{
    public JevBudgetStatus Status { get; set; } = status;

    public Task<JevBudgetStatus> GetStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(Status);
}

public sealed class RuleTesterTests : IAsyncLifetime
{
    private readonly PipelineDb _db = new();
    private readonly FakeInboxGmail _gmail = new();
    private readonly FakeJevClient _jev = new(_ => Answers.ConfidentPromo);
    private readonly FixedJevBudget _budget = new(new JevBudgetStatus(0, 1_000_000, 0, 100));
    private readonly NullboxOptions _options = new();

    public Task InitializeAsync() => _db.InitializeAsync();

    public Task DisposeAsync() => _db.DisposeAsync();

    private RuleTester Tester() => new(new GmailReader(_gmail), _jev, _db.RuleSets(), _db.Log(), Options.Create(_options), _db.Time, budget: _budget);

    private static RuleSetDocument Promo => RuleSetSeeds.Load(RuleSetSeeds.PromoCleanup);

    /// <summary>Promo-cleanup with a confidence bar the scripted answers miss, so it only labels.</summary>
    private static RuleSetDocument Stricter => Promo with
    {
        Decisions = [.. Promo.Decisions.Select((d, i) => i == 1 ? d with { When = d.When.Replace("0.85", "0.99", StringComparison.Ordinal) } : d)],
    };

    private void Inbox(params string[] ids)
    {
        foreach (var id in ids)
        {
            _gmail.Add(id);
            _gmail.InboxSearchResults.Add(id);
        }
    }

    [Fact]
    public void The_tester_can_only_read_gmail()
    {
        var parameters = typeof(RuleTester).GetConstructors().Single().GetParameters().Select(p => p.ParameterType).ToList();

        Assert.Contains(typeof(IGmailReader), parameters);
        Assert.DoesNotContain(parameters, t => typeof(IGmailService).IsAssignableFrom(t));
        Assert.False(typeof(IGmailService).IsAssignableFrom(typeof(GmailReader)));
    }

    [Fact]
    public async Task A_draft_runs_over_the_inbox_in_dry_run_without_touching_gmail_or_the_log()
    {
        Inbox("m1", "m2");

        var tester = Tester();
        var plan = await tester.PrepareAsync(Promo, new TestSelection { CompareWithActive = false });
        Assert.Null(plan.Blocked);
        Assert.Equal((2, 2), (plan.Messages.Count, plan.JevCalls));
        Assert.Empty(_jev.Calls);

        var run = await tester.RunAsync(plan);

        var row = run.Rows[0];
        Assert.Equal(["Archive confident promos"], row.Decisions);
        Assert.Equal(["label:nullbox/promo", "label:nullbox/review/promo-cleanup/archive-confident-promos", "mark_read", "archive"], row.Actions);
        Assert.Contains(row.Answers, a => a is { Question: "category", Value: "promo (95%)" });
        Assert.Contains("\"subject\"", row.State, StringComparison.Ordinal);
        Assert.Equal(2, run.JevCalls);
        Assert.All(_jev.Calls, c => Assert.StartsWith("test:promo-cleanup#", c.Context!.RuleSetVersion, StringComparison.Ordinal));
        Assert.Empty(_gmail.Changes);
        Assert.Empty(await _db.Log().ListAsync());
    }

    [Fact]
    public async Task Compared_with_the_active_version_it_reuses_recorded_verdicts_and_flags_changes()
    {
        Inbox("m1", "m2");
        var active = await _db.ActivateAsync(Promo);
        await _db.Log().RecordAsync(new DecisionRecord(
            "m1", active.Key, RuleSetMode.DryRun, DecisionOutcome.DryRun, ["Archive confident promos"],
            ["label:nullbox/promo", "label:nullbox/review/promo-cleanup/archive-confident-promos", "mark_read", "archive"],
            [], "jev-fake", "{}", _db.Time.GetUtcNow()));

        var tester = Tester();
        var plan = await tester.PrepareAsync(Stricter, new TestSelection());
        Assert.Equal(active.Key, plan.Active?.Key);
        Assert.Equal(["m1"], plan.Recorded.Keys);
        Assert.Equal(3, plan.JevCalls);

        var run = await tester.RunAsync(plan);

        Assert.Equal(2, run.Changed);
        Assert.All(run.Rows, r => Assert.Equal(["label:nullbox/promo"], r.Actions));
        Assert.Equal("recorded by v1", run.Rows.Single(r => r.MessageId == "m1").ActiveSource);
        Assert.Equal("v1, asked now", run.Rows.Single(r => r.MessageId == "m2").ActiveSource);
        Assert.Equal(active.Key, _jev.Calls.Single(c => !c.Context!.RuleSetVersion.StartsWith("test:", StringComparison.Ordinal)).Context!.RuleSetVersion);
        Assert.Empty(_gmail.Changes);
        Assert.Single(await _db.Log().ListAsync());
    }

    [Fact]
    public async Task Batches_are_capped()
    {
        Inbox([.. Enumerable.Range(1, 40).Select(i => $"m{i}")]);

        var plan = await Tester().PrepareAsync(Promo, new TestSelection { Count = 500, CompareWithActive = false });

        Assert.Equal(25, plan.Messages.Count);
        Assert.Equal(25, Tester().MaxMessages);
    }

    [Fact]
    public async Task A_batch_over_the_jev_budget_is_refused_before_any_call()
    {
        Inbox("m1", "m2");
        _budget.Status = new JevBudgetStatus(999_990, 1_000_000, 0, 100);

        var tester = Tester();
        var plan = await tester.PrepareAsync(Promo, new TestSelection());

        Assert.Contains("left in today's Jev budget", plan.Blocked, StringComparison.Ordinal);
        await Assert.ThrowsAsync<InvalidOperationException>(() => tester.RunAsync(plan));
        Assert.Empty(_jev.Calls);

        _budget.Status = new JevBudgetStatus(0, 1_000_000, 99, 100);
        Assert.Contains("left this hour", (await tester.PrepareAsync(Promo, new TestSelection())).Blocked, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Running_out_of_budget_part_way_stops_the_run()
    {
        Inbox("m1", "m2");
        var jev = new ThrowingAfter(_jev, 1);
        var tester = new RuleTester(new GmailReader(_gmail), jev, _db.RuleSets(), _db.Log(), Options.Create(_options), _db.Time, budget: _budget);

        var run = await tester.RunAsync(await tester.PrepareAsync(Promo, new TestSelection { CompareWithActive = false }));

        Assert.Single(run.Rows);
        Assert.Contains("Stopped after 1 of 2", run.Stopped, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_and_the_decision_log_pick_the_messages()
    {
        _gmail.Add("s1");
        _gmail.Add("a1");
        _gmail.Search = query => query == "from:shop" ? ["s1"] : [];
        var tester = Tester();

        Assert.Equal("Enter a Gmail search.", (await tester.PrepareAsync(Promo, new TestSelection { Source = TestSource.Search })).Blocked);
        var searched = await tester.PrepareAsync(Promo, new TestSelection { Source = TestSource.Search, Query = "from:shop" });
        Assert.Equal(["s1"], searched.Messages.Select(m => m.Id));

        Assert.Contains("no messages", (await tester.PrepareAsync(Promo, new TestSelection { Source = TestSource.Audit })).Blocked, StringComparison.Ordinal);
        await _db.Log().RecordAsync(new DecisionRecord("a1", "promo-cleanup@v1", RuleSetMode.DryRun, DecisionOutcome.DryRun, [], [], [], "jev-fake", "{}", _db.Time.GetUtcNow()));
        var sampled = await tester.PrepareAsync(Promo, new TestSelection { Source = TestSource.Audit });
        Assert.Equal(["a1"], sampled.Messages.Select(m => m.Id));
    }

    [Fact]
    public async Task Gmail_errors_block_the_run_with_the_reason()
    {
        _gmail.InboxSearchResults.Add("gone");

        var plan = await Tester().PrepareAsync(Promo, new TestSelection());

        Assert.StartsWith("Couldn't read Gmail:", plan.Blocked, StringComparison.Ordinal);
    }

    private sealed class ThrowingAfter(IJevClient inner, int allowed) : IJevClient
    {
        private int _calls;

        public Task<JevResult> EvaluateAsync(JevRequest request, JevCallContext? context = null, CancellationToken cancellationToken = default)
            => ++_calls > allowed
                ? throw new JevBudgetExceededException("Daily Jev budget is used up", DateTimeOffset.UnixEpoch)
                : inner.EvaluateAsync(request, context, cancellationToken);
    }
}
