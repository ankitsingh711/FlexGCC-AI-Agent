using System.Text;
using System.Text.Json;
using CodeReviewAgent.Core.Diff;
using CodeReviewAgent.Core.Sources;

namespace CodeReviewAgent.Core.Agent;

/// <summary>Result of running one investigation tool. Errors are returned to the model, never thrown.</summary>
public sealed record ToolOutcome(string Content, bool IsError)
{
    public static ToolOutcome Ok(string content) => new(content, false);

    public static ToolOutcome Error(string message) => new(message, true);
}

/// <summary>Static description of a tool: name, model-facing description, JSON schema.</summary>
public sealed record ToolSpec(string Name, string Description, string JsonSchema, bool Strict);

/// <summary>
/// The agent's read-only toolbox. Every tool is side-effect free and scoped to the change under review:
/// file paths are validated, output is size-capped (with an explicit note to the model, never silent truncation),
/// and repository tools are only offered when the source actually has a repository behind it.
/// </summary>
public sealed class ReviewTools(IChangeSource source, AgentOptions options)
{
    public const string ListChangedFiles = "list_changed_files";
    public const string GetFileDiff = "get_file_diff";
    public const string ReadFile = "read_file";
    public const string SearchRepositoryFiles = "search_repository_files";
    public const string SubmitReview = "submit_review";

    private readonly Dictionary<string, int> _filesRead = new(StringComparer.Ordinal);

    /// <summary>Files the agent has read via read_file, with their line counts. Used to ground cited line numbers.</summary>
    public IReadOnlyDictionary<string, int> FilesRead => _filesRead;

    public IReadOnlyList<ToolSpec> GetSpecs()
    {
        var specs = new List<ToolSpec>
        {
            new(
                ListChangedFiles,
                "List every file touched by the change with its change type and added/removed line counts. " +
                "Call this first when the diff in the prompt was summarized because it was too large.",
                """{"type":"object","properties":{},"additionalProperties":false}""",
                Strict: true),
            new(
                GetFileDiff,
                "Return the diff hunks for one changed file, annotated with new-file line numbers in the left gutter. " +
                "Call this for any changed file whose diff is not already shown in full in the conversation.",
                """{"type":"object","properties":{"path":{"type":"string","description":"Path of a changed file, exactly as listed by list_changed_files."}},"required":["path"],"additionalProperties":false}""",
                Strict: true),
        };

        if (source.SupportsRepositoryAccess)
        {
            specs.Add(new(
                ReadFile,
                "Read a file from the repository at the PR's head commit, with line numbers. Works for changed and unchanged files. " +
                "Call this when a finding depends on code outside the diff: the full body of a modified function, a called method's " +
                "signature or null-handling, a class's other members, or an existing test file. Returns at most " +
                $"{options.MaxReadFileLines} lines per call; use start_line/end_line to page.",
                """{"type":"object","properties":{"path":{"type":"string","description":"Repository-relative path, e.g. src/app/service.py"},"start_line":{"type":"integer","description":"First line to return (1-based). Defaults to 1."},"end_line":{"type":"integer","description":"Last line to return (inclusive)."}},"required":["path"],"additionalProperties":false}""",
                Strict: true));
            specs.Add(new(
                SearchRepositoryFiles,
                "Find repository file paths containing a case-insensitive substring. Call this to check whether tests already exist " +
                "for changed code (e.g. search 'test' plus the module name) or to locate a referenced file before read_file.",
                """{"type":"object","properties":{"query":{"type":"string","description":"Substring to match against file paths, e.g. 'user_service' or 'tests/'."}},"required":["query"],"additionalProperties":false}""",
                Strict: true));
        }

        specs.Add(new(
            SubmitReview,
            "Submit the final structured review. Call this exactly once, when your investigation is complete; it ends the review. " +
            "Every finding must reference a file from the diff, and line numbers must be new-file line numbers shown in the diff gutter.",
            SubmitReviewSchema,
            Strict: true));

        return specs;
    }

