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
    }
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
