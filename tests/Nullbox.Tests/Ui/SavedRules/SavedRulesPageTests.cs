using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Nullbox.Core.Pipeline;
using Nullbox.Core.Rules;
using Nullbox.Core.SavedRules;
using Nullbox.Tests.Pipeline;
using Nullbox.Web.Components.Pages;
using Nullbox.Web.Ui.Rules;

namespace Nullbox.Tests.Ui.SavedRules;

/// <summary>The Saved rules page over a real migrated store.</summary>
public sealed class SavedRulesPageTests : IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly PipelineDb _db = new();
    private readonly BunitContext _ctx = new();

    public SavedRulesPageTests()
    {
        _ctx.Services.AddMudServices();
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        _ctx.Services.AddSingleton<TimeProvider>(_db.Time);
        _ctx.Services.AddTransient<ISavedRuleStore>(_ => _db.SavedRules());
        _ctx.Services.AddTransient<IDecisionLog>(_ => _db.Log());
        _ctx.Services.AddTransient<IRuleSetStore>(_ => _db.RuleSets());
        _ctx.Services.AddTransient<RuleSetCatalog>();
    }

    public Task InitializeAsync() => _db.InitializeAsync();

    public async Task DisposeAsync()
    {
        await _ctx.DisposeAsync();
        await _db.DisposeAsync();
    }

    private static SavedRule Shop => new() { Name = "shop", From = "@shop.example", Actions = ["label:shop"], Remove = RemoveMode.Archive };

    private async Task<SavedRule?> StoredAsync(string name) => (await _db.SavedRules().GetAsync(name))?.Rule;

    [Fact]
    public async Task A_new_rule_is_built_in_the_form_and_saved_as_a_dry_run()
    {
        var cut = _ctx.Render<SavedRulesPage>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=empty]"), Wait);

        cut.Find("[data-testid=new]").Click();
        cut.Find("[data-testid=name]").Change("news");
        cut.Find("[data-testid=to]").Change("me+news@gmail.com");
        cut.Find("[data-testid=subject]").Change("weekly");
        cut.Find("[data-testid=labels]").Change("news, reading");
        cut.Find("[data-testid=mark-read] input, input[data-testid=mark-read]").Change(true);
        cut.Find("[data-testid=save]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=row-news]"), Wait);
        var saved = await StoredAsync("news");
        Assert.NotNull(saved);
        Assert.Equal((RuleSetMode.DryRun, true, RemoveMode.Archive, "weekly"), (saved.Mode, saved.Stop, saved.Remove, saved.SubjectContains));
        Assert.Equal(["label:news", "label:reading", "mark_read"], saved.Actions);
        Assert.Contains("dry run", cut.Find("[data-testid=message]").TextContent, StringComparison.Ordinal);
        Assert.Contains("to me+news@gmail.com", cut.Find("[data-testid=row-news] [data-testid=match]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_input_is_listed_and_nothing_is_saved()
    {
        var cut = _ctx.Render<SavedRulesPage>();
        cut.Find("[data-testid=new]").Click();
        cut.Find("[data-testid=name]").Change("bad");
        cut.Find("[data-testid=from]").Change("shop.example");
        cut.Find("[data-testid=save]").Click();

        cut.WaitForAssertion(() => Assert.Contains("/from", cut.Find("[data-testid=errors]").TextContent, StringComparison.Ordinal), Wait);
        Assert.Null(await StoredAsync("bad"));
    }

    [Fact]
    public async Task Editing_a_rule_keeps_its_name_and_can_make_it_live()
    {
        await _db.SavedRules().SaveAsync(Shop);
        var cut = _ctx.Render<SavedRulesPage>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=edit-shop]"), Wait);

        cut.Find("[data-testid=edit-shop]").Click();
        Assert.Equal("@shop.example", cut.Find("[data-testid=from]").GetAttribute("value"));
        cut.Find("[data-testid=from]").Change("deals@shop.example");
        cut.Find("[data-testid=mode-live]").Click();
        cut.Find("[data-testid=save]").Click();

        cut.WaitForAssertion(() => Assert.Equal("live", cut.Find("[data-testid=row-shop] [data-testid=mode]").TextContent.Trim()), Wait);
        var saved = await StoredAsync("shop");
        Assert.Equal(("deals@shop.example", RuleSetMode.Live), (saved!.From, saved.Mode));
    }

    [Fact]
    public async Task A_rule_can_be_turned_off_from_the_list()
    {
        await _db.SavedRules().SaveAsync(Shop);
        var cut = _ctx.Render<SavedRulesPage>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=enabled-shop] input, input[data-testid=enabled-shop]"), Wait);

        cut.Find("[data-testid=enabled-shop] input, input[data-testid=enabled-shop]").Change(false);

        cut.WaitForAssertion(() => Assert.Contains("Turned shop off", cut.Find("[data-testid=message]").TextContent, StringComparison.Ordinal), Wait);
        Assert.False((await StoredAsync("shop"))!.Enabled);
    }

    [Fact]
    public async Task Switching_to_delete_asks_first()
    {
        await _db.SavedRules().SaveAsync(Shop);
        var cut = _ctx.Render<SavedRulesPage>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=edit-shop]"), Wait);
        cut.Find("[data-testid=edit-shop]").Click();

        cut.Find("[data-testid=remove-delete]").Click();
        var confirm = cut.Find("[data-testid=confirm-delete]");
        Assert.Contains("Trash", confirm.TextContent, StringComparison.Ordinal);
        Assert.Contains("delete lock is on", confirm.TextContent, StringComparison.Ordinal);
        Assert.Equal("archives", cut.Find("[data-testid=remove-state]").TextContent.Trim());

        cut.Find("[data-testid=confirm-delete-no]").Click();
        Assert.Empty(cut.FindAll("[data-testid=confirm-delete]"));
        Assert.Equal("archives", cut.Find("[data-testid=remove-state]").TextContent.Trim());

        cut.Find("[data-testid=remove-delete]").Click();
        cut.Find("[data-testid=confirm-delete-yes]").Click();
        Assert.Equal("deletes", cut.Find("[data-testid=remove-state]").TextContent.Trim());
        cut.Find("[data-testid=save]").Click();

        cut.WaitForAssertion(() => Assert.Contains("delete", cut.Find("[data-testid=row-shop] [data-testid=action]").TextContent, StringComparison.Ordinal), Wait);
        Assert.Equal(RemoveMode.Delete, (await StoredAsync("shop"))!.Remove);
    }

    [Fact]
    public async Task Turning_delete_on_through_the_json_view_also_asks_first()
    {
        await _db.SavedRules().SaveAsync(Shop);
        var cut = _ctx.Render<SavedRulesPage>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=edit-shop]"), Wait);
        cut.Find("[data-testid=edit-shop]").Click();

        cut.FindAll(".mud-tab").Single(t => t.TextContent.Trim() == "JSON").Click();
        var json = SavedRuleJson.Serialize(Shop with { Remove = RemoveMode.Delete });
        cut.Find("[data-testid=json]").Change(json);
        cut.Find("[data-testid=json-apply]").Click();

        cut.Find("[data-testid=confirm-delete]");
        cut.FindAll(".mud-tab").Single(t => t.TextContent.Trim() == "Form").Click();
        Assert.Equal("archives", cut.Find("[data-testid=remove-state]").TextContent.Trim());
    }

    [Fact]
    public async Task The_test_box_shows_the_rules_a_message_matches_in_order()
    {
        await _db.SavedRules().SaveAsync(Shop with { Name = "label-shop", Mode = RuleSetMode.Live, Stop = false, Remove = null });
        await _db.SavedRules().SaveAsync(Shop with { Name = "settle", Mode = RuleSetMode.Live });
        await _db.SavedRules().SaveAsync(Shop with { Name = "late" });
        await _db.SavedRules().SaveAsync(Shop with { Name = "off", Enabled = false });
        await _db.SavedRules().SaveAsync(Shop with { Name = "elsewhere", From = "@other.example" });
        var cut = _ctx.Render<SavedRulesPage>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=row-elsewhere]"), Wait);

        cut.Find("[data-testid=test-from]").Input("Shop <deals@shop.example>");

        cut.WaitForAssertion(() => cut.Find("[data-testid=test-results]"), Wait);
        Assert.Equal(
            ["test-label-shop", "test-settle", "test-late", "test-off"],
            cut.FindAll("[data-testid=test-results] li").Select(li => li.GetAttribute("data-testid")));
        Assert.Contains("Jev rule sets are skipped", cut.Find("[data-testid=test-settle]").TextContent, StringComparison.Ordinal);
        Assert.Contains("earlier rule stops first", cut.Find("[data-testid=test-late]").TextContent, StringComparison.Ordinal);
        Assert.Contains("it's off", cut.Find("[data-testid=test-off]").TextContent, StringComparison.Ordinal);
        Assert.Equal("/audit?ruleSet=saved%3Asettle", cut.Find("[data-testid=test-settle] a").GetAttribute("href"));

        cut.Find("[data-testid=test-from]").Input("someone@else.example");
        cut.WaitForAssertion(() => cut.Find("[data-testid=test-none]"), Wait);
    }

    [Fact]
    public async Task Rules_can_be_reordered_and_removed_after_a_confirmation()
    {
        await _db.SavedRules().SaveAsync(Shop with { Name = "a" });
        await _db.SavedRules().SaveAsync(Shop with { Name = "b" });
        var cut = _ctx.Render<SavedRulesPage>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=up-b]"), Wait);

        cut.Find("[data-testid=up-b]").Click();
        cut.WaitForAssertion(() => Assert.Equal(["row-b", "row-a"], cut.FindAll("[data-testid=saved-rules] tbody tr").Select(r => r.GetAttribute("data-testid"))), Wait);

        cut.Find("[data-testid=remove-a]").Click();
        cut.Find("[data-testid=confirm-remove-yes]").Click();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=row-a]")), Wait);
        Assert.Null(await StoredAsync("a"));
    }
}