    public async Task<ToolOutcome> ExecuteAsync(string name, IReadOnlyDictionary<string, JsonElement> input, CancellationToken ct)
    {
        try
        {
            return name switch
            {
                ListChangedFiles => ToolOutcome.Ok(RenderFileList()),
                GetFileDiff => RunGetFileDiff(input),
                ReadFile when source.SupportsRepositoryAccess => await RunReadFileAsync(input, ct),
                SearchRepositoryFiles when source.SupportsRepositoryAccess => await RunSearchAsync(input, ct),
                _ => ToolOutcome.Error($"Unknown or unavailable tool '{name}'."),
            };
        }
        catch (ChangeSourceException ex)
        {
            return ToolOutcome.Error(ex.Message);
        }
        catch (HttpRequestException ex)
        {
            return ToolOutcome.Error($"Repository request failed: {ex.Message}. Continue the review without this information.");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return ToolOutcome.Error("Repository request timed out. Continue the review without this information.");
        }
    }

    public string RenderFileList()
    {
        var sb = new StringBuilder();
        sb.Append($"{source.Diff.Files.Count} file(s) changed, +{source.Diff.TotalAdditions} -{source.Diff.TotalDeletions}\n");
        foreach (var f in source.Diff.Files)
        {
            sb.Append($"- {f.Path} ({f.ChangeType.ToString().ToLowerInvariant()}{(f.IsBinary ? ", binary" : string.Empty)}) +{f.Additions} -{f.Deletions}\n");
        }

        return sb.ToString();
    }

    private ToolOutcome RunGetFileDiff(IReadOnlyDictionary<string, JsonElement> input)
    {
        var path = GetString(input, "path");
        var file = source.Diff.FindFile(path);
        if (file is null)
        {
            return ToolOutcome.Error($"'{path}' is not part of this change. Changed files: {string.Join(", ", source.Diff.Files.Select(f => f.Path))}");
        }

        return ToolOutcome.Ok(UnifiedDiffParser.RenderAnnotated(file, options.MaxDiffLinesPerFileTool));
    }

    private async Task<ToolOutcome> RunReadFileAsync(IReadOnlyDictionary<string, JsonElement> input, CancellationToken ct)
    {
        var rawPath = GetString(input, "path");
        if (!TryNormalizeRepoPath(rawPath, out var path, out var pathError))
        {
            return ToolOutcome.Error(pathError!);
        }

        var content = await source.ReadFileAsync(path, ct);
        if (content is null)
        {
            var deleted = source.Diff.FindFile(path) is { ChangeType: FileChangeType.Deleted };
            return ToolOutcome.Error(deleted
                ? $"'{path}' is deleted by this change; it has no head-revision content."
                : $"'{path}' does not exist at the head commit. Use search_repository_files to find the right path.");
        }

        if (content.Contains('\0'))
        {
            return ToolOutcome.Error($"'{path}' is a binary file.");
        }

        var lines = content.Replace("\r\n", "\n").Split('\n');
        _filesRead[path] = lines.Length;
        var start = Math.Max(1, GetInt(input, "start_line") ?? 1);
        var requestedEnd = GetInt(input, "end_line") ?? lines.Length;
        var end = Math.Min(Math.Min(requestedEnd, lines.Length), start + options.MaxReadFileLines - 1);
        if (start > lines.Length)
        {
            return ToolOutcome.Error($"'{path}' has only {lines.Length} lines.");
        }

        var sb = new StringBuilder();
        sb.Append($"{path} (lines {start}-{end} of {lines.Length}, head revision)\n");
        for (var i = start; i <= end; i++)
        {
            sb.Append(i.ToString().PadLeft(5)).Append(" | ").Append(lines[i - 1]).Append('\n');
        }

        if (end < Math.Min(requestedEnd, lines.Length))
        {
            sb.Append($"... output capped at {options.MaxReadFileLines} lines; call read_file again with start_line={end + 1} for more.\n");
        }

        return ToolOutcome.Ok(sb.ToString());
    }

