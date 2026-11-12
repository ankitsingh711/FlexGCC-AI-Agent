using System.Text.Json;
using Anthropic.Exceptions;
using CodeReviewAgent.Api.Contracts;
using CodeReviewAgent.Api.Data;
using CodeReviewAgent.Core.Agent;
using CodeReviewAgent.Core.Llm;
using CodeReviewAgent.Core.Models;
using CodeReviewAgent.Core.Sources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CodeReviewAgent.Api.Services;

/// <summary>An error with an HTTP status and a message that is safe to show to the user.</summary>
public sealed class ReviewFailedException(int statusCode, string title, string detail, Exception? inner = null)
    : Exception(detail, inner)
{
    public int StatusCode { get; } = statusCode;

    public string Title { get; } = title;
}

/// <summary>Application service: turns a request into a change source, runs the agent, persists the run.</summary>
public sealed class ReviewService(
    ReviewDbContext db,
    CodeReviewAgent.Core.Agent.CodeReviewAgent agent,
    IHttpClientFactory httpClientFactory,
    IOptions<AgentOptions> agentOptions,
    ProviderSelection provider,
    ILogger<ReviewService> logger)
{
    public async Task<ReviewRunDto> RunAsync(CreateReviewRequest request, Func<AgentEvent, Task>? onEvent, CancellationToken ct)
    {
        if (!provider.IsConfigured)
        {
            throw new ReviewFailedException(
                StatusCodes.Status503ServiceUnavailable,
                "Model API key not configured",
                $"Set the {provider.ApiKeyEnvVar} environment variable for the '{provider.Provider}' provider and restart the API.");
        }

        var source = await CreateSourceAsync(request, ct);

        var run = new ReviewRun
        {
            Id = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow,
            Status = ReviewStatus.Running,
            SourceKind = source.Kind,
            SourceLabel = source.Label,
            PullRequestUrl = request.PullRequestUrl?.Trim(),
            DiffText = source.RawDiff,
        };
        db.Reviews.Add(run);
        await db.SaveChangesAsync(ct);

        try
        {
            var result = await agent.ReviewAsync(source, onEvent, ct);
            run.Status = ReviewStatus.Completed;
            run.ResultJson = JsonSerializer.Serialize(result, ReviewJson.Options);
            run.Verdict = JsonSerializer.Serialize(result.Verdict, ReviewJson.Options).Trim('"');
            run.FindingCount = result.Findings.Count;
            run.HighSeverityCount = result.Findings.Count(f => f.Severity is Severity.Critical or Severity.High);
            run.DurationMs = result.DurationMs;
            run.InputTokens = result.Usage.InputTokens;
            run.OutputTokens = result.Usage.OutputTokens;
            run.Model = result.Model;
            await db.SaveChangesAsync(CancellationToken.None);
            return ToDto(run, result);
        }
        catch (Exception ex)
        {
            var failure = MapAgentFailure(ex);
            run.Status = ReviewStatus.Failed;
            run.Error = failure.Message;
            run.DurationMs = (long)(DateTimeOffset.UtcNow - run.CreatedAt).TotalMilliseconds;
            await db.SaveChangesAsync(CancellationToken.None);
            if (ReferenceEquals(failure, ex))
            {
                throw;
            }

            throw failure;
        }
    }

    public async Task<IReadOnlyList<ReviewSummaryDto>> ListAsync(int take, CancellationToken ct) =>
        await db.Reviews.AsNoTracking()
            .OrderByDescending(r => r.CreatedAt)
            .Take(Math.Clamp(take, 1, 100))
            .Select(r => new ReviewSummaryDto(r.Id, r.CreatedAt, r.Status.ToString().ToLower(), r.SourceKind, r.SourceLabel, r.Verdict, r.FindingCount, r.HighSeverityCount, r.DurationMs))
            .ToListAsync(ct);

    public async Task<ReviewRunDto?> GetAsync(Guid id, CancellationToken ct)
    {
        var run = await db.Reviews.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (run is null)
        {
            return null;
        }

        var result = run.ResultJson is null ? null : JsonSerializer.Deserialize<ReviewResult>(run.ResultJson, ReviewJson.Options);
        return ToDto(run, result);
    }

    private async Task<IChangeSource> CreateSourceAsync(CreateReviewRequest request, CancellationToken ct)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(request.Diff))
            {
                return new PastedDiffSource(request.Diff);
            }

            if (!GitHubPullRequestRef.TryParse(request.PullRequestUrl, out var pr, out var error))
            {
                throw new ReviewFailedException(StatusCodes.Status400BadRequest, "Invalid pull request URL", error!);
            }

            var client = new GitHubApiClient(httpClientFactory.CreateClient(GitHubApiClient.HttpClientName));
            return await GitHubPullRequestSource.CreateAsync(client, pr!, agentOptions.Value.MaxDiffChars, ct);
        }
        catch (ChangeSourceException ex)
        {
            throw new ReviewFailedException(StatusCodes.Status422UnprocessableEntity, "Could not load the change", ex.Message, ex);
        }
        catch (HttpRequestException ex)
        {
            throw new ReviewFailedException(StatusCodes.Status502BadGateway, "GitHub unreachable", "Could not reach the GitHub API.", ex);
        }
    }

    /// <summary>Most-specific-first mapping from SDK/agent failures to user-facing errors.</summary>
    private Exception MapAgentFailure(Exception ex)
    {
        logger.LogError(ex, "Review failed");
        return ex switch
        {
            ReviewFailedException => ex,
            OperationCanceledException => ex,
            AgentException a => new ReviewFailedException(StatusCodes.Status502BadGateway, "The review agent could not finish", a.Message, a),
            ModelProviderException { StatusCode: 401 or 403 } p => new ReviewFailedException(StatusCodes.Status503ServiceUnavailable, "Model API key rejected", p.Message, p),
            ModelProviderException { StatusCode: 429 } p => new ReviewFailedException(StatusCodes.Status429TooManyRequests, "Rate limited by the model provider", $"{p.Message} Free tiers have low per-minute limits; wait a minute and retry.", p),
            ModelProviderException { StatusCode: 404 } p => new ReviewFailedException(StatusCodes.Status503ServiceUnavailable, "Model not found", $"{p.Message} Check Agent:Model.", p),
            ModelProviderException p => new ReviewFailedException(StatusCodes.Status502BadGateway, "Model provider error", p.Message, p),
            AnthropicUnauthorizedException => new ReviewFailedException(StatusCodes.Status503ServiceUnavailable, "Anthropic API key rejected", "The configured ANTHROPIC_API_KEY was rejected by the API.", ex),
            AnthropicForbiddenException => new ReviewFailedException(StatusCodes.Status503ServiceUnavailable, "Anthropic API access denied", "The API key does not have access to the configured model.", ex),
            AnthropicNotFoundException => new ReviewFailedException(StatusCodes.Status503ServiceUnavailable, "Model not found", $"Model '{agentOptions.Value.Model}' was not found. Check Agent:Model.", ex),
            AnthropicRateLimitException => new ReviewFailedException(StatusCodes.Status429TooManyRequests, "Rate limited", "The Anthropic API rate limit was hit (after retries). Try again shortly.", ex),
            AnthropicBadRequestException => new ReviewFailedException(StatusCodes.Status502BadGateway, "Model request rejected", ex.Message, ex),
            Anthropic5xxException => new ReviewFailedException(StatusCodes.Status502BadGateway, "Anthropic API error", "The Anthropic API returned a server error (after retries). Try again shortly.", ex),
            AnthropicIOException => new ReviewFailedException(StatusCodes.Status502BadGateway, "Anthropic API unreachable", "Could not reach the Anthropic API.", ex),
            AnthropicApiException => new ReviewFailedException(StatusCodes.Status502BadGateway, "Anthropic API error", ex.Message, ex),
            _ => new ReviewFailedException(StatusCodes.Status500InternalServerError, "Unexpected error", "The review failed unexpectedly. See server logs.", ex),
        };
    }

    private static ReviewRunDto ToDto(ReviewRun run, ReviewResult? result) =>
        new(run.Id, run.CreatedAt, run.Status.ToString().ToLowerInvariant(), run.SourceKind, run.SourceLabel, run.PullRequestUrl, run.Error, result, run.DiffText);
}

