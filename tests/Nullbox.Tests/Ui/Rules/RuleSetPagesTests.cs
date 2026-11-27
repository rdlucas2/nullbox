using System.Text.Json.Nodes;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Nullbox.Core.Rules;
using Nullbox.Tests.Pipeline;
using Nullbox.Web.Components.Pages;
using Nullbox.Web.Ui.Rules;

namespace Nullbox.Tests.Ui.Rules;

/// <summary>The rule-set pages over a real migrated store.</summary>
public sealed class RuleSetPagesTests : IAsyncLifetime
{
    private readonly PipelineDb _db = new();
    private readonly BunitContext _ctx = new();

    public RuleSetPagesTests()
    {
        _ctx.Services.AddMudServices();
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        _ctx.Services.AddSingleton<TimeProvider>(_db.Time);
        _ctx.Services.AddTransient<IRuleSetStore>(_ => _db.RuleSets());
        _ctx.Services.AddTransient<RuleSetCatalog>();
    }

    public Task InitializeAsync() => _db.InitializeAsync();

    public async Task DisposeAsync()
    {
        await _ctx.DisposeAsync();
        await _db.DisposeAsync();
    }

    private IRenderedComponent<RuleSetEditor> Editor(string name)
        => _ctx.Render<RuleSetEditor>(p => p.Add(c => c.Name, name));

    private static string Json(IRenderedComponent<RuleSetEditor> cut) => cut.Find("[data-testid=json]").GetAttribute("value") ?? string.Empty;

    private static bool Disabled(IRenderedComponent<RuleSetEditor> cut, string testId) => cut.Find($"[data-testid={testId}]").HasAttribute("disabled");

