using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Nullbox.Core.Rules;
using Nullbox.Data;
using Nullbox.Data.Rules;
using Nullbox.Tests.Infrastructure;

namespace Nullbox.Tests.Rules;

public sealed class RuleSetStoreTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nullbox-rules-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 27, 8, 0, 0, TimeSpan.Zero));
    private ServiceProvider _services = null!;

    public async Task InitializeAsync()
    {
        _services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<TimeProvider>(_time)
            .AddNullboxData(_dir)
            .BuildServiceProvider();
        await using var scope = _services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<NullboxDbContext>().Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    /// <summary>A fresh context per call, like a request scope.</summary>
    private EfRuleSetStore Store() => new(_services.CreateScope().ServiceProvider.GetRequiredService<NullboxDbContext>(), _time);

    private static RuleSetDocument Promo => RuleSetSeeds.Load(RuleSetSeeds.PromoCleanup);

    [Fact]
    public async Task First_save_creates_version_one_without_activating_it()
    {
        var saved = await Store().SaveAsync(Promo);

        var summary = await Store().GetAsync(RuleSetSeeds.PromoCleanup);
        Assert.Equal(1, saved.Version);
        Assert.Equal("promo-cleanup@v1", saved.Key);
        Assert.False(saved.IsActive);
        Assert.Equal(new RuleSetSummary("promo-cleanup", Promo.Description, 1, null, null, _time.GetUtcNow().UtcDateTime), summary);
        Assert.Null(await Store().GetVersionAsync(RuleSetSeeds.PromoCleanup));
    }

    [Fact]
    public async Task Each_change_adds_an_immutable_version_and_identical_saves_do_not()
    {
        var v1 = await Store().SaveAsync(Promo);
        _time.Advance(TimeSpan.FromMinutes(5));
        var v2 = await Store().SaveAsync(Promo with { Mode = RuleSetMode.Live });
        var again = await Store().SaveAsync(Promo with { Mode = RuleSetMode.Live, Schema = null });

        var versions = await Store().ListVersionsAsync(RuleSetSeeds.PromoCleanup);
        Assert.Equal(2, v2.Version);
        Assert.Equal(v2.Version, again.Version);
        Assert.Equal([2, 1], versions.Select(v => v.Version));
        Assert.Equal(RuleSetMode.DryRun, (await Store().GetVersionAsync(RuleSetSeeds.PromoCleanup, 1))!.Document.Mode);
        Assert.Equal(v1.Json, versions[1].Json);
        Assert.Equal(v1.CreatedUtc, versions[1].CreatedUtc);
    }

    [Fact]
    public async Task Activating_picks_the_version_the_pipeline_uses()
    {
        await Store().SaveAsync(Promo);
        await Store().SaveAsync(Promo with { Mode = RuleSetMode.Live });

        await Store().ActivateAsync(RuleSetSeeds.PromoCleanup, 1);
        var active = await Store().GetVersionAsync(RuleSetSeeds.PromoCleanup);
        var summary = await Store().GetAsync(RuleSetSeeds.PromoCleanup);

        Assert.Equal(1, active!.Version);
        Assert.True(active.IsActive);
        Assert.Equal(1, summary!.ActiveVersion);
        Assert.Equal(RuleSetMode.DryRun, summary.ActiveMode);
        Assert.Equal(2, summary.LatestVersion);
    }

    [Fact]
    public async Task Activating_a_missing_version_or_rule_set_throws()
    {
        await Store().SaveAsync(Promo);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => Store().ActivateAsync(RuleSetSeeds.PromoCleanup, 9));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Store().ActivateAsync("nope", 1));
    }

    [Fact]
    public async Task Invalid_documents_are_not_saved()
    {
        var bad = Promo with { Decisions = [new DecisionRule { When = "ghost.noul > 0.5", Actions = ["star"] }] };

        var ex = await Assert.ThrowsAsync<RuleSetValidationException>(() => Store().SaveAsync(bad));

        Assert.Contains("ghost", Assert.Single(ex.Errors).Message);
        Assert.Empty(await Store().ListAsync());
    }

    [Fact]
    public async Task Import_and_export_round_trip()
    {
        var imported = await Store().ImportAsync(RuleSetSeeds.Json(RuleSetSeeds.ImportantMailGuard));
        var exported = (await Store().GetVersionAsync(RuleSetSeeds.ImportantMailGuard, imported.Version))!.Json;

        await Store().DeleteAsync(RuleSetSeeds.ImportantMailGuard);
        var reimported = await Store().ImportAsync(exported);

        Assert.Equal(imported.Hash, reimported.Hash);
        Assert.Equal(1, reimported.Version);
    }

    [Fact]
    public async Task Import_rejects_invalid_json_with_errors()
    {
        var ex = await Assert.ThrowsAsync<RuleSetValidationException>(() => Store().ImportAsync("""{"name":"x"}"""));

        Assert.NotEmpty(ex.Errors);
    }

    [Fact]
    public async Task Saving_under_a_new_name_duplicates_and_delete_removes_every_version()
    {
        await Store().SaveAsync(Promo);
        await Store().SaveAsync(Promo with { Name = "promo-copy" });
        await Store().SaveAsync(Promo with { Name = "promo-copy", Description = "changed" });

        Assert.True(await Store().DeleteAsync("promo-copy"));
        Assert.False(await Store().DeleteAsync("promo-copy"));
        Assert.Equal(["promo-cleanup"], (await Store().ListAsync()).Select(s => s.Name));
        Assert.Empty(await Store().ListVersionsAsync("promo-copy"));
    }

    [Fact]
    public async Task Startup_seeds_the_examples_once_as_active_dry_runs()
    {
        var initializer = new DatabaseInitializer(_services.GetRequiredService<IServiceScopeFactory>(), NullLogger<DatabaseInitializer>.Instance);

        await initializer.StartAsync(default);
        await Store().SaveAsync(Promo with { Description = "edited" });
        await initializer.StartAsync(default);

        var list = await Store().ListAsync();
        Assert.Equal(["important-mail-guard", "promo-cleanup"], list.Select(s => s.Name));
        Assert.All(list, s => Assert.Equal((1, RuleSetMode.DryRun), (s.ActiveVersion!.Value, s.ActiveMode!.Value)));
        Assert.Equal(2, list.Single(s => s.Name == RuleSetSeeds.PromoCleanup).LatestVersion);
    }

    [Fact]
    public async Task Startup_upgrades_stored_versions_that_use_the_old_archive_and_trash_actions()
    {
        const string legacy = """
            {"name":"old-junk","mode":"live","questions":{"spam":{"type":"noul"}},"decisions":[
              {"name":"Bin it","when":"spam.noul >= 0.9","actions":["mark_read","trash"]}]}
            """;
        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NullboxDbContext>();
            db.RuleSets.Add(new RuleSetRow
            {
                Name = "old-junk",
                ActiveVersion = 1,
                Versions = [new RuleSetVersionRow { Version = 1, Json = legacy, Hash = "old" }],
            });
            await db.SaveChangesAsync();
        }

        await new DatabaseInitializer(_services.GetRequiredService<IServiceScopeFactory>(), NullLogger<DatabaseInitializer>.Instance).StartAsync(default);

        var active = await Store().GetVersionAsync("old-junk");
        Assert.NotNull(active);
        Assert.Equal(1, active.Version);
        Assert.Equal(["mark_read"], active.Document.Decisions[0].Actions);
        Assert.Equal(RemoveMode.Archive, active.Document.Decisions[0].Remove);
        Assert.Equal(RuleJson.Hash(active.Document), active.Hash);
        Assert.Equal(RuleSetMode.Live, (await Store().GetAsync("old-junk"))!.ActiveMode);
    }
}

[Collection(AppCollection.Name)]
public sealed class RuleSetHostingTests(NullboxAppFactory factory)
{
    [Fact]
    public async Task App_start_migrates_and_seeds_the_example_rule_sets()
    {
        _ = factory.Server;
        await using var scope = factory.Services.CreateAsyncScope();

        var list = await scope.ServiceProvider.GetRequiredService<IRuleSetStore>().ListAsync();

        Assert.Equal(RuleSetSeeds.Names.Order(), list.Select(s => s.Name));
        Assert.True(File.Exists(Path.Combine(factory.DataDirectory, "nullbox.db")));
    }

    [Fact]
    public async Task Schema_is_published_for_the_editor()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/schemas/ruleset.schema.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/schema+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(RuleSetSchema.Text, await response.Content.ReadAsStringAsync());
    }
}
