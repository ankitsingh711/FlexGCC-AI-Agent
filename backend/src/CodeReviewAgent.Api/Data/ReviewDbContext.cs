using Microsoft.EntityFrameworkCore;

namespace CodeReviewAgent.Api.Data;

public enum ReviewStatus
{
    Running,
    Completed,
    Failed,
}

/// <summary>One review run: the input, the outcome, and enough telemetry (tokens, duration) to reason about cost.</summary>
public sealed class ReviewRun
{
    public Guid Id { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public ReviewStatus Status { get; set; }

    /// <summary>"diff" or "github_pr".</summary>
    public string SourceKind { get; set; } = string.Empty;

    public string SourceLabel { get; set; } = string.Empty;

    public string? PullRequestUrl { get; set; }

    /// <summary>The diff that was reviewed, kept so a run can be reproduced or re-reviewed.</summary>
    public string DiffText { get; set; } = string.Empty;

    public string? Verdict { get; set; }

    public int FindingCount { get; set; }

    public int HighSeverityCount { get; set; }

    /// <summary>Serialized <see cref="Core.Models.ReviewResult"/>.</summary>
    public string? ResultJson { get; set; }

    public string? Error { get; set; }

    public long DurationMs { get; set; }

    public long InputTokens { get; set; }

    public long OutputTokens { get; set; }

    public string? Model { get; set; }
}

public sealed class ReviewDbContext(DbContextOptions<ReviewDbContext> options) : DbContext(options)
{
    public DbSet<ReviewRun> Reviews => Set<ReviewRun>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var run = modelBuilder.Entity<ReviewRun>();
        run.ToTable("ReviewRuns");
        run.HasKey(r => r.Id);
        run.Property(r => r.Status).HasConversion<string>().HasMaxLength(16);
        run.Property(r => r.SourceKind).HasMaxLength(16);
        run.Property(r => r.SourceLabel).HasMaxLength(300);
        run.Property(r => r.PullRequestUrl).HasMaxLength(500);
        run.Property(r => r.Verdict).HasMaxLength(32);
        run.Property(r => r.Model).HasMaxLength(100);

        // SQLite can't ORDER BY DateTimeOffset natively; store as UTC ticks so history sorts server-side.
        run.Property(r => r.CreatedAt).HasConversion(v => v.UtcTicks, v => new DateTimeOffset(v, TimeSpan.Zero));
        run.HasIndex(r => r.CreatedAt);
    }
}
