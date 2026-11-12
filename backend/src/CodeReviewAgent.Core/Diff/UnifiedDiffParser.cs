using System.Text;
using System.Text.RegularExpressions;

namespace CodeReviewAgent.Core.Diff;

/// <summary>
/// Parses unified diffs as produced by <c>git diff</c>, GitHub's <c>.diff</c> endpoint, or plain <c>diff -u</c>.
/// Tracks old/new line numbers for every line so the agent can cite exact new-file lines and so findings
/// can be validated against what the diff actually contains.
/// </summary>
public static partial class UnifiedDiffParser
{
    [GeneratedRegex(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@ ?(.*)$")]
    private static partial Regex HunkHeader();

    [GeneratedRegex(@"^diff --git (""?)a/(.+?)\1 (""?)b/(.+?)\3$")]
    private static partial Regex GitHeader();

    public static ParsedDiff Parse(string diffText)
    {
        ArgumentNullException.ThrowIfNull(diffText);

        var lines = diffText.Replace("\r\n", "\n").Split('\n');
        var files = new List<DiffFile>();
        FileBuilder? current = null;
        HunkBuilder? hunk = null;

        void FlushHunk()
        {
            if (hunk is not null && current is not null)
            {
                current.Hunks.Add(hunk.Build());
            }

            hunk = null;
        }

        void FlushFile()
        {
            FlushHunk();
            if (current is not null && current.HasContent)
            {
                files.Add(current.Build());
            }

            current = null;
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                FlushFile();
                current = new FileBuilder { HasContent = true };
                var m = GitHeader().Match(line);
                if (m.Success)
                {
                    current.OldPath = m.Groups[2].Value;
                    current.NewPath = m.Groups[4].Value;
                }

                continue;
            }

            // A hunk is open and the line belongs to it: consume before checking file-level headers,
            // because a removed line can legitimately start with "--" (e.g. "--- " inside a removed SQL comment).
            if (hunk is not null && current is not null && hunk.Accepts(line))
            {
                hunk.Add(line);
                continue;
            }

            if (line.StartsWith("--- ", StringComparison.Ordinal) && i + 1 < lines.Length && lines[i + 1].StartsWith("+++ ", StringComparison.Ordinal))
            {
                // Plain unified diffs (no "diff --git" line) start a new file at "---".
                if (current is null || current.Hunks.Count > 0 || hunk is not null)
                {
                    FlushFile();
                    current = new FileBuilder();
                }

                current.HasContent = true;
                current.OldPath = ParseHeaderPath(line[4..]) ?? current.OldPath;
                current.NewPath = ParseHeaderPath(lines[i + 1][4..]) ?? current.NewPath;
                if (line[4..].Trim() == "/dev/null")
                {
                    current.ChangeType = FileChangeType.Added;
                    current.OldPath = null;
                }

                if (lines[i + 1][4..].Trim() == "/dev/null")
                {
                    current.ChangeType = FileChangeType.Deleted;
                    current.NewPath = null;
                }

                i++;
                continue;
            }

            if (current is null)
            {
                continue; // Preamble (e.g. commit message from `git format-patch`).
            }

            var header = HunkHeader().Match(line);
            if (header.Success)
            {
                FlushHunk();
                hunk = new HunkBuilder(
                    int.Parse(header.Groups[1].Value),
                    header.Groups[2].Success ? int.Parse(header.Groups[2].Value) : 1,
                    int.Parse(header.Groups[3].Value),
                    header.Groups[4].Success ? int.Parse(header.Groups[4].Value) : 1,
                    header.Groups[5].Value.Trim());
                continue;
            }

            if (line.StartsWith("new file mode", StringComparison.Ordinal))
            {
                current.ChangeType = FileChangeType.Added;
            }
            else if (line.StartsWith("deleted file mode", StringComparison.Ordinal))
            {
                current.ChangeType = FileChangeType.Deleted;
            }
            else if (line.StartsWith("rename from ", StringComparison.Ordinal))
            {
                current.ChangeType = FileChangeType.Renamed;
                current.OldPath = line["rename from ".Length..];
            }
            else if (line.StartsWith("rename to ", StringComparison.Ordinal))
            {
                current.ChangeType = FileChangeType.Renamed;
                current.NewPath = line["rename to ".Length..];
            }
            else if (line.StartsWith("Binary files ", StringComparison.Ordinal) || line.StartsWith("GIT binary patch", StringComparison.Ordinal))
            {
                current.IsBinary = true;
            }
        }

        FlushFile();
        return new ParsedDiff(files);
    }

    /// <summary>
    /// Renders a file's hunks with explicit new-file line numbers in a gutter, e.g. <c>  42 + var x = 1;</c>.
    /// Raw unified diffs force the model to do line arithmetic from hunk headers, which it gets wrong often enough
    /// to matter; an explicit gutter makes cited line numbers reliable.
    /// </summary>
    public static string RenderAnnotated(DiffFile file, int maxLines = int.MaxValue)
    {
        var sb = new StringBuilder();
        sb.Append("### ").Append(file.Path);
        if (file.ChangeType == FileChangeType.Renamed && file.OldPath is not null)
        {
            sb.Append(" (renamed from ").Append(file.OldPath).Append(')');
        }
        else if (file.ChangeType != FileChangeType.Modified)
        {
            sb.Append(" (").Append(file.ChangeType.ToString().ToLowerInvariant()).Append(')');
        }

        sb.Append($"  [+{file.Additions} -{file.Deletions}]\n");

        if (file.IsBinary)
        {
            sb.Append("(binary file - content not shown)\n");
            return sb.ToString();
        }

        var emitted = 0;
        foreach (var hunk in file.Hunks)
        {
            sb.Append($"@@ -{hunk.OldStart},{hunk.OldCount} +{hunk.NewStart},{hunk.NewCount} @@ {hunk.SectionHeader}".TrimEnd()).Append('\n');
            foreach (var l in hunk.Lines)
            {
                if (emitted++ >= maxLines)
                {
                    sb.Append($"... (diff for this file truncated after {maxLines} lines; call get_file_diff for the rest)\n");
                    return sb.ToString();
                }

                var gutter = l.NewLineNumber?.ToString() ?? string.Empty;
                var marker = l.Kind switch
                {
                    DiffLineKind.Added => '+',
                    DiffLineKind.Removed => '-',
                    _ => ' ',
                };
                sb.Append(gutter.PadLeft(5)).Append(' ').Append(marker).Append(' ').Append(l.Text).Append('\n');
            }
        }

        return sb.ToString();
    }

    private static string? ParseHeaderPath(string raw)
    {
        var p = raw.Split('\t')[0].Trim();
        if (p == "/dev/null")
        {
            return null;
        }

        if (p.Length >= 2 && p[0] == '"' && p[^1] == '"')
        {
            p = p[1..^1];
        }

        return ParsedDiff.NormalizePath(p);
    }

    private sealed class FileBuilder
    {
        public string? OldPath { get; set; }

        public string? NewPath { get; set; }

        public FileChangeType ChangeType { get; set; } = FileChangeType.Modified;

        public bool IsBinary { get; set; }

        public bool HasContent { get; set; }

        public List<DiffHunk> Hunks { get; } = [];

        public DiffFile Build()
        {
            if (ChangeType == FileChangeType.Modified && OldPath is not null && NewPath is not null && OldPath != NewPath)
            {
                ChangeType = FileChangeType.Renamed;
            }

            return new DiffFile(
                ChangeType == FileChangeType.Added ? null : OldPath,
                ChangeType == FileChangeType.Deleted ? null : NewPath,
                ChangeType,
                IsBinary,
                Hunks);
        }
    }

    private sealed class HunkBuilder
    {
        private readonly List<DiffLine> _lines = [];
        private readonly int _oldStart;
        private readonly int _oldCount;
        private readonly int _newStart;
        private readonly int _newCount;
        private readonly string _sectionHeader;
        private int _oldLine;
        private int _newLine;
        private int _oldSeen;
        private int _newSeen;

        public HunkBuilder(int oldStart, int oldCount, int newStart, int newCount, string sectionHeader)
        {
            (_oldStart, _oldCount, _newStart, _newCount, _sectionHeader) = (oldStart, oldCount, newStart, newCount, sectionHeader);
            _oldLine = oldStart;
            _newLine = newStart;
        }

        private bool IsComplete => _oldSeen >= _oldCount && _newSeen >= _newCount;

        /// <summary>
        /// A line belongs to this hunk while the hunk's declared line counts are not yet exhausted.
        /// "\ No newline at end of file" markers are always accepted (and ignored).
        /// </summary>
        public bool Accepts(string line)
        {
            if (line.StartsWith('\\'))
            {
                return true;
            }

            if (IsComplete)
            {
                return false;
            }

            return line.Length == 0 || line[0] is ' ' or '+' or '-';
        }

        public void Add(string line)
        {
            if (line.StartsWith('\\'))
            {
                return;
            }

            // Some tools strip the trailing space of empty context lines; treat "" as an empty context line.
            var marker = line.Length == 0 ? ' ' : line[0];
            var text = line.Length == 0 ? string.Empty : line[1..];
            switch (marker)
            {
                case '+':
                    _lines.Add(new DiffLine(DiffLineKind.Added, null, _newLine++, text));
                    _newSeen++;
                    break;
                case '-':
                    _lines.Add(new DiffLine(DiffLineKind.Removed, _oldLine++, null, text));
                    _oldSeen++;
                    break;
                default:
                    _lines.Add(new DiffLine(DiffLineKind.Context, _oldLine++, _newLine++, text));
                    _oldSeen++;
                    _newSeen++;
                    break;
            }
        }

        public DiffHunk Build() => new(_oldStart, _oldCount, _newStart, _newCount, _sectionHeader, _lines);
    }
}
