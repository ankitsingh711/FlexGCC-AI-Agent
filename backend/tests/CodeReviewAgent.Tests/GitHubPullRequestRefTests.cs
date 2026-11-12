using CodeReviewAgent.Core.Sources;

namespace CodeReviewAgent.Tests;

public class GitHubPullRequestRefTests
{
    [Theory]
    [InlineData("https://github.com/dotnet/runtime/pull/123", "dotnet", "runtime", 123)]
    [InlineData("https://github.com/owner/repo.name/pull/7/files", "owner", "repo.name", 7)]
    [InlineData("https://GitHub.com/a-b/c_d/pull/1?diff=split#top", "a-b", "c_d", 1)]
    [InlineData("  https://github.com/o/r/pull/42  ", "o", "r", 42)]
    public void Accepts_valid_pull_request_urls(string url, string owner, string repo, int number)
    {
        Assert.True(GitHubPullRequestRef.TryParse(url, out var pr, out var error), error);
        Assert.Equal(new GitHubPullRequestRef(owner, repo, number), pr);
    }

    [Theory]
    [InlineData("http://github.com/o/r/pull/1")]
    [InlineData("https://github.com.evil.com/o/r/pull/1")]
    [InlineData("https://evil.com/github.com/o/r/pull/1")]
    [InlineData("https://github.com:8443/o/r/pull/1")]
    [InlineData("https://user@169.254.169.254/o/r/pull/1")]
    [InlineData("https://github.com/o/r/issues/1")]
    [InlineData("https://github.com/o/r/pull/abc")]
    [InlineData("https://github.com/o/r/pull/-1")]
    [InlineData("https://github.com/o/r/pull/0")]
    [InlineData("https://github.com/o/../pull/1")]
    [InlineData("https://github.com/o%2F..%2Fx/r/pull/1")]
    [InlineData("github.com/o/r/pull/1")]
    [InlineData("")]
    [InlineData(null)]
    public void Rejects_anything_else(string? url)
    {
        Assert.False(GitHubPullRequestRef.TryParse(url, out var pr, out var error));
        Assert.Null(pr);
        Assert.False(string.IsNullOrEmpty(error));
    }
}
