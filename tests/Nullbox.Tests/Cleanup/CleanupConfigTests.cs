using Nullbox.Core.Cleanup;

namespace Nullbox.Tests.Cleanup;

/// <summary>
/// Golden cases for emailBlaster's <c>build_query</c> (cleanup.py has no tests of its own, so these pin its exact
/// output, including Python's <c>.get(key, default)</c> truthiness) and the config format, read from emailBlaster's
/// own files.
/// </summary>
public sealed class CleanupConfigTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Cleanup", name));

    private static string Query(string rule, string keep = "{}")
    {
        var config = CleanupConfig.Parse($$"""{"keep":{{keep}},"rules":[{{rule}}]}""");
        return CleanupQuery.Build(config.Rules[0], config.Keep);
    }

    [Fact]
    public void Example_rules_build_the_same_queries_as_cleanup_py()
    {
        var config = CleanupConfig.Parse(Fixture("cleanup_rules.example.json"));

        Assert.Equal("cleanup_unread", config.Label);
        Assert.Equal(
            [
                "in:inbox is:unread ({category:promotions category:social category:forums} older_than:30d) -is:starred -is:important -from:(yourbank.com OR yourinsurer.com OR yourschool.org OR amazon.com)",
                "in:inbox is:unread (category:updates older_than:6m) -is:starred -is:important -from:(yourbank.com OR yourinsurer.com OR yourschool.org OR amazon.com)",
                "in:inbox is:unread (category:updates older_than:30d newer_than:6m) -is:starred -is:important -from:(yourbank.com OR yourinsurer.com OR yourschool.org OR amazon.com)",
            ],
            config.Rules.Select(r => CleanupQuery.Build(r, config.Keep)));
        Assert.Equal(["Old promos and social", "Old updates", "Newer updates"], config.Rules.Select(r => r.Name));
        Assert.All(config.Rules, r => Assert.Equal((true, true), (r.MarkRead, r.Archive)));
    }

    [Fact]
    public void All_read_includes_read_mail_and_keeps_nothing()
    {
        var config = CleanupConfig.Parse(Fixture("cleanup_all_read.json"));
        var rule = Assert.Single(config.Rules);

        Assert.Equal("in:inbox (is:read)", CleanupQuery.Build(rule, config.Keep));
        Assert.Equal((false, true), (rule.MarkRead, rule.Archive));
        Assert.Equal("cleanup_read_20261122_all", config.Label);
    }

    [Fact]
    public void All_unread_wraps_the_rule_query_in_parentheses()
    {
        var config = CleanupConfig.Parse(Fixture("cleanup_all_unread.json"));

        Assert.Equal("in:inbox is:unread (in:inbox)", CleanupQuery.Build(Assert.Single(config.Rules), config.Keep));
    }

    [Theory]
    [InlineData("""{"name":"r","query":"older_than:1y"}""", "{}", "in:inbox is:unread (older_than:1y) -is:starred -is:important")] // keep defaults on
    [InlineData("""{"name":"r","query":"q","unread_only":false}""", "{}", "in:inbox (q) -is:starred -is:important")]
    [InlineData("""{"name":"r","query":"q","unread_only":null}""", "{}", "in:inbox (q) -is:starred -is:important")] // None is falsy
    [InlineData("""{"name":"r","query":"q","unread_only":0}""", "{}", "in:inbox (q) -is:starred -is:important")]
    [InlineData("""{"name":"r","query":"q"}""", """{"starred":false}""", "in:inbox is:unread (q) -is:important")]
    [InlineData("""{"name":"r","query":"q"}""", """{"important":false,"starred":false}""", "in:inbox is:unread (q)")]
    [InlineData("""{"name":"r","query":"q"}""", """{"senders":[]}""", "in:inbox is:unread (q) -is:starred -is:important")]
    [InlineData("""{"name":"r","query":"q"}""", """{"starred":false,"important":false,"senders":["a.com"]}""", "in:inbox is:unread (q) -from:(a.com)")]
    public void Build_query_matches_python_semantics(string rule, string keep, string expected)
        => Assert.Equal(expected, Query(rule, keep));

    [Fact]
    public void Missing_keep_and_flags_use_cleanup_py_defaults()
    {
        var config = CleanupConfig.Parse("""{"rules":[{"name":"r","query":"q"}]}""");

        Assert.Null(config.Label);
        Assert.Equal((true, true, true), (config.Rules[0].UnreadOnly, config.Rules[0].MarkRead, config.Rules[0].Archive));
        Assert.Equal("in:inbox is:unread (q) -is:starred -is:important", CleanupQuery.Build(config.Rules[0], config.Keep));
    }

    [Theory]
    [InlineData("""{"label":"","rules":[{"name":"r","query":"q"}]}""")]
    [InlineData("""{"label":null,"rules":[{"name":"r","query":"q"}]}""")]
    public void A_blank_label_means_the_dated_default(string json)
        => Assert.Null(CleanupConfig.Parse(json).Label);

    [Fact]
    public void Default_label_is_cleanup_and_the_utc_date()
        => Assert.Equal("cleanup_20261128", CleanupConfig.DefaultLabel(new DateTimeOffset(2026, 11, 27, 23, 30, 0, TimeSpan.FromHours(-5))));

    [Theory]
    [InlineData("""{"rule":[]}""", "rules")]
    [InlineData("""{"rules":[]}""", "rules")]
    [InlineData("""{"rules":[{"query":"q"}]}""", "rules[0].name")]
    [InlineData("""{"rules":[{"name":"r"}]}""", "rules[0].query")]
    [InlineData("""{"rules":[{"name":"r","query":"  "}]}""", "rules[0].query")]
    [InlineData("""{"keep":{"senders":"a.com"},"rules":[{"name":"r","query":"q"}]}""", "keep.senders")]
    [InlineData("""[1]""", "object")]
    [InlineData("""{ nope""", "JSON")]
    public void Invalid_configs_name_the_problem(string json, string expected)
    {
        var ex = Assert.Throws<CleanupConfigException>(() => CleanupConfig.Parse(json));

        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, true, "mark read + archive")]
    [InlineData(true, false, "mark read")]
    [InlineData(false, true, "archive")]
    [InlineData(false, false, "nothing")]
    public void Rules_describe_what_they_do(bool markRead, bool archive, string expected)
        => Assert.Equal(expected, new CleanupRule { Name = "r", Query = "q", MarkRead = markRead, Archive = archive }.Describe());
}
