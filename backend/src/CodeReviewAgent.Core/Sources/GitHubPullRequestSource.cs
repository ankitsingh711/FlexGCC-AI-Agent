using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeReviewAgent.Core.Diff;

namespace CodeReviewAgent.Core.Sources;

/// <summary>A strictly parsed reference to a public GitHub pull request.</summary>
public sealed partial record GitHubPullRequestRef(string Owner, string Repo, int Number)
{
    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9._-]{0,99})$")]
    private static partial Regex NamePart();

    public override string ToString() => $"{Owner}/{Repo}#{Number}";

    /// <summary>
    /// Accepts only <c>https://github.com/{owner}/{repo}/pull/{number}</c> (optionally followed by /files etc.).
    /// The host is fixed and owner/repo are restricted to GitHub's allowed characters, so a user-supplied URL can
    /// never redirect our server-side HTTP calls elsewhere (SSRF) or inject path segments.
    /// </summary>
    public static bool TryParse(string? url, out GitHubPullRequestRef? reference, out string? error)
    {
        reference = null;
        error = null;
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            error = "Not a valid absolute URL.";
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttps || !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) || !uri.IsDefaultPort)
        {
            error = "Only https://github.com pull request URLs are supported.";
            return false;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 4 || segments[2] != "pull")
        {
            error = "Expected a URL like https://github.com/{owner}/{repo}/pull/{number}.";
            return false;
        }

        if (!NamePart().IsMatch(segments[0]) || !NamePart().IsMatch(segments[1]) || segments[1] is "." or "..")
        {
            error = "Owner or repository name contains invalid characters.";
            return false;
        }

        if (!int.TryParse(segments[3], out var number) || number <= 0)
        {
            error = "Pull request number must be a positive integer.";
            return false;
        }

        reference = new GitHubPullRequestRef(segments[0], segments[1], number);
        return true;
    }
}

/// <summary>Thin, read-only GitHub REST client. Expects an HttpClient whose BaseAddress is https://api.github.com/.</summary>
public sealed class GitHubApiClient(HttpClient http)
{
    public const string HttpClientName = "github";

