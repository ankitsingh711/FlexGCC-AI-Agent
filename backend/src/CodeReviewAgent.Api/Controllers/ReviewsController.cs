using System.Text.Json;
using CodeReviewAgent.Api.Contracts;
using CodeReviewAgent.Api.Services;
using CodeReviewAgent.Core.Agent;
using CodeReviewAgent.Core.Models;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace CodeReviewAgent.Api.Controllers;

[ApiController]
[Route("api/reviews")]
public sealed class ReviewsController(ReviewService reviews) : ControllerBase
{
    /// <summary>Runs a review and returns the complete result.</summary>
    [HttpPost]
    [EnableRateLimiting("reviews")]
    [ProducesResponseType<ReviewRunDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create([FromBody] CreateReviewRequest request, CancellationToken ct)
    {
        try
        {
            var run = await reviews.RunAsync(request, onEvent: null, ct);
            return Ok(run);
        }
        catch (ReviewFailedException ex)
        {
            return Problem(title: ex.Title, detail: ex.Message, statusCode: ex.StatusCode);
        }
    }

    /// <summary>
    /// Runs a review and streams progress as Server-Sent Events: <c>agent</c> events while it works, then exactly
    /// one <c>result</c> (a ReviewRunDto) or <c>error</c> (ProblemDetails) event. POST + fetch streaming is used
    /// instead of EventSource because the request body can be a large diff.
    /// </summary>
    [HttpPost("stream")]
    [EnableRateLimiting("reviews")]
    public async Task Stream([FromBody] CreateReviewRequest request, CancellationToken ct)
    {
        Response.Headers.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";
        HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        async Task Send(string eventName, object payload)
        {
            var json = JsonSerializer.Serialize(payload, ReviewJson.Options);
            await Response.WriteAsync($"event: {eventName}\ndata: {json}\n\n", ct);
            await Response.Body.FlushAsync(ct);
        }

        try
        {
            var run = await reviews.RunAsync(request, e => Send("agent", e), ct);
            await Send("result", run);
        }
        catch (ReviewFailedException ex)
        {
            await Send("error", new ProblemDetails { Title = ex.Title, Detail = ex.Message, Status = ex.StatusCode });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Client disconnected; the run is recorded as failed by the service.
        }
    }

    [HttpGet]
    public async Task<IReadOnlyList<ReviewSummaryDto>> List([FromQuery] int take = 25, CancellationToken ct = default) =>
        await reviews.ListAsync(take, ct);

    [HttpGet("{id:guid}")]
    [ProducesResponseType<ReviewRunDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var run = await reviews.GetAsync(id, ct);
        return run is null ? NotFound() : Ok(run);
    }
}
