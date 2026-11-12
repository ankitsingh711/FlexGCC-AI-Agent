using CodeReviewAgent.Api.Contracts;

namespace CodeReviewAgent.Api.Services;

/// <summary>
/// Sample diffs shipped in /samples. Each file may start with "# Title:" and "# Description:" lines, which the diff
/// parser ignores as preamble. Lookups go through the enumerated catalog, so a name can never become a path.
/// </summary>
public sealed class SampleCatalog
{
    private readonly Dictionary<string, (SampleDto Meta, string Path)> _samples;

    public SampleCatalog(string directory)
    {
        _samples = Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*.diff")
                .OrderBy(p => p, StringComparer.Ordinal)
                .Select(p => (Meta: ReadMeta(p), Path: p))
                .ToDictionary(x => x.Meta.Name, StringComparer.OrdinalIgnoreCase)
            : [];
    }

    public IReadOnlyList<SampleDto> List() => _samples.Values.Select(s => s.Meta).ToList();

    public string? Read(string name) => _samples.TryGetValue(name, out var s) ? File.ReadAllText(s.Path) : null;

    private static SampleDto ReadMeta(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        string title = name, description = string.Empty;
        foreach (var line in File.ReadLines(path).Take(5))
        {
            if (line.StartsWith("# Title:", StringComparison.Ordinal))
            {
                title = line["# Title:".Length..].Trim();
            }
            else if (line.StartsWith("# Description:", StringComparison.Ordinal))
            {
                description = line["# Description:".Length..].Trim();
            }
        }

        return new SampleDto(name, title, description);
    }
}
