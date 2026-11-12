using CodeReviewAgent.Core.Diff;

namespace CodeReviewAgent.Core.Sources;

/// <summary>
/// Where a change under review comes from. The agent only sees a source through this interface, which is what
/// decides which tools it gets: a pasted diff cannot read whole files, a GitHub PR can.
/// </summary>
public interface IChangeSource
{
    /// <summary>"diff" or "github_pr".</summary>
    string Kind { get; }

    /// <summary>Human-readable label, e.g. "owner/repo#123" or "pasted diff".</summary>
    string Label { get; }

    string RawDiff { get; }

    ParsedDiff Diff { get; }

    /// <summary>PR title, if any. Author-controlled, therefore untrusted.</summary>
    string? Title { get; }

    /// <summary>PR description, if any. Author-controlled, therefore untrusted.</summary>
    string? Description { get; }

    /// <summary>True when the source can return full file contents at the head revision.</summary>
    bool SupportsRepositoryAccess { get; }

    /// <summary>Returns the head-revision content of a file, or null if it does not exist.</summary>
    Task<string?> ReadFileAsync(string path, CancellationToken cancellationToken);

    /// <summary>Returns every file path in the repository at the head revision.</summary>
    Task<IReadOnlyList<string>> ListRepositoryFilesAsync(CancellationToken cancellationToken);
}

/// <summary>An error the caller can act on (bad input, PR not found, rate limited), as opposed to a bug.</summary>
public sealed class ChangeSourceException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>A raw diff pasted by the user or piped into the CLI. Only the diff itself is available.</summary>
public sealed class PastedDiffSource : IChangeSource
{
    public PastedDiffSource(string rawDiff, string? label = null)
    {
        RawDiff = rawDiff;
        Diff = UnifiedDiffParser.Parse(rawDiff);
        Label = string.IsNullOrWhiteSpace(label) ? "pasted diff" : label;
        if (Diff.Files.Count == 0)
        {
            throw new ChangeSourceException("The input does not contain a unified diff (no file headers or hunks were found).");
        }
    }

    public string Kind => "diff";

    public string Label { get; }

    public string RawDiff { get; }

    public ParsedDiff Diff { get; }

    public string? Title => null;

    public string? Description => null;

    public bool SupportsRepositoryAccess => false;

    public Task<string?> ReadFileAsync(string path, CancellationToken cancellationToken) =>
        throw new NotSupportedException("A pasted diff has no repository to read from.");

    public Task<IReadOnlyList<string>> ListRepositoryFilesAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException("A pasted diff has no repository to list.");
}
