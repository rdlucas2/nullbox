using Nullbox.Core.Rules;
using Nullbox.Core.SavedRules;
using Nullbox.Tests.Pipeline;

namespace Nullbox.Tests.SavedRules;

public sealed class SavedRuleStoreTests : IAsyncLifetime
{
    private readonly PipelineDb _db = new();

    public Task InitializeAsync() => _db.InitializeAsync();

    public Task DisposeAsync() => _db.DisposeAsync();

    private static SavedRule Rule(string name, string from = "@shop.example") => new() { Name = name, From = from, Actions = ["label:shop"] };

    private async Task<string[]> NamesAsync() => [.. (await _db.SavedRules().ListAsync()).Select(r => r.Rule.Name)];

    [Fact]
    public async Task New_rules_go_last_and_saving_a_name_again_replaces_it_in_place()
    {
        await _db.SavedRules().SaveAsync(Rule("a"));
        await _db.SavedRules().SaveAsync(Rule("b"));
        await _db.SavedRules().SaveAsync(Rule("a", "@other.example"));

        Assert.Equal(["a", "b"], await NamesAsync());
        Assert.Equal("@other.example", (await _db.SavedRules().GetAsync("a"))!.Rule.From);
    }

    [Fact]
    public async Task A_rename_keeps_the_place_and_cannot_take_another_rules_name()
    {
        await _db.SavedRules().SaveAsync(Rule("a"));
        await _db.SavedRules().SaveAsync(Rule("b"));

        await _db.SavedRules().SaveAsync(Rule("first"), "a");
        var clash = await Assert.ThrowsAsync<SavedRuleValidationException>(() => _db.SavedRules().SaveAsync(Rule("b"), "first"));

        Assert.Equal(["first", "b"], await NamesAsync());
        Assert.Equal("/name", Assert.Single(clash.Errors).Path);
    }

    [Fact]
    public async Task Invalid_rules_are_not_stored()
    {
        var error = await Assert.ThrowsAsync<SavedRuleValidationException>(() => _db.SavedRules().SaveAsync(Rule("a", "not-an-address")));

        Assert.Equal("/from", Assert.Single(error.Errors).Path);
        Assert.Empty(await NamesAsync());
    }

    [Fact]
    public async Task Rules_move_up_and_down_and_can_be_removed()
    {
        foreach (var name in new[] { "a", "b", "c" })
        {
            await _db.SavedRules().SaveAsync(Rule(name));
        }

        Assert.True(await _db.SavedRules().MoveAsync("c", -1));
        Assert.Equal(["a", "c", "b"], await NamesAsync());
        Assert.True(await _db.SavedRules().MoveAsync("a", 5));
        Assert.Equal(["c", "b", "a"], await NamesAsync());
        Assert.True(await _db.SavedRules().DeleteAsync("b"));
        Assert.False(await _db.SavedRules().DeleteAsync("b"));
        Assert.False(await _db.SavedRules().MoveAsync("b", 1));
        Assert.Equal(["c", "a"], await NamesAsync());
    }

    [Fact]
    public async Task Import_replaces_by_name_adds_the_rest_and_round_trips_an_export()
    {
        await _db.SavedRules().SaveAsync(Rule("a"));
        await _db.SavedRules().SaveAsync(Rule("b"));

        var result = await _db.SavedRules().ImportAsync("""
            { "rules": [
              { "name": "c", "to": "me+news@gmail.com", "remove": "archive" },
              { "name": "a", "from": "@new.example", "actions": ["star"], "mode": "live" }
            ] }
            """);

        Assert.Equal(new SavedRulesImport(1, 1), result);
        Assert.Equal(["a", "b", "c"], await NamesAsync());
        Assert.Equal(RuleSetMode.Live, (await _db.SavedRules().GetAsync("a"))!.Rule.Mode);

        var export = await _db.SavedRules().ExportAsync();
        Assert.Equal(new SavedRulesImport(0, 3), await _db.SavedRules().ImportAsync(export));
        Assert.Equal(export, await _db.SavedRules().ExportAsync());
    }

    [Fact]
    public async Task An_import_with_one_bad_rule_stores_nothing()
    {
        var error = await Assert.ThrowsAsync<SavedRuleValidationException>(() => _db.SavedRules().ImportAsync("""
            { "rules": [ { "name": "ok", "from": "@x.example", "actions": ["star"] }, { "name": "bad", "from": "x", "actions": ["star"] } ] }
            """));

        Assert.Contains(error.Errors, e => e.Path == "/rules/1/from");
        Assert.Empty(await NamesAsync());
    }
}
