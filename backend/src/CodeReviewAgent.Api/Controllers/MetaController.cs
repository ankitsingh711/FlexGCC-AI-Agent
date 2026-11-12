using CodeReviewAgent.Api.Contracts;
using CodeReviewAgent.Api.Services;
using CodeReviewAgent.Core.Agent;
using CodeReviewAgent.Core.Llm;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace CodeReviewAgent.Api.Controllers;

[ApiController]
[Route("api")]
public sealed class MetaController(SampleCatalog samples, ProviderSelection provider, IOptions<AgentOptions> agent, IConfiguration config) : ControllerBase
{
    [HttpGet("health")]
    public HealthDto Health() => new(
        "ok",
        provider.Provider,
        provider.Model,
        provider.IsConfigured,
        provider.ApiKeyEnvVar,
        !string.IsNullOrWhiteSpace(config["GITHUB_TOKEN"] ?? config["GitHub:Token"]),
        agent.Value.MaxTurns);

    [HttpGet("samples")]
    public IReadOnlyList<SampleDto> Samples() => samples.List();

    [HttpGet("samples/{name}")]
    [Produces("text/plain")]
    public IActionResult Sample(string name)
    {
        var diff = samples.Read(name);
        return diff is null ? NotFound() : Content(diff, "text/plain");
    }
}