    public static void Configure(HttpClient http, string? token)
    {
        http.BaseAddress = new Uri("https://api.github.com/");
        http.Timeout = TimeSpan.FromSeconds(30);
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("code-review-agent", "1.0"));
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        if (!string.IsNullOrWhiteSpace(token))
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }

    public async Task<JsonDocument> GetPullRequestAsync(GitHubPullRequestRef pr, CancellationToken ct)
    {
        using var response = await SendAsync($"repos/{pr.Owner}/{pr.Repo}/pulls/{pr.Number}", "application/vnd.github+json", ct);
        await EnsureSuccessAsync(response, $"pull request {pr}", ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    public async Task<string> GetPullRequestDiffAsync(GitHubPullRequestRef pr, int maxChars, CancellationToken ct)
    {
        using var response = await SendAsync($"repos/{pr.Owner}/{pr.Repo}/pulls/{pr.Number}", "application/vnd.github.diff", ct);
        await EnsureSuccessAsync(response, $"diff for {pr}", ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (text.Length > maxChars)
        {
            throw new ChangeSourceException(
                $"The diff for {pr} is {text.Length:N0} characters, above the {maxChars:N0} limit. Review a smaller PR or raise Agent:MaxDiffChars.");
        }

        return text;
    }

    public async Task<string?> GetFileAsync(GitHubPullRequestRef pr, string sha, string path, CancellationToken ct)
    {
        var encodedPath = string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
        using var response = await SendAsync($"repos/{pr.Owner}/{pr.Repo}/contents/{encodedPath}?ref={Uri.EscapeDataString(sha)}", "application/vnd.github.raw", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, $"file {path}", ct);
        return await response.Content.ReadAsStringAsync(ct);
    }

    public async Task<(IReadOnlyList<string> Paths, bool Truncated)> GetTreeAsync(GitHubPullRequestRef pr, string sha, CancellationToken ct)
    {
        using var response = await SendAsync($"repos/{pr.Owner}/{pr.Repo}/git/trees/{Uri.EscapeDataString(sha)}?recursive=1", "application/vnd.github+json", ct);
        await EnsureSuccessAsync(response, "repository tree", ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var paths = doc.RootElement.GetProperty("tree").EnumerateArray()
            .Where(e => e.GetProperty("type").GetString() == "blob")
            .Select(e => e.GetProperty("path").GetString()!)
            .ToList();
        var truncated = doc.RootElement.TryGetProperty("truncated", out var t) && t.GetBoolean();
        return (paths, truncated);
    }

    private Task<HttpResponseMessage> SendAsync(string relativeUrl, string accept, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, relativeUrl);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        return http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string what, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(ct);
        var message = response.StatusCode switch
        {
            HttpStatusCode.NotFound => $"GitHub returned 404 for {what}. Only public repositories are supported unless GITHUB_TOKEN is set.",
            HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests when body.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
                => "GitHub API rate limit exceeded. Set GITHUB_TOKEN to raise the limit.",
            HttpStatusCode.Unauthorized => "GitHub rejected the configured GITHUB_TOKEN.",
            _ => $"GitHub request for {what} failed with HTTP {(int)response.StatusCode}.",
        };
        throw new ChangeSourceException(message);
    }
}

/// <summary>A public GitHub pull request: diff + PR metadata + read access to files at the head commit.</summary>
public sealed class GitHubPullRequestSource : IChangeSource
{
    private const int MaxCachedFileChars = 2_000_000;

    private readonly GitHubApiClient _client;
    private readonly GitHubPullRequestRef _pr;
    private readonly string _headSha;
    private readonly ConcurrentDictionary<string, Task<string?>> _fileCache = new(StringComparer.Ordinal);
    private Task<(IReadOnlyList<string> Paths, bool Truncated)>? _tree;

    private GitHubPullRequestSource(GitHubApiClient client, GitHubPullRequestRef pr, string headSha, string rawDiff, string? title, string? description)
    {
        _client = client;
        _pr = pr;
        _headSha = headSha;
        RawDiff = rawDiff;
        Diff = UnifiedDiffParser.Parse(rawDiff);
        Title = title;
        Description = description;
    }

    public string Kind => "github_pr";

    public string Label => _pr.ToString();

    public string RawDiff { get; }

    public ParsedDiff Diff { get; }

    public string? Title { get; }

    public string? Description { get; }

    public bool SupportsRepositoryAccess => true;

    public static async Task<GitHubPullRequestSource> CreateAsync(GitHubApiClient client, GitHubPullRequestRef pr, int maxDiffChars, CancellationToken ct)
    {
        using var meta = await client.GetPullRequestAsync(pr, ct);
        var root = meta.RootElement;
        var headSha = root.GetProperty("head").GetProperty("sha").GetString()
            ?? throw new ChangeSourceException("GitHub response did not include a head commit SHA.");
        var title = root.TryGetProperty("title", out var t) ? t.GetString() : null;
        var body = root.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null;

        var diff = await client.GetPullRequestDiffAsync(pr, maxDiffChars, ct);
        var source = new GitHubPullRequestSource(client, pr, headSha, diff, title, body);
        if (source.Diff.Files.Count == 0)
        {
            throw new ChangeSourceException($"{pr} has no reviewable file changes.");
        }

        return source;
    }

    public Task<string?> ReadFileAsync(string path, CancellationToken cancellationToken) =>
        _fileCache.GetOrAdd(path, p => FetchFileAsync(p, cancellationToken));

    public async Task<IReadOnlyList<string>> ListRepositoryFilesAsync(CancellationToken cancellationToken)
    {
        _tree ??= _client.GetTreeAsync(_pr, _headSha, cancellationToken);
        return (await _tree).Paths;
    }

    private async Task<string?> FetchFileAsync(string path, CancellationToken ct)
    {
        var content = await _client.GetFileAsync(_pr, _headSha, path, ct);
        if (content is not null && content.Length > MaxCachedFileChars)
        {
            throw new ChangeSourceException($"{path} is too large to read ({content.Length:N0} characters).");
        }

        return content;
    }
}