    [Fact]
    public async Task A_rule_set_built_from_scratch_produces_the_expected_json_and_saves_as_v1()
    {
        var cut = _ctx.Render<RuleSetEditor>();

        cut.Find("[data-testid=name]").Input("newsletters");
        cut.Find("[data-testid=description]").Input("Archives newsletters.");
        cut.Find("[data-testid=field-snippet] input").Change(false);
        cut.Find("[data-testid=add-noul]").Click();
        cut.Find("[data-testid=question-noul_1] [data-testid=question-instructions]").Change("This is a newsletter the recipient subscribed to.");
        cut.Find("[data-testid=question-noul_1] [data-testid=question-name]").Change("newsletter");
        cut.Find("[data-testid=add-decision]").Click();
        cut.Find("[data-testid=decision-0] [data-testid=decision-name]").Change("Archive newsletters");
        cut.Find("[data-testid=decision-0] [data-testid=clause-question]").Change("newsletter");
        cut.Find("[data-testid=decision-0] [data-testid=clause-field]").Change("noul");
        cut.Find("[data-testid=decision-0] [data-testid=clause-operator]").Change(">=");
        cut.Find("[data-testid=decision-0] [data-testid=clause-value]").Change("0.8");
        cut.Find("[data-testid=decision-0] [data-testid=add-clause]").Click();
        cut.Find("[data-testid=decision-0] [data-testid=threshold]").Change("0.9");
        cut.Find("[data-testid=decision-0] [data-testid=decision-actions]").Change("label:nullbox/newsletter, mark_read");

        var expected = JsonNode.Parse("""
            {
              "$schema": "/schemas/ruleset.schema.json",
              "name": "newsletters",
              "description": "Archives newsletters.",
              "mode": "dry_run",
              "model": "jev-latest",
              "state": { "fields": ["from", "subject"], "body_max_chars": 2000 },
              "questions": {
                "newsletter": { "type": "noul", "instructions": "This is a newsletter the recipient subscribed to." }
              },
              "match": "first",
              "decisions": [
                {
                  "name": "Archive newsletters",
                  "when": "newsletter.noul >= 0.9",
                  "actions": ["label:nullbox/newsletter", "mark_read"],
                  "remove": "archive"
                }
              ]
            }
            """);
        Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(Json(cut))), Json(cut));
        Assert.Contains("\"newsletter.noul >= 0.9\"", Json(cut), StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=validation-errors]"));
        Assert.Contains("\"newsletter\"", cut.Find("[data-testid=request-preview]").TextContent, StringComparison.Ordinal);

        cut.Find("[data-testid=save]").Click();

        var saved = await _db.RuleSets().GetVersionAsync("newsletters", 1);
        Assert.NotNull(saved);
        Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(saved.Json)));
        Assert.False(saved.IsActive);
        Assert.EndsWith("/rules/newsletters", _ctx.Services.GetRequiredService<NavigationManager>().Uri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Json_edits_show_up_in_the_tabs_once_applied_and_bad_json_is_refused()
    {
        await _db.RuleSets().SaveAsync(RuleSetSeeds.Load(RuleSetSeeds.PromoCleanup));
        var cut = Editor(RuleSetSeeds.PromoCleanup);
        Assert.True(cut.Find("[data-testid=name]").HasAttribute("readonly"));

        var edited = JsonNode.Parse(Json(cut))!.AsObject();
        edited["questions"]!.AsObject()["urgent"] = new JsonObject { ["type"] = "noul", ["instructions"] = "The sender needs an answer today." };
        edited["decisions"]![1]!["name"] = "Archive sure promos";
        cut.Find("[data-testid=json]").Change(edited.ToJsonString());
        Assert.True(Disabled(cut, "save"));
        cut.Find("[data-testid=apply-json]").Click();

        Assert.NotNull(cut.Find("[data-testid=question-urgent]"));
        Assert.Equal("Archive sure promos", cut.Find("[data-testid=decision-1] [data-testid=decision-name]").GetAttribute("value"));
        Assert.NotNull(cut.Find("[data-testid=unsaved]"));
        Assert.False(Disabled(cut, "save"));

        cut.Find("[data-testid=json]").Change("""{ "name": "promo-cleanup" }""");
        cut.Find("[data-testid=apply-json]").Click();

        Assert.NotNull(cut.Find("[data-testid=json-errors]"));
        Assert.NotNull(cut.Find("[data-testid=question-urgent]"));

        cut.Find("[data-testid=json]").Change(RuleSetSeeds.Json(RuleSetSeeds.ImportantMailGuard));
        cut.Find("[data-testid=apply-json]").Click();
        Assert.Contains("duplicate it", cut.Find("[data-testid=json-errors]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Switching_a_decision_to_delete_needs_a_confirmation_and_cancelling_keeps_archive()
    {
        await _db.RuleSets().SaveAsync(RuleSetSeeds.Load(RuleSetSeeds.PromoCleanup));
        var cut = Editor(RuleSetSeeds.PromoCleanup);
        string State() => cut.Find("[data-testid=decision-1] [data-testid=remove-state]").TextContent;

        cut.Find("[data-testid=decision-1] [data-testid=remove-delete]").Click();

        var confirm = cut.Find("[data-testid=decision-1] [data-testid=confirm-delete]");
        Assert.Contains("Archive confident promos", confirm.TextContent, StringComparison.Ordinal);
        Assert.Contains("30 days", confirm.TextContent, StringComparison.Ordinal);
        Assert.NotNull(cut.Find("[data-testid=delete-lock-note]"));
        Assert.Equal("archives", State());

        cut.Find("[data-testid=confirm-delete-no]").Click();
        Assert.Equal("archives", State());
        Assert.Empty(cut.FindAll("[data-testid=confirm-delete]"));
        Assert.Empty(cut.FindAll("[data-testid=unsaved]"));

        cut.Find("[data-testid=decision-1] [data-testid=remove-delete]").Click();
        cut.Find("[data-testid=confirm-delete-yes]").Click();
        Assert.Equal("deletes", State());
        Assert.Contains("\"remove\": \"delete\"", Json(cut), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validation_errors_block_saving()
    {
        await _db.RuleSets().SaveAsync(RuleSetSeeds.Load(RuleSetSeeds.PromoCleanup));
        var cut = Editor(RuleSetSeeds.PromoCleanup);
        Assert.True(Disabled(cut, "save"));

        cut.Find("[data-testid=decision-0] [data-testid=decision-when]").Change("nobody.noul >= 0.5");

        Assert.Contains("/decisions/0", cut.Find("[data-testid=validation-errors]").TextContent, StringComparison.Ordinal);
        Assert.True(Disabled(cut, "save"));
        Assert.Single(await _db.RuleSets().ListVersionsAsync(RuleSetSeeds.PromoCleanup));
    }

    [Fact]
    public async Task Saving_makes_a_new_version_that_is_activated_and_switched_live_only_after_confirming()
    {
        await _db.RuleSets().SaveAsync(RuleSetSeeds.Load(RuleSetSeeds.PromoCleanup));
        var cut = Editor(RuleSetSeeds.PromoCleanup);

        cut.Find("[data-testid=description]").Input("Edited.");
        Assert.True(Disabled(cut, "activate"));
        cut.Find("[data-testid=save]").Click();

        Assert.Contains("Saved v2", cut.Find("[data-testid=message]").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=unsaved]"));
        Assert.Contains("v2 compared with v1", cut.Markup, StringComparison.Ordinal);

        cut.Find("[data-testid=activate]").Click();
        Assert.Null((await _db.RuleSets().GetAsync(RuleSetSeeds.PromoCleanup))!.ActiveVersion);
        cut.Find("[data-testid=confirm-action-yes]").Click();
        Assert.Equal(2, (await _db.RuleSets().GetAsync(RuleSetSeeds.PromoCleanup))!.ActiveVersion);
        Assert.Contains("dry run", cut.Find("[data-testid=active-mode]").TextContent, StringComparison.Ordinal);

        cut.Find("[data-testid=set-mode]").Click();
        Assert.Contains("change mail in Gmail", cut.Find("[data-testid=confirm-action]").TextContent, StringComparison.Ordinal);
        cut.Find("[data-testid=confirm-action-no]").Click();
        Assert.Equal(RuleSetMode.DryRun, (await _db.RuleSets().GetAsync(RuleSetSeeds.PromoCleanup))!.ActiveMode);

        cut.Find("[data-testid=set-mode]").Click();
        cut.Find("[data-testid=confirm-action-yes]").Click();
        var summary = (await _db.RuleSets().GetAsync(RuleSetSeeds.PromoCleanup))!;
        Assert.Equal((3, RuleSetMode.Live), (summary.ActiveVersion, summary.ActiveMode));
        Assert.Contains("live", cut.Find("[data-testid=active-mode]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Versions_show_a_diff_and_load_back_into_the_editor()
    {
        var first = await _db.RuleSets().SaveAsync(RuleSetSeeds.Load(RuleSetSeeds.PromoCleanup));
        await _db.RuleSets().SaveAsync(first.Document with { Description = "Second." });
        var cut = Editor(RuleSetSeeds.PromoCleanup);

        var diff = cut.Find("[data-testid=version-diff]").TextContent;
        Assert.Contains("+   \"description\": \"Second.\"", diff, StringComparison.Ordinal);
        Assert.Contains("-   \"description\": \"Labels marketing", diff, StringComparison.Ordinal);

        cut.Find("[data-testid=version-1]").Click();
        cut.Find("[data-testid=restore]").Click();

        Assert.Equal(first.Document.Description, cut.Find("[data-testid=description]").GetAttribute("value"));
        Assert.NotNull(cut.Find("[data-testid=unsaved]"));
    }

    [Fact]
    public async Task Unknown_rule_sets_say_so()
    {
        var cut = Editor("nope");

        await cut.WaitForAssertionAsync(() => Assert.NotNull(cut.Find("[data-testid=not-found]")));
    }

    [Fact]
    public async Task The_list_counts_delete_rules_and_duplicates_imports_and_deletes()
    {
        var promo = RuleSetSeeds.Load(RuleSetSeeds.PromoCleanup);
        await _db.ActivateAsync(promo with
        {
            Decisions = [.. promo.Decisions.Select((d, i) => i == 1 ? d with { Remove = RemoveMode.Delete } : d)],
        });
        await _db.RuleSets().SaveAsync(RuleSetSeeds.Load(RuleSetSeeds.ImportantMailGuard));
        var cut = _ctx.Render<RuleSets>();

        var promoRow = cut.Find("[data-testid=row-promo-cleanup]");
        Assert.Contains("1 in v1", promoRow.QuerySelector("[data-testid=deletes]")!.TextContent, StringComparison.Ordinal);
        Assert.Contains("dry run", promoRow.QuerySelector("[data-testid=mode]")!.TextContent, StringComparison.Ordinal);
        var guardRow = cut.Find("[data-testid=row-important-mail-guard]");
        Assert.Contains("none", guardRow.QuerySelector("[data-testid=deletes]")!.TextContent, StringComparison.Ordinal);
        Assert.Contains("inactive", guardRow.QuerySelector("[data-testid=mode]")!.TextContent, StringComparison.Ordinal);

        cut.Find("[data-testid=duplicate-promo-cleanup]").Click();
        Assert.NotNull(cut.Find("[data-testid=row-promo-cleanup-copy]"));

        cut.Find("[data-testid=remove-promo-cleanup-copy]").Click();
        cut.Find("[data-testid=confirm-remove-no]").Click();
        Assert.NotNull(cut.Find("[data-testid=row-promo-cleanup-copy]"));
        cut.Find("[data-testid=remove-promo-cleanup-copy]").Click();
        cut.Find("[data-testid=confirm-remove-yes]").Click();
        Assert.Empty(cut.FindAll("[data-testid=row-promo-cleanup-copy]"));
        Assert.Null(await _db.RuleSets().GetAsync("promo-cleanup-copy"));

        cut.Find("[data-testid=import-toggle]").Click();
        cut.Find("[data-testid=import-json]").Change("""{ "name": "broken" }""");
        cut.Find("[data-testid=import-save]").Click();
        Assert.NotNull(cut.Find("[data-testid=import-errors]"));

        cut.Find("[data-testid=import-json]").Change(RuleSetSeeds.Json(RuleSetSeeds.PromoCleanup).Replace("\"promo-cleanup\"", "\"imported\"", StringComparison.Ordinal));
        cut.Find("[data-testid=import-save]").Click();
        Assert.Contains("Imported imported as v1", cut.Find("[data-testid=message]").TextContent, StringComparison.Ordinal);
        Assert.NotNull(cut.Find("[data-testid=row-imported]"));
    }
}
