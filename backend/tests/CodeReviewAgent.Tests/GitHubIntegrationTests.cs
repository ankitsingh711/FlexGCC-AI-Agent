using CodeReviewAgent.Core.Agent;
using CodeReviewAgent.Core.Sources;

namespace CodeReviewAgent.Tests;

/// <summary>
/// Hits the real GitHub API. Skipped unless RUN_GITHUB_IT=1 so the default test run stays offline and deterministic.
/// </summary>
public class GitHubIntegrationTests
{
    [Fact]
    public async Task Loads_a_public_pull_request_and_reads_files_at_head()
    {
        if (Environment.GetEnvironmentVariable("RUN_GITHUB_IT") != "1")
        {
            return;
        }

        using var http = new HttpClient();
        GitHubApiClient.Configure(http, Environment.GetEnvironmentVariable("GITHUB_TOKEN"));
        Assert.True(GitHubPullRequestRef.TryParse("https://github.com/octocat/Hello-World/pull/1", out var pr, out _));

        var source = await GitHubPullRequestSource.CreateAsync(new GitHubApiClient(http), pr!, 400_000, default);

        Assert.Equal("octocat/Hello-World#1", source.Label);
        Assert.NotEmpty(source.Diff.Files);
        Assert.False(string.IsNullOrEmpty(source.Title));

        var tools = new ReviewTools(source, new AgentOptions());
        var path = source.Diff.Files[0].Path;
        var read = await tools.ExecuteAsync(ReviewTools.ReadFile, Fixtures.Json($$"""{"path":"{{path}}"}"""), default);
        Assert.False(read.IsError, read.Content);
        Assert.Contains("head revision", read.Content);

        var search = await tools.ExecuteAsync(ReviewTools.SearchRepositoryFiles, Fixtures.Json("""{"query":"README"}"""), default);
        Assert.Contains("README", search.Content);

        var missing = await tools.ExecuteAsync(ReviewTools.ReadFile, Fixtures.Json("""{"path":"does/not/exist.txt"}"""), default);
        Assert.True(missing.IsError);
    }
}
