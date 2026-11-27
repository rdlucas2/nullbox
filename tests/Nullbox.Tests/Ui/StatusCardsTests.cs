using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Nullbox.Core.Gmail;
using Nullbox.Core.Jev;
using Nullbox.Web.Components.Dashboard;
using Nullbox.Web.Components.Layout;
using Nullbox.Web.Ui;

namespace Nullbox.Tests.Ui;

/// <summary>MudBlazor services are async-disposable only, so the bUnit context is disposed through <see cref="IAsyncLifetime"/>.</summary>
public sealed class StatusCardsTests : IAsyncLifetime
{
    private readonly BunitContext _ctx = new();

    private static readonly DateTimeOffset Now = new(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);

    public StatusCardsTests()
    {
        _ctx.Services.AddMudServices();
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private static DashboardSnapshot Snapshot(
        GmailCredentialStatus? gmail = null,
        JobRunStatus? poll = null,
        JevBudgetStatus? budget = null,
        QueueStatus? queues = null)
        => new(gmail, poll, null, queues ?? new QueueStatus(0, 0, 0, 0, new Dictionary<string, long>()), budget, Now);

    [Fact]
    public void Empty_snapshot_shows_setup_hints()
    {
        var cut = _ctx.Render<StatusCards>(p => p.Add(c => c.Snapshot, Snapshot()));

        Assert.Contains("Not connected", cut.Find("[data-testid=gmail-state]").TextContent);
        Assert.NotNull(cut.Find("a[href='/settings/gmail']"));
        Assert.Contains("not scheduled yet", cut.Find("[data-testid=poll-last]").TextContent);
        Assert.Contains("Jev is not set up", cut.Find("[data-testid=jev-tokens]").TextContent);
        Assert.Contains("0 waiting", cut.Find("[data-testid=queue-depth]").TextContent);
    }

    [Fact]
    public void Ready_gmail_shows_short_scopes_and_expiry()
    {
        var gmail = new GmailCredentialStatus(
            GmailTokenKind.Primary,
            GmailAuthState.Ready,
            ["https://www.googleapis.com/auth/gmail.modify"],
            Now.AddMinutes(30),
            Now,
            null);

        var cut = _ctx.Render<StatusCards>(p => p.Add(c => c.Snapshot, Snapshot(gmail)));

        Assert.Contains("Connected", cut.Find("[data-testid=gmail-state]").TextContent);
        Assert.Contains("gmail.modify", cut.Markup);
        Assert.Contains("2026-11-27 12:30 UTC", cut.Markup);
    }

    [Fact]
    public void Revoked_gmail_points_to_reauthorize()
    {
        var gmail = new GmailCredentialStatus(GmailTokenKind.Primary, GmailAuthState.ReauthorizationRequired, [], null, null, "Google rejected the refresh token.");

        var cut = _ctx.Render<StatusCards>(p => p.Add(c => c.Snapshot, Snapshot(gmail)));

        Assert.Contains("Reauthorize", cut.Find("[data-testid=gmail-state]").TextContent);
        Assert.Contains("Google rejected the refresh token.", cut.Markup);
        Assert.NotNull(cut.Find("a[href='/settings/gmail']"));
    }

    [Fact]
    public void Poll_queue_and_budget_show_their_numbers()
    {
        var poll = new JobRunStatus("inbox-poll", Now.UtcDateTime.AddMinutes(-3), "Succeeded", Now.UtcDateTime.AddMinutes(2), null);
        var queues = new QueueStatus(4, 1, 2, 10, new Dictionary<string, long> { ["inbox"] = 4 });
        var budget = new JevBudgetStatus(1500, 1000, 3, 60);

        var cut = _ctx.Render<StatusCards>(p => p.Add(c => c.Snapshot, Snapshot(poll: poll, budget: budget, queues: queues)));

        Assert.Contains("3 min ago", cut.Find("[data-testid=poll-last]").TextContent);
        Assert.Contains("4 waiting", cut.Find("[data-testid=queue-depth]").TextContent);
        Assert.Contains("inbox: 4", cut.Markup);
        Assert.Contains("1,500 / 1,000 tokens today", cut.Find("[data-testid=jev-tokens]").TextContent);
        Assert.Contains("Over budget", cut.Markup);
    }

    [Fact]
    public void Nav_menu_links_every_section_and_hangfire()
    {
        var cut = _ctx.Render<NavMenu>();

        foreach (var item in NavItems.All)
        {
            Assert.NotNull(cut.Find($"a[href='{item.Href}']"));
        }

        Assert.NotNull(cut.Find("a[href='/hangfire']"));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _ctx.DisposeAsync();
}
