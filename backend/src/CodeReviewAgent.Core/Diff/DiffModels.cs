namespace CodeReviewAgent.Core.Diff;

public enum FileChangeType
{
    Modified,
    Added,
    Deleted,
    Renamed,
}

public enum DiffLineKind
{
    Context,
    Added,
    Removed,
}

/// <summary>One line inside a hunk. Line numbers are 1-based; the side a line does not exist on is null.</summary>
public sealed record DiffLine(DiffLineKind Kind, int? OldLineNumber, int? NewLineNumber, string Text);

public sealed record DiffHunk(int OldStart, int OldCount, int NewStart, int NewCount, string SectionHeader, IReadOnlyList<DiffLine> Lines);

public sealed record DiffFile(
    string? OldPath,
    string? NewPath,
    FileChangeType ChangeType,
    bool IsBinary,
    IReadOnlyList<DiffHunk> Hunks)
{
    /// <summary>The path reviewers refer to: the new path, or the old path for deleted files.</summary>
    public string Path => NewPath ?? OldPath ?? "(unknown)";

    public int Additions => Hunks.Sum(h => h.Lines.Count(l => l.Kind == DiffLineKind.Added));

    public int Deletions => Hunks.Sum(h => h.Lines.Count(l => l.Kind == DiffLineKind.Removed));

    /// <summary>New-file line numbers that are visible in the diff (added or context lines).</summary>
    public IReadOnlySet<int> VisibleNewLines =>
        _visibleNewLines ??= Hunks.SelectMany(h => h.Lines)
            .Where(l => l.NewLineNumber.HasValue)
            .Select(l => l.NewLineNumber!.Value)
            .ToHashSet();

    /// <summary>New-file line numbers that were added by this change.</summary>
    public IReadOnlySet<int> AddedNewLines =>
        _addedNewLines ??= Hunks.SelectMany(h => h.Lines)
            .Where(l => l.Kind == DiffLineKind.Added)
            .Select(l => l.NewLineNumber!.Value)
            .ToHashSet();

    private HashSet<int>? _visibleNewLines;
    private HashSet<int>? _addedNewLines;
}

public sealed record ParsedDiff(IReadOnlyList<DiffFile> Files)
{
    public int TotalAdditions => Files.Sum(f => f.Additions);

    public int TotalDeletions => Files.Sum(f => f.Deletions);

    /// <summary>
    /// Finds a file by the path a model (or a human) used. Tolerates the common "a/" / "b/" / "./" prefixes
    /// and leading slashes, because models frequently echo the raw diff header.
    /// </summary>
    public DiffFile? FindFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var normalized = NormalizePath(path);
        return Files.FirstOrDefault(f => NormalizePath(f.Path) == normalized)
            ?? Files.FirstOrDefault(f => f.OldPath is not null && NormalizePath(f.OldPath) == normalized);
    }

    public static string NormalizePath(string path)
    {
        var p = path.Trim().Replace('\\', '/');
        if (p.StartsWith("a/", StringComparison.Ordinal) || p.StartsWith("b/", StringComparison.Ordinal))
        {
            p = p[2..];
        }

        while (p.StartsWith("./", StringComparison.Ordinal))
        {
            p = p[2..];
        }

        return p.TrimStart('/');
    }
}
