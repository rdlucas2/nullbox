using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Nullbox.Core.Jev;
using Nullbox.Core.Rules;
using Nullbox.Data;
using Nullbox.Data.Pipeline;
using Nullbox.Data.Rules;

namespace Nullbox.Tests.Pipeline;

/// <summary>A migrated nullbox.db in a temp directory, with stores handed out per scope like request scopes.</summary>
public sealed class PipelineDb : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nullbox-pipeline-tests", Guid.NewGuid().ToString("N"));
    private ServiceProvider _services = null!;

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 27, 9, 0, 0, TimeSpan.Zero));

    public string Directory => _dir;

    public async Task InitializeAsync()
    {
        _services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<TimeProvider>(Time)
            .AddNullboxData(_dir)
            .BuildServiceProvider();
        await using var scope = _services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<NullboxDbContext>().Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        SqliteConnection.ClearAllPools();
        System.IO.Directory.Delete(_dir, recursive: true);
    }

    private NullboxDbContext Context() => _services.CreateScope().ServiceProvider.GetRequiredService<NullboxDbContext>();

    public EfDecisionLog Log() => new(Context());

    public EfRuleSetStore RuleSets() => new(Context(), Time);

    /// <summary>Saves and activates <paramref name="document"/>.</summary>
    public async Task<RuleSetVersion> ActivateAsync(RuleSetDocument document)
    {
        var saved = await RuleSets().SaveAsync(document);
        await RuleSets().ActivateAsync(saved.Name, saved.Version);
        return saved with { IsActive = true };
    }
}

public static class Answers
{
    public static JevResult Of(params (string Question, JevAnswer Answer)[] answers) => new()
    {
        Model = "jev-fake",
        Answers = answers.ToDictionary(a => a.Question, a => a.Answer),
    };

    public static JevChoiceAnswer Choice(string choice, double confidence, params (string Option, double P)[] probabilities) => new()
    {
        Choice = choice,
        Confidence = confidence,
        Probabilities = probabilities.Length == 0
            ? new Dictionary<string, double> { [choice] = confidence }
            : probabilities.ToDictionary(p => p.Option, p => p.P),
    };

    public static JevNoulAnswer Noul(double value) => new() { Noul = value };

    public static JevScoreAnswer Score(double score, double confidence) => new() { Score = score, Confidence = confidence };

    /// <summary>
    /// Answers for the promo-cleanup seed: not from a person, a confident promo that fits a category, so the
    /// "Archive confident promos" decision matches.
    /// </summary>
    public static JevResult ConfidentPromo { get; } = Of(
        ("from_a_person", Noul(0.05)),
        ("category", Choice("promo", 0.95, ("promo", 0.95), ("other", 0.05))),
        ("fits_category", Noul(0.9)));
}