    private async Task<ToolOutcome> RunSearchAsync(IReadOnlyDictionary<string, JsonElement> input, CancellationToken ct)
    {
        var query = GetString(input, "query").Trim();
        if (query.Length < 2)
        {
            return ToolOutcome.Error("Query must be at least 2 characters.");
        }

        var all = await source.ListRepositoryFilesAsync(ct);
        var matches = all.Where(p => p.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0)
        {
            return ToolOutcome.Ok($"No files match '{query}' ({all.Count:N0} files searched).");
        }

        const int Cap = 60;
        var sb = new StringBuilder($"{matches.Count} file(s) match '{query}':\n");
        foreach (var m in matches.Take(Cap))
        {
            sb.Append("- ").Append(m).Append('\n');
        }

        if (matches.Count > Cap)
        {
            sb.Append($"... {matches.Count - Cap} more; refine the query.\n");
        }

        return ToolOutcome.Ok(sb.ToString());
    }

    /// <summary>Rejects absolute paths, traversal and URL-ish input before anything reaches the GitHub client.</summary>
    public static bool TryNormalizeRepoPath(string raw, out string path, out string? error)
    {
        path = ParsedDiff.NormalizePath(raw ?? string.Empty);
        error = null;
        if (path.Length == 0 || path.Length > 400)
        {
            error = "Path must be a non-empty repository-relative path.";
            return false;
        }

        if (path.Contains("://", StringComparison.Ordinal) || path.Contains('?') || path.Contains('#') || path.Contains('\0'))
        {
            error = "Path must be a repository-relative file path, not a URL.";
            return false;
        }

        if (path.Split('/').Any(seg => seg is ".." or "." || seg.Length == 0))
        {
            error = "Path must not contain '.', '..' or empty segments.";
            return false;
        }

        return true;
    }

    private static string GetString(IReadOnlyDictionary<string, JsonElement> input, string key) =>
        input.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

    private static int? GetInt(IReadOnlyDictionary<string, JsonElement> input, string key) =>
        input.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    // Strict tool schemas require additionalProperties:false and every property listed in "required";
    // optional values are expressed as nullable types instead.
    public const string SubmitReviewSchema = """
    {
      "type": "object",
      "properties": {
        "summary": {
          "type": "string",
          "description": "2-4 sentences: what the change does and the overall assessment."
        },
        "verdict": {
          "type": "string",
          "enum": ["approve", "comment", "request_changes"],
          "description": "request_changes if any critical/high bug or risk; comment if only medium/low findings; approve if nothing material."
        },
        "findings": {
          "type": "array",
          "items": {
            "type": "object",
            "properties": {
              "category": { "type": "string", "enum": ["bug", "risk", "missing_test", "style"] },
              "severity": { "type": "string", "enum": ["critical", "high", "medium", "low", "info"] },
              "confidence": { "type": "string", "enum": ["high", "medium", "low"] },
              "file": { "type": "string", "description": "Changed file path exactly as shown in the diff." },
              "line": { "anyOf": [{ "type": "integer" }, { "type": "null" }], "description": "New-file line number from the diff gutter, or null for a file-level finding." },
              "end_line": { "anyOf": [{ "type": "integer" }, { "type": "null" }], "description": "Last line of a multi-line range, or null." },
              "title": { "type": "string", "description": "One-line summary of the issue." },
              "explanation": { "type": "string", "description": "Why it is a problem and when it triggers. Concrete inputs/state where possible." },
              "suggestion": { "anyOf": [{ "type": "string" }, { "type": "null" }], "description": "A specific fix, ideally a short code snippet, or null." }
            },
            "required": ["category", "severity", "confidence", "file", "line", "end_line", "title", "explanation", "suggestion"],
            "additionalProperties": false
          }
        }
      },
      "required": ["summary", "verdict", "findings"],
      "additionalProperties": false
    }
    """;
}
