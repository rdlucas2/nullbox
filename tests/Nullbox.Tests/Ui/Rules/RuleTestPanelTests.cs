using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MudBlazor.Services;
using Nullbox.Core.Gmail;
using Nullbox.Core.Jev;
using Nullbox.Core.Options;
using Nullbox.Core.Rules;
using Nullbox.Jev.Testing;
using Nullbox.Tests.Inbox;
using Nullbox.Tests.Pipeline;
using Nullbox.Web.Components.Rules;
using Nullbox.Web.Pipeline;
using TesterPage = Nullbox.Web.Components.Pages.RuleTester;

namespace Nullbox.Tests.Ui.Rules;

/// <summary>The tester panel and the /test page with a scripted Gmail and Jev.</summary>
public sealed class RuleTestPanelTests : IAsyncLifetime
{
    private readonly PipelineDb _db = new();
    private readonly BunitContext _ctx = new();
    private readonly FakeInboxGmail _gmail = new();
    private readonly FakeJevClient _jev = new(_ => Answers.ConfidentPromo);
    private readonly FixedJevBudget _budget = new(new JevBudgetStatus(0, 1_000_000, 0, 100));

    public RuleTestPanelTests()
    {
        _ctx.Services.AddMudServices();
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        _ctx.Services.AddSingleton<TimeProvider>(_db.Time);
        _ctx.Services.AddTransient<IRuleSetStore>(_ => _db.RuleSets());
        _ctx.Services.AddTransient<RuleTester>(_ => new RuleTester(
            new GmailReader(_gmail), _jev, _db.RuleSets(), _db.Log(), Options.Create(new NullboxOptions()), _db.Time, budget: _budget));
    }

    public Task InitializeAsync() => _db.InitializeAsync();

    public async Task DisposeAsync()
    {
        await _ctx.DisposeAsync();
        await _db.DisposeAsync();
    }

    private static RuleSetDocument Promo => RuleSetSeeds.Load(RuleSetSeeds.PromoCleanup);

    private void Inbox(params string[] ids)
    {
        foreach (var id in ids)
        {
            _gmail.Add(id);
            _gmail.InboxSearchResults.Add(id);
        }
    }

    [Fact]
    public void A_draft_is_checked_then_run_and_its_verdicts_shown_without_gmail_changes()
    {
        Inbox("m1", "m2");
        var cut = _ctx.Render<RuleTestPanel>(p => p.Add(c => c.Draft, Promo));

        cut.Find("[data-testid=test-prepare]").Click();
        cut.WaitForElement("[data-testid=test-run]");
        Assert.Contains("2 message(s), at most 2 Jev call(s)", cut.Find("[data-testid=test-plan]").TextContent, StringComparison.Ordinal);
        Assert.Empty(_jev.Calls);

        cut.Find("[data-testid=test-run]").Click();
        cut.WaitForElement("[data-testid=test-results]");

        Assert.Contains("2 message(s) tested, 2 Jev call(s)", cut.Find("[data-testid=test-summary]").TextContent, StringComparison.Ordinal);
        var row = cut.Find("[data-testid=test-row-m1]");
        Assert.Contains("Archive confident promos", row.QuerySelector("[data-testid=test-decisions]")!.TextContent, StringComparison.Ordinal);
        Assert.Contains("archive", row.QuerySelector("[data-testid=test-actions]")!.TextContent, StringComparison.Ordinal);
        Assert.Contains("promo (95%)", row.QuerySelector("[data-testid=test-answers]")!.TextContent, StringComparison.Ordinal);
        Assert.Empty(_gmail.Changes);
    }

    [Fact]
    public async Task Changes_against_the_active_version_are_highlighted()
    {
        Inbox("m1");
        await _db.ActivateAsync(Promo);
        var labelOnly = Promo with
        {
            Decisions = [new DecisionRule { Name = "Only label", When = "category.choice == \"promo\"", Actions = ["label:nullbox/promo"] }],
        };
        var cut = _ctx.Render<RuleTestPanel>(p => p.Add(c => c.Draft, labelOnly));

        cut.Find("[data-testid=test-prepare]").Click();
        cut.WaitForElement("[data-testid=test-run]").Click();
        cut.WaitForElement("[data-testid=test-results]");

        Assert.Equal("true", cut.Find("[data-testid=test-row-m1]").GetAttribute("data-changed"));
        Assert.Contains("1 differ from v1", cut.Find("[data-testid=test-summary]").TextContent, StringComparison.Ordinal);
        Assert.Contains("archive", cut.Find("[data-testid=test-active]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void An_over_budget_batch_shows_why_and_offers_no_run()
    {
        Inbox("m1");
        _budget.Status = new JevBudgetStatus(1_000_000, 1_000_000, 0, 100);
        var cut = _ctx.Render<RuleTestPanel>(p => p.Add(c => c.Draft, Promo));

        cut.Find("[data-testid=test-prepare]").Click();
        cut.WaitForElement("[data-testid=test-blocked]");

        Assert.Empty(cut.FindAll("[data-testid=test-run]"));
        Assert.Empty(_jev.Calls);
    }

    [Fact]
    public void An_invalid_draft_cannot_be_tested()
    {
        var cut = _ctx.Render<RuleTestPanel>(p => p.Add(c => c.Draft, (RuleSetDocument?)null));

        Assert.Contains("Fix the rule set's errors", cut.Find("[data-testid=test-unavailable]").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=test-prepare]"));
    }

    [Fact]
    public async Task The_test_page_loads_the_newest_version_of_the_chosen_rule_set()
    {
        Assert.NotEmpty(_ctx.Render<TesterPage>().FindAll("[data-testid=test-no-rule-sets]"));

        await _db.ActivateAsync(Promo);
        await _db.RuleSets().SaveAsync(Promo with { Description = "Draft v2" });
        var cut = _ctx.Render<TesterPage>();

        cut.WaitForAssertion(() => Assert.Contains("v2", cut.Find("[data-testid=test-version]").TextContent, StringComparison.Ordinal));
        Assert.NotEmpty(cut.FindAll("[data-testid=test-prepare]"));
    }
}
