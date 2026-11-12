using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeReviewAgent.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter<FindingCategory>))]
public enum FindingCategory
{
    [JsonStringEnumMemberName("bug")] Bug,
    [JsonStringEnumMemberName("risk")] Risk,
    [JsonStringEnumMemberName("missing_test")] MissingTest,
    [JsonStringEnumMemberName("style")] Style,
}

[JsonConverter(typeof(JsonStringEnumConverter<Severity>))]
public enum Severity
{
    [JsonStringEnumMemberName("critical")] Critical,
    [JsonStringEnumMemberName("high")] High,
    [JsonStringEnumMemberName("medium")] Medium,
    [JsonStringEnumMemberName("low")] Low,
    [JsonStringEnumMemberName("info")] Info,
}

[JsonConverter(typeof(JsonStringEnumConverter<Confidence>))]
public enum Confidence
{
    [JsonStringEnumMemberName("high")] High,
    [JsonStringEnumMemberName("medium")] Medium,
    [JsonStringEnumMemberName("low")] Low,
}

[JsonConverter(typeof(JsonStringEnumConverter<ReviewVerdict>))]
public enum ReviewVerdict
{
    [JsonStringEnumMemberName("approve")] Approve,
    [JsonStringEnumMemberName("comment")] Comment,
    [JsonStringEnumMemberName("request_changes")] RequestChanges,
}

public sealed record ReviewFinding
{
    public required FindingCategory Category { get; init; }

    public required Severity Severity { get; init; }

    public required Confidence Confidence { get; init; }

    public required string File { get; init; }

    /// <summary>1-based line in the new version of the file, or null for file-level findings.</summary>
    public int? Line { get; init; }

    public int? EndLine { get; init; }

    public required string Title { get; init; }

    public required string Explanation { get; init; }

    public string? Suggestion { get; init; }
}

public sealed record DiffStats(int FilesChanged, int Additions, int Deletions);

public sealed record TokenUsage(long InputTokens, long OutputTokens, long CacheReadInputTokens, long CacheCreationInputTokens)
{
    public static TokenUsage Zero { get; } = new(0, 0, 0, 0);

    public TokenUsage Add(TokenUsage other) => new(
        InputTokens + other.InputTokens,
        OutputTokens + other.OutputTokens,
        CacheReadInputTokens + other.CacheReadInputTokens,
        CacheCreationInputTokens + other.CacheCreationInputTokens);
}

public sealed record ToolCallRecord(int Turn, string Name, string InputSummary, bool IsError, int ResultChars, long DurationMs);

public sealed record ReviewResult
{
    public required string Summary { get; init; }

    public required ReviewVerdict Verdict { get; init; }

    public required IReadOnlyList<ReviewFinding> Findings { get; init; }

    public required DiffStats Stats { get; init; }

    public required string Model { get; init; }

    public required int Turns { get; init; }

    public required TokenUsage Usage { get; init; }

    public required IReadOnlyList<ToolCallRecord> ToolCalls { get; init; }

    /// <summary>Post-processing notes, e.g. findings whose line numbers were outside the diff and were downgraded.</summary>
    public required IReadOnlyList<string> Warnings { get; init; }

    public required long DurationMs { get; init; }
}

public static class ReviewJson
{
    /// <summary>Shared serializer settings: camelCase properties, enums as their snake_case wire names.</summary>
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };
}
