using System.Text.Json;
using System.Text.Json.Serialization;
using CodeReviewAgent.Core.Diff;
using CodeReviewAgent.Core.Models;

namespace CodeReviewAgent.Core.Agent;

/// <summary>
/// Grounds the model's submitted review in the actual diff before anything is shown to a user.
/// <list type="bullet">
/// <item>Hard errors (unknown files, malformed payload) are sent back to the model as a tool error so it can fix them.</item>
/// <item>Soft problems (a line number that isn't in the diff) are repaired deterministically and reported as warnings.</item>
/// </list>
/// The schema is already enforced by strict tool use; this is defence in depth plus semantic checks the schema can't express.
/// </summary>
public sealed class FindingValidator(ParsedDiff diff, IReadOnlyDictionary<string, int> filesReadLineCounts, int maxFindings = 50)
{
    public sealed record Outcome(bool IsValid, string? ErrorForModel, string Summary, ReviewVerdict Verdict, IReadOnlyList<ReviewFinding> Findings, IReadOnlyList<string> Warnings);

    private static readonly JsonSerializerOptions WireOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    /// <param name="input">The raw submit_review tool input.</param>
    /// <param name="allowRepair">When false, hard errors are returned to the model; when true (retries exhausted) offending findings are dropped instead.</param>
    public Outcome Validate(IReadOnlyDictionary<string, JsonElement> input, bool allowRepair)
    {
        SubmitReviewInput? payload;
        try
        {
            payload = JsonSerializer.Deserialize<SubmitReviewInput>(JsonSerializer.Serialize(input), WireOptions);
        }
        catch (JsonException ex)
        {
            return Fail($"submit_review input could not be parsed: {ex.Message}. Resubmit matching the schema.");
        }

        if (payload is null || string.IsNullOrWhiteSpace(payload.Summary) || payload.Verdict is null)
        {
            return Fail("submit_review requires a non-empty 'summary' and a 'verdict'.");
        }

        var warnings = new List<string>();
        var findings = new List<ReviewFinding>();
        var unknownFiles = new List<string>();

        foreach (var raw in payload.Findings ?? [])
        {
            if (raw.Category is null || raw.Severity is null || string.IsNullOrWhiteSpace(raw.Title) || string.IsNullOrWhiteSpace(raw.Explanation))
            {
                if (!allowRepair)
                {
                    return Fail($"Finding '{raw.Title}' is missing category, severity, title or explanation.");
                }

                warnings.Add($"Dropped an incomplete finding ('{raw.Title}').");
                continue;
            }

            var file = diff.FindFile(raw.File);
            if (file is null)
            {
                unknownFiles.Add(raw.File ?? "(null)");
                if (allowRepair)
                {
                    warnings.Add($"Dropped finding '{raw.Title}': file '{raw.File}' is not part of this change.");
                }

                continue;
            }

            var (line, endLine) = GroundLines(file, raw, warnings);
            findings.Add(new ReviewFinding
            {
                Category = raw.Category.Value,
                Severity = raw.Severity.Value,
                Confidence = raw.Confidence ?? Confidence.Medium,
                File = file.Path,
                Line = line,
                EndLine = endLine,
                Title = raw.Title.Trim(),
                Explanation = raw.Explanation.Trim(),
                Suggestion = string.IsNullOrWhiteSpace(raw.Suggestion) ? null : raw.Suggestion.Trim(),
            });
        }

        if (unknownFiles.Count > 0 && !allowRepair)
        {
            return Fail(
                $"These finding file paths are not in the diff: {string.Join(", ", unknownFiles.Distinct())}. " +
                $"Valid paths: {string.Join(", ", diff.Files.Select(f => f.Path))}. " +
                "Findings must be attached to a changed file (attach missing-test findings to the source file that needs tests). Resubmit.");
        }

        var deduped = findings
            .GroupBy(f => (f.File, f.Line, f.Category, Title: f.Title.ToLowerInvariant()))
            .Select(g => g.First())
            .OrderBy(f => f.Severity)
            .ThenBy(f => f.Category)
            .ThenBy(f => f.File, StringComparer.Ordinal)
            .ThenBy(f => f.Line ?? 0)
            .ToList();

        if (deduped.Count < findings.Count)
        {
            warnings.Add($"Removed {findings.Count - deduped.Count} duplicate finding(s).");
        }

        if (deduped.Count > maxFindings)
        {
            warnings.Add($"Kept the {maxFindings} most severe of {deduped.Count} findings.");
            deduped = deduped.Take(maxFindings).ToList();
        }

        var verdict = payload.Verdict.Value;
        var blocking = deduped.Any(f => f.Severity is Severity.Critical or Severity.High && f.Category is FindingCategory.Bug or FindingCategory.Risk);
        if (verdict == ReviewVerdict.Approve && blocking)
        {
            verdict = ReviewVerdict.RequestChanges;
            warnings.Add("Verdict changed from 'approve' to 'request_changes' because a critical/high bug or risk was reported.");
        }

        return new Outcome(true, null, payload.Summary.Trim(), verdict, deduped, warnings);
    }

    private (int? Line, int? EndLine) GroundLines(DiffFile file, SubmitFinding raw, List<string> warnings)
    {
        if (raw.Line is not { } line)
        {
            return (null, null);
        }

        if (file.ChangeType == FileChangeType.Deleted || file.IsBinary)
        {
            warnings.Add($"'{raw.Title}': {file.Path} has no new-file lines; finding kept as file-level.");
            return (null, null);
        }

        // A line is grounded if it is visible in the diff, or the agent actually read that file and the line exists.
        var grounded = file.VisibleNewLines.Contains(line)
            || (filesReadLineCounts.TryGetValue(file.Path, out var count) && line >= 1 && line <= count);
        if (!grounded)
        {
            warnings.Add($"'{raw.Title}': line {line} of {file.Path} is outside the diff and was not read by the agent; finding kept as file-level.");
            return (null, null);
        }

        int? end = raw.EndLine is { } e && e > line ? e : null;
        return (line, end);
    }

    private static Outcome Fail(string error) => new(false, error, string.Empty, ReviewVerdict.Comment, [], []);

    private sealed record SubmitReviewInput(string? Summary, ReviewVerdict? Verdict, List<SubmitFinding>? Findings);

    private sealed record SubmitFinding(
        FindingCategory? Category,
        Severity? Severity,
        Confidence? Confidence,
        string? File,
        int? Line,
        [property: JsonPropertyName("end_line")] int? EndLine,
        string? Title,
        string? Explanation,
        string? Suggestion);
}
