using CodeReviewAgent.Core.Diff;

namespace CodeReviewAgent.Tests;

public class UnifiedDiffParserTests
{
    private const string MultiFileDiff = """
        diff --git a/src/calc.py b/src/calc.py
        index 1111111..2222222 100644
        --- a/src/calc.py
        +++ b/src/calc.py
        @@ -1,4 +1,5 @@
         def add(a, b):
        -    return a - b
        +    return a + b
        +
         def sub(a, b):
             return a - b
        @@ -10,2 +11,3 @@ def mul(a, b):
             x = 1
        +    y = 2
             return x
        diff --git a/README.md b/README.md
        new file mode 100644
        index 0000000..3333333
        --- /dev/null
        +++ b/README.md
        @@ -0,0 +1,2 @@
        +# Title
        +Body
        \ No newline at end of file
        diff --git a/old.txt b/old.txt
        deleted file mode 100644
        index 4444444..0000000
        --- a/old.txt
        +++ /dev/null
        @@ -1 +0,0 @@
        -gone
        diff --git a/a/b.cs b/a/c.cs
        similarity index 90%
        rename from a/b.cs
        rename to a/c.cs
        diff --git a/logo.png b/logo.png
        Binary files a/logo.png and b/logo.png differ
        """;

    [Fact]
    public void Parses_all_files_with_change_types()
    {
        var diff = UnifiedDiffParser.Parse(MultiFileDiff);

        Assert.Equal(["src/calc.py", "README.md", "old.txt", "a/c.cs", "logo.png"], diff.Files.Select(f => f.Path));
        Assert.Equal(FileChangeType.Modified, diff.Files[0].ChangeType);
        Assert.Equal(FileChangeType.Added, diff.Files[1].ChangeType);
        Assert.Equal(FileChangeType.Deleted, diff.Files[2].ChangeType);
        Assert.Equal(FileChangeType.Renamed, diff.Files[3].ChangeType);
        Assert.Equal("a/b.cs", diff.Files[3].OldPath);
        Assert.True(diff.Files[4].IsBinary);
    }

    [Fact]
    public void Tracks_old_and_new_line_numbers()
    {
        var file = UnifiedDiffParser.Parse(MultiFileDiff).Files[0];

        Assert.Equal(2, file.Hunks.Count);
        var lines = file.Hunks[0].Lines;
        Assert.Equal(new DiffLine(DiffLineKind.Context, 1, 1, "def add(a, b):"), lines[0]);
        Assert.Equal(new DiffLine(DiffLineKind.Removed, 2, null, "    return a - b"), lines[1]);
        Assert.Equal(new DiffLine(DiffLineKind.Added, null, 2, "    return a + b"), lines[2]);
        Assert.Equal(new DiffLine(DiffLineKind.Added, null, 3, string.Empty), lines[3]);
        Assert.Equal(new DiffLine(DiffLineKind.Context, 3, 4, "def sub(a, b):"), lines[4]);

        var second = file.Hunks[1];
        Assert.Equal("def mul(a, b):", second.SectionHeader);
        Assert.Equal(12, second.Lines.Single(l => l.Kind == DiffLineKind.Added).NewLineNumber);

        Assert.Equal(3, file.Additions);
        Assert.Equal(1, file.Deletions);
        Assert.Contains(12, file.AddedNewLines);
        Assert.Contains(13, file.VisibleNewLines);
        Assert.DoesNotContain(14, file.VisibleNewLines);
    }

    [Fact]
    public void Ignores_no_newline_marker_and_counts_added_file()
    {
        var readme = UnifiedDiffParser.Parse(MultiFileDiff).Files[1];
        Assert.Equal(2, readme.Additions);
        Assert.Null(readme.OldPath);
    }

    [Fact]
    public void Removed_line_starting_with_dashes_is_not_mistaken_for_a_file_header()
    {
        const string diff = """
            diff --git a/q.sql b/q.sql
            --- a/q.sql
            +++ b/q.sql
            @@ -1,2 +1,2 @@
            --- a comment
            +-- a better comment
             SELECT 1;
            """;

        var file = Assert.Single(UnifiedDiffParser.Parse(diff).Files);
        Assert.Equal(1, file.Deletions);
        Assert.Equal(1, file.Additions);
        Assert.Equal("-- a comment", file.Hunks[0].Lines[0].Text);
    }

    [Fact]
    public void Parses_plain_unified_diff_without_git_header()
    {
        const string diff = """
            --- a/one.txt	2024-01-01
            +++ b/one.txt	2024-01-02
            @@ -1 +1 @@
            -a
            +b
            --- a/two.txt
            +++ b/two.txt
            @@ -1 +1,2 @@
             x
            +y
            """;

        var parsed = UnifiedDiffParser.Parse(diff);
        Assert.Equal(["one.txt", "two.txt"], parsed.Files.Select(f => f.Path));
        Assert.Equal(2, parsed.TotalAdditions);
    }

    [Fact]
    public void Ignores_preamble_and_handles_crlf()
    {
        var diff = "From abc Mon Sep 17 00:00:00 2001\r\nSubject: [PATCH] fix\r\n\r\ndiff --git a/x b/x\r\n--- a/x\r\n+++ b/x\r\n@@ -1 +1 @@\r\n-1\r\n+2\r\n";
        var file = Assert.Single(UnifiedDiffParser.Parse(diff).Files);
        Assert.Equal("2", file.Hunks[0].Lines[1].Text);
    }

    [Fact]
    public void Returns_no_files_for_non_diff_text()
    {
        Assert.Empty(UnifiedDiffParser.Parse("hello world\nthis is not a diff").Files);
    }

    [Fact]
    public void Annotated_render_shows_new_line_numbers_in_gutter()
    {
        var file = UnifiedDiffParser.Parse(MultiFileDiff).Files[0];
        var rendered = UnifiedDiffParser.RenderAnnotated(file);

        Assert.Contains("### src/calc.py  [+3 -1]", rendered);
        Assert.Contains("    2 +     return a + b", rendered);
        Assert.Contains("      -     return a - b", rendered);
        Assert.Contains("   12 +     y = 2", rendered);
    }

    [Theory]
    [InlineData("a/src/calc.py")]
    [InlineData("b/src/calc.py")]
    [InlineData("./src/calc.py")]
    [InlineData("/src/calc.py")]
    [InlineData("src/calc.py")]
    public void FindFile_tolerates_common_prefixes(string path)
    {
        Assert.Equal("src/calc.py", UnifiedDiffParser.Parse(MultiFileDiff).FindFile(path)?.Path);
    }

    [Fact]
    public void All_bundled_samples_parse_with_consistent_hunk_sizes()
    {
        var samples = Directory.GetFiles(TestPaths.SamplesDirectory, "*.diff");
        Assert.NotEmpty(samples);
        foreach (var path in samples)
        {
            var diff = UnifiedDiffParser.Parse(File.ReadAllText(path));
            Assert.NotEmpty(diff.Files);
            foreach (var hunk in diff.Files.SelectMany(f => f.Hunks))
            {
                Assert.Equal(hunk.OldCount, hunk.Lines.Count(l => l.Kind != DiffLineKind.Added));
                Assert.Equal(hunk.NewCount, hunk.Lines.Count(l => l.Kind != DiffLineKind.Removed));
            }
        }
    }
}

internal static class TestPaths
{
    public static string SamplesDirectory
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "samples")))
            {
                dir = dir.Parent;
            }

            return dir is null ? throw new DirectoryNotFoundException("samples/ not found") : Path.Combine(dir.FullName, "samples");
        }
    }
}
