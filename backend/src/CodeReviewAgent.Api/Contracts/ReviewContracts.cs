using System.ComponentModel.DataAnnotations;
using CodeReviewAgent.Core.Models;

namespace CodeReviewAgent.Api.Contracts;

/// <summary>Request body for POST /api/reviews. Exactly one of <see cref="Diff"/> or <see cref="PullRequestUrl"/>.</summary>
public sealed class CreateReviewRequest : IValidatableObject
{
    /// <summary>A unified diff (output of `git diff`, `git show`, or a GitHub .diff).</summary>
    [MaxLength(400_000, ErrorMessage = "Diff must be at most 400,000 characters.")]
    public string? Diff { get; set; }

    /// <summary>A public GitHub pull request URL, e.g. https://github.com/owner/repo/pull/123.</summary>
    [MaxLength(500)]
    public string? PullRequestUrl { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var hasDiff = !string.IsNullOrWhiteSpace(Diff);
        var hasUrl = !string.IsNullOrWhiteSpace(PullRequestUrl);
        if (hasDiff == hasUrl)
        {
            yield return new ValidationResult(
                "Provide exactly one of 'diff' or 'pullRequestUrl'.",
                [nameof(Diff), nameof(PullRequestUrl)]);
        }
    }
}

public sealed record ReviewSummaryDto(
    Guid Id,
    DateTimeOffset CreatedAt,
    string Status,
    string SourceKind,
    string SourceLabel,
    string? Verdict,
    int FindingCount,
    int HighSeverityCount,
    long DurationMs);

public sealed record ReviewRunDto(
    Guid Id,
    DateTimeOffset CreatedAt,
    string Status,
    string SourceKind,
    string SourceLabel,
    string? PullRequestUrl,
    string? Error,
    ReviewResult? Result,
    string Diff);

public sealed record SampleDto(string Name, string Title, string Description);

public sealed record HealthDto(string Status, string Provider, string Model, bool ModelConfigured, string ApiKeyEnvVar, bool GitHubTokenConfigured, int MaxTurns);
