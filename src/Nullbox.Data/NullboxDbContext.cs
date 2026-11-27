using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Nullbox.Data;

/// <summary>
/// nullbox's own tables in <c>{data}/nullbox.db</c>. The schema is versioned with EF Core migrations, applied on start
/// (<see cref="DatabaseInitializer"/>).
/// </summary>
public sealed class NullboxDbContext(DbContextOptions<NullboxDbContext> options) : DbContext(options)
{
    public DbSet<RuleSetRow> RuleSets => Set<RuleSetRow>();

    public DbSet<RuleSetVersionRow> RuleSetVersions => Set<RuleSetVersionRow>();

    public DbSet<DecisionRow> Decisions => Set<DecisionRow>();

    public DbSet<MessageFailureRow> MessageFailures => Set<MessageFailureRow>();

    public DbSet<AppSettingRow> AppSettings => Set<AppSettingRow>();

    public DbSet<CleanupRunRow> CleanupRuns => Set<CleanupRunRow>();

    public DbSet<SavedRuleRow> SavedRules => Set<SavedRuleRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RuleSetRow>(e =>
        {
            e.ToTable("rule_sets");
            e.HasKey(r => r.Id);
            e.Property(r => r.Name).HasMaxLength(64).IsRequired();
            e.HasIndex(r => r.Name).IsUnique();
            e.Property(r => r.Description).HasMaxLength(2000);
            e.HasMany(r => r.Versions).WithOne().HasForeignKey(v => v.RuleSetId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RuleSetVersionRow>(e =>
        {
            e.ToTable("rule_set_versions");
            e.HasKey(v => v.Id);
            e.HasIndex(v => new { v.RuleSetId, v.Version }).IsUnique();
            e.Property(v => v.Json).IsRequired();
            e.Property(v => v.Hash).HasMaxLength(64).IsRequired();
        });

        modelBuilder.Entity<DecisionRow>(e =>
        {
            e.ToTable("decisions");
            e.HasKey(d => d.Id);
            e.Property(d => d.MessageId).HasMaxLength(128).IsRequired();
            e.Property(d => d.RuleSetKey).HasMaxLength(96).IsRequired();
            e.Property(d => d.Mode).HasConversion<string>().HasMaxLength(16);
            e.Property(d => d.Outcome).HasConversion<string>().HasMaxLength(16);
            e.Property(d => d.Model).HasMaxLength(128);
            e.Property(d => d.RuleSetName).HasMaxLength(64).IsRequired();
            e.Property(d => d.InputsHash).HasMaxLength(64);
            e.Property(d => d.Error).HasMaxLength(2000);
            e.HasIndex(d => new { d.MessageId, d.RuleSetKey });
            e.HasIndex(d => new { d.RuleSetName, d.RuleSetVersion });
            e.HasIndex(d => new { d.Outcome, d.CreatedUtc });
            e.HasIndex(d => d.CreatedUtc);
        });

        modelBuilder.Entity<MessageFailureRow>(e =>
        {
            e.ToTable("message_failures");
            e.HasKey(f => f.MessageId);
            e.Property(f => f.MessageId).HasMaxLength(128);
            e.Property(f => f.LastError).HasMaxLength(2000);
            e.HasIndex(f => new { f.Poison, f.UpdatedUtc });
        });

        modelBuilder.Entity<AppSettingRow>(e =>
        {
            e.ToTable("app_settings");
            e.HasKey(a => a.Key);
            e.Property(a => a.Key).HasMaxLength(64);
            e.Property(a => a.Value).HasMaxLength(2000);
        });

        modelBuilder.Entity<CleanupRunRow>(e =>
        {
            e.ToTable("cleanup_runs");
            e.HasKey(r => r.Id);
            e.Property(r => r.ConfigId).HasMaxLength(100).IsRequired();
            e.Property(r => r.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(r => r.Label).HasMaxLength(225);
            e.Property(r => r.RulesJson).IsRequired();
            e.Property(r => r.Error).HasMaxLength(2000);
            e.HasIndex(r => r.CreatedUtc);
        });

        modelBuilder.Entity<SavedRuleRow>(e =>
        {
            e.ToTable("saved_rules");
            e.HasKey(r => r.Id);
            e.Property(r => r.Name).HasMaxLength(64).IsRequired();
            e.HasIndex(r => r.Name).IsUnique();
            e.Property(r => r.Json).IsRequired();
            e.HasIndex(r => r.Position);
        });
    }
}

/// <summary>One saved rule (see <see cref="Nullbox.Core.SavedRules.SavedRule"/>) as validated JSON.</summary>
public sealed class SavedRuleRow
{
    public int Id { get; set; }

    public required string Name { get; set; }

    /// <summary>Where it runs, lowest first.</summary>
    public int Position { get; set; }

    public required string Json { get; set; }

    public DateTime CreatedUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }
}

/// <summary>One rule-set version's verdict on one message (see <see cref="Nullbox.Core.Pipeline.DecisionRecord"/>).</summary>
public sealed class DecisionRow
{
    public long Id { get; set; }

    public required string MessageId { get; set; }

    public required string RuleSetKey { get; set; }

    public Nullbox.Core.Rules.RuleSetMode Mode { get; set; }

    public Nullbox.Core.Pipeline.DecisionOutcome Outcome { get; set; }

    /// <summary>JSON array of matched decision names.</summary>
    public required string DecisionsJson { get; set; }

    /// <summary>JSON array of action strings.</summary>
    public required string ActionsJson { get; set; }

    /// <summary>JSON array of "action (decision): reason" strings.</summary>
    public required string SkippedJson { get; set; }

    public string? Model { get; set; }

    public required string AnswersJson { get; set; }

    public DateTime CreatedUtc { get; set; }

    /// <summary>Name part of <see cref="RuleSetKey"/>, for filtering.</summary>
    public string RuleSetName { get; set; } = string.Empty;

    /// <summary>Version part of <see cref="RuleSetKey"/>, for filtering.</summary>
    public int RuleSetVersion { get; set; }

    /// <summary>SHA-256 of the full state sent to Jev.</summary>
    public string? InputsHash { get; set; }

    /// <summary>The state sent to Jev with the body cut short; never the full body.</summary>
    public string? StateJson { get; set; }

    /// <summary>JSON array of the message's Gmail labels when it was evaluated.</summary>
    public string? LabelsJson { get; set; }

    /// <summary>Jev's least sure answer, 0 to 1.</summary>
    public double? Confidence { get; set; }

    public string? Error { get; set; }
}

/// <summary>Failed attempts for a message; <see cref="Poison"/> once it is given up on.</summary>
public sealed class MessageFailureRow
{
    public required string MessageId { get; set; }

    public int Attempts { get; set; }

    public string? LastError { get; set; }

    public bool Poison { get; set; }

    public DateTime UpdatedUtc { get; set; }
}

/// <summary>Runtime settings changed from the app, such as the kill switch.</summary>
public sealed class AppSettingRow
{
    public required string Key { get; set; }

    public string? Value { get; set; }
}

public sealed class RuleSetRow
{
    public int Id { get; set; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    /// <summary>The version the pipeline uses; null until one is activated.</summary>
    public int? ActiveVersion { get; set; }

    public DateTime CreatedUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }

    public List<RuleSetVersionRow> Versions { get; set; } = [];
}

/// <summary>An immutable saved version: the rule-set JSON exactly as validated.</summary>
public sealed class RuleSetVersionRow
{
    public int Id { get; set; }

    public int RuleSetId { get; set; }

    public int Version { get; set; }

    public required string Json { get; set; }

    /// <summary>SHA-256 of the canonical JSON, to spot saves that change nothing.</summary>
    public required string Hash { get; set; }

    public DateTime CreatedUtc { get; set; }
}

/// <summary>Lets <c>dotnet ef migrations add</c> build the context without the web host.</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<NullboxDbContext>
{
    public NullboxDbContext CreateDbContext(string[] args)
        => new(new DbContextOptionsBuilder<NullboxDbContext>().UseSqlite("Data Source=design-time.db").Options);
}

/// <summary>One cleanup run (see <see cref="Nullbox.Core.Cleanup.CleanupRun"/>); per-rule results are JSON.</summary>
public sealed class CleanupRunRow
{
    public long Id { get; set; }

    public required string ConfigId { get; set; }

    public bool DryRun { get; set; }

    public int SampleSize { get; set; }

    public Nullbox.Core.Cleanup.CleanupRunStatus Status { get; set; }

    public DateTime CreatedUtc { get; set; }

    public DateTime? FinishedUtc { get; set; }

    public string? Label { get; set; }

    public int Matched { get; set; }

    public int Changed { get; set; }

    public required string RulesJson { get; set; }

    public string? Error { get; set; }
}
