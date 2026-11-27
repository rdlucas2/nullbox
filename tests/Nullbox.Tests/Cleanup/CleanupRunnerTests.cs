using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Nullbox.Core.Cleanup;
using Nullbox.Core.Gmail;
using Nullbox.Gmail.Cleanup;
using Nullbox.Tests.Inbox;

namespace Nullbox.Tests.Cleanup;

public sealed class CleanupRunnerTests
{
    private readonly FakeInboxGmail _gmail = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 27, 9, 0, 0, TimeSpan.Zero));
    private readonly Dictionary<string, string[]> _results = new(StringComparer.Ordinal);

    public CleanupRunnerTests() => _gmail.Search = query => _results.TryGetValue(query, out var ids) ? ids : [];

    private CleanupRunner Runner() => new(_gmail, _time, NullLogger<CleanupRunner>.Instance);

    private static CleanupConfig Config(string rules, string extra = "")
        => CleanupConfig.Parse($$"""{"keep":{"starred":false,"important":false}{{extra}},"rules":[{{rules}}]}""");

    private void Matches(string ruleQuery, params string[] ids) => _results[$"in:inbox is:unread ({ruleQuery})"] = ids;

    private void From(string id, string from) => _gmail.Messages[id] = _gmail.Add(id) with { From = from };

    [Fact]
    public async Task Dry_run_counts_each_rule_once_per_message_and_changes_nothing()
    {
        Matches("a", "m1", "m2");
        Matches("b", "m2", "m3");

        var result = await Runner().RunAsync(Config("""{"name":"A","query":"a"},{"name":"B","query":"b"}"""), dryRun: true);

        Assert.True(result.DryRun);
        Assert.Equal([("A", 2, 0), ("B", 1, 0)], result.Rules.Select(r => (r.Name, r.Matched, r.Changed)));
        Assert.Equal((3, 0), (result.Matched, result.Changed));
        Assert.Equal(["in:inbox is:unread (a)", "in:inbox is:unread (b)"], result.Rules.Select(r => r.Query));
        Assert.Equal("cleanup_20261127", result.Label);
        Assert.Empty(_gmail.Changes);
    }

    [Fact]
    public async Task Sample_lists_the_top_senders_of_the_first_matches()
    {
        Matches("a", "m1", "m2", "m3", "m4");
        From("m1", "Shop <deals@shop.example>");
        From("m2", "News <news@paper.example>");
        From("m3", "Shop <deals@shop.example>");
        From("m4", "Never sampled <x@y.example>");

        var result = await Runner().RunAsync(Config("""{"name":"A","query":"a"}"""), dryRun: true, sampleSize: 3);

        Assert.Equal(
            [new SenderCount("Shop <deals@shop.example>", 2), new SenderCount("News <news@paper.example>", 1)],
            Assert.Single(result.Rules).TopSenders);
        Assert.Equal([MessageFormat.Metadata, MessageFormat.Metadata, MessageFormat.Metadata], _gmail.Fetches);
    }

    [Fact]
    public async Task A_sender_sample_skips_messages_that_cannot_be_read()
    {
        Matches("a", "gone", "m1");
        From("m1", "Shop <deals@shop.example>");

        var result = await Runner().RunAsync(Config("""{"name":"A","query":"a"}"""), dryRun: true, sampleSize: 5);

        Assert.Equal([new SenderCount("Shop <deals@shop.example>", 1)], result.Rules[0].TopSenders);
    }

    [Fact]
    public async Task Apply_marks_read_archives_and_labels_with_one_modify_per_rule()
    {
        Matches("a", "m1", "m2");
        Matches("b", "m2", "m3");

        var result = await Runner().RunAsync(
            Config("""{"name":"A","query":"a"},{"name":"B","query":"b"}""", extra: ""","label":"cleanup_unread" """),
            dryRun: false);

        Assert.Equal(
            ["label:cleanup_unread", "modify:m1,m2:+Label_cleanup_unread:-UNREAD,INBOX", "modify:m3:+Label_cleanup_unread:-UNREAD,INBOX"],
            _gmail.Changes);
        Assert.Equal([2, 1], result.Rules.Select(r => r.Changed));
        Assert.False(result.DryRun);
    }

    [Fact]
    public async Task Mark_read_only_rules_neither_archive_nor_label()
    {
        Matches("a", "m1");

        await Runner().RunAsync(Config("""{"name":"A","query":"a","archive":false}"""), dryRun: false);

        Assert.Equal(["modify:m1:+:-UNREAD"], _gmail.Changes);
    }

    [Fact]
    public async Task Archive_only_rules_leave_mail_unread()
    {
        _results["in:inbox (is:read)"] = ["m1"];

        await Runner().RunAsync(Config("""{"name":"A","query":"is:read","unread_only":false,"mark_read":false}"""), dryRun: false);

        Assert.Equal(["label:cleanup_20261127", "modify:m1:+Label_cleanup_20261127:-INBOX"], _gmail.Changes);
    }

    [Fact]
    public async Task A_rule_that_does_nothing_makes_no_call()
    {
        Matches("a", "m1");

        var result = await Runner().RunAsync(Config("""{"name":"A","query":"a","archive":false,"mark_read":false}"""), dryRun: false);

        Assert.Empty(_gmail.Changes);
        Assert.Equal((1, 0), (result.Matched, result.Changed));
    }

    [Fact]
    public async Task Nothing_matched_changes_nothing_and_creates_no_label()
    {
        var result = await Runner().RunAsync(Config("""{"name":"A","query":"a"}"""), dryRun: false);

        Assert.Empty(_gmail.Changes);
        Assert.Equal(0, result.Matched);
    }

    [Fact]
    public async Task Keep_exclusions_reach_the_gmail_search()
    {
        await Runner().RunAsync(
            CleanupConfig.Parse("""{"keep":{"senders":["bank.example"]},"rules":[{"name":"A","query":"older_than:1y"}]}"""),
            dryRun: true);

        Assert.Equal(["search:in:inbox is:unread (older_than:1y) -is:starred -is:important -from:(bank.example)"], _gmail.Calls);
    }
}
