using System.Text.Json;
using Anthropic.Exceptions;
using CodeReviewAgent.Core.Agent;
using CodeReviewAgent.Core.Llm;
using CodeReviewAgent.Core.Models;
using CodeReviewAgent.Core.Sources;
using Microsoft.Extensions.Logging;

// review-agent: run the code review agent from a terminal or CI job.
//   git diff main | dotnet run --project src/CodeReviewAgent.Cli -- --diff -
//   dotnet run --project src/CodeReviewAgent.Cli -- --pr https://github.com/owner/repo/pull/123 --fail-on high
// Exit codes: 0 = ok, 1 = findings at/above --fail-on, 2 = bad input, 3 = agent/API failure.

const int ExitOk = 0, ExitFindings = 1, ExitBadInput = 2, ExitFailure = 3;

string? diffArg = null, prArg = null;
var json = false;
Severity? failOn = null;
var verbose = false;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--diff" when i + 1 < args.Length: diffArg = args[++i]; break;
        case "--pr" when i + 1 < args.Length: prArg = args[++i]; break;
        case "--json": json = true; break;
        case "--verbose": verbose = true; break;
        case "--fail-on" when i + 1 < args.Length:
            try
            {
                failOn = JsonSerializer.Deserialize<Severity>(JsonSerializer.Serialize(args[++i].ToLowerInvariant()));
            }
            catch (JsonException)
            {
                Console.Error.WriteLine($"--fail-on must be one of critical, high, medium, low, info (got '{args[i]}').");
                return ExitBadInput;
            }

            break;
        case "-h" or "--help":
            PrintUsage();
            return ExitOk;
        default:
            Console.Error.WriteLine($"Unknown or incomplete argument: {args[i]}");
            PrintUsage();
            return ExitBadInput;
    }
}

if ((diffArg is null) == (prArg is null))
{
    Console.Error.WriteLine("Specify exactly one of --diff <file|-> or --pr <url>.");
    PrintUsage();
    return ExitBadInput;
}

var options = new AgentOptions { Model = string.Empty };
if (Environment.GetEnvironmentVariable("REVIEW_PROVIDER") is { Length: > 0 } providerName) options.Provider = providerName;
if (Environment.GetEnvironmentVariable("REVIEW_MODEL") is { Length: > 0 } model) options.Model = model;
if (Environment.GetEnvironmentVariable("REVIEW_BASE_URL") is { Length: > 0 } baseUrl) options.BaseUrl = baseUrl;
if (Environment.GetEnvironmentVariable("REVIEW_EFFORT") is { Length: > 0 } effort) options.Effort = effort;

ProviderSelection selection;
try
{
    selection = ProviderPresets.Resolve(options, Environment.GetEnvironmentVariable);
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine(ex.Message);
    return ExitBadInput;
}

if (!selection.IsConfigured)
{
    Console.Error.WriteLine($"{selection.ApiKeyEnvVar} is not set (provider '{selection.Provider}').");
    return ExitBadInput;
}

// Logs go to stderr so stdout stays clean for the report / JSON (pipe-friendly in CI).
using var loggerFactory = LoggerFactory.Create(b => b
    .AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace)
    .AddSimpleConsole(o => o.SingleLine = true)
    .SetMinimumLevel(verbose ? LogLevel.Information : LogLevel.Warning));

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

IChangeSource source;
try
{
    if (diffArg is not null)
    {
        var text = diffArg == "-" ? await Console.In.ReadToEndAsync(cts.Token) : await File.ReadAllTextAsync(diffArg, cts.Token);
        if (text.Length > options.MaxDiffChars)
        {
            Console.Error.WriteLine($"Diff is {text.Length:N0} characters; the limit is {options.MaxDiffChars:N0}.");
            return ExitBadInput;
        }

        source = new PastedDiffSource(text, diffArg == "-" ? "stdin diff" : Path.GetFileName(diffArg));
    }
    else
    {
        if (!GitHubPullRequestRef.TryParse(prArg, out var pr, out var error))
        {
            Console.Error.WriteLine(error);
            return ExitBadInput;
        }

        var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        GitHubApiClient.Configure(http, Environment.GetEnvironmentVariable("GITHUB_TOKEN"));
        source = await GitHubPullRequestSource.CreateAsync(new GitHubApiClient(http), pr!, options.MaxDiffChars, cts.Token);
    }
}
catch (Exception ex) when (ex is ChangeSourceException or FileNotFoundException or HttpRequestException)
{
    Console.Error.WriteLine($"Could not load the change: {ex.Message}");
    return ExitBadInput;
}

using var llmHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
var chatModel = ChatModelFactory.Create(selection, options, llmHttp, loggerFactory);
var agent = new CodeReviewAgent.Core.Agent.CodeReviewAgent(chatModel, options, loggerFactory.CreateLogger<CodeReviewAgent.Core.Agent.CodeReviewAgent>());
Console.Error.WriteLine($"[model] {selection.Provider} · {selection.Model}");

ReviewResult result;
try
{
    result = await agent.ReviewAsync(source, e =>
    {
        var line = e.Type switch
        {
            "tool_call" => $"  → {e.Tool}({e.Message})",
            "tool_result" => $"  ← {e.Tool} {(e.IsError == true ? "ERROR " : string.Empty)}{e.DurationMs}ms",
            "thinking" or "message" => verbose ? $"  · {Flatten(e.Message)}" : null,
            _ => $"[{e.Type}] {e.Message}",
        };
        if (line is not null)
        {
            Console.Error.WriteLine(line);
        }

        return Task.CompletedTask;
    }, cts.Token);
}
catch (AgentException ex)
{
    Console.Error.WriteLine($"Review failed: {ex.Message}");
    return ExitFailure;
}
catch (ModelProviderException ex)
{
    Console.Error.WriteLine($"Model provider error: {ex.Message}");
    return ExitFailure;
}
catch (AnthropicApiException ex)
{
    Console.Error.WriteLine($"Anthropic API error: {ex.Message}");
    return ExitFailure;
}
catch (AnthropicIOException ex)
{
    Console.Error.WriteLine($"Could not reach the Anthropic API: {ex.Message}");
    return ExitFailure;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelled.");
    return ExitFailure;
}

if (json)
{
    Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions(ReviewJson.Options) { WriteIndented = true }));
}
else
{
    PrintReport(result);
}

return failOn is { } threshold && result.Findings.Any(f => f.Severity <= threshold) ? ExitFindings : ExitOk;

static void PrintReport(ReviewResult r)
{
    var verdict = JsonSerializer.Serialize(r.Verdict, ReviewJson.Options).Trim('"');
    Console.WriteLine();
    Console.WriteLine($"Verdict: {verdict.ToUpperInvariant()}   ({r.Findings.Count} finding(s), {r.Stats.FilesChanged} file(s), +{r.Stats.Additions} -{r.Stats.Deletions})");
    Console.WriteLine();
    Console.WriteLine(r.Summary);
    foreach (var group in r.Findings.GroupBy(f => f.Category))
    {
        Console.WriteLine();
        Console.WriteLine($"== {JsonSerializer.Serialize(group.Key, ReviewJson.Options).Trim('"').Replace('_', ' ').ToUpperInvariant()} ==");
        foreach (var f in group)
        {
            var sev = JsonSerializer.Serialize(f.Severity, ReviewJson.Options).Trim('"');
            var loc = f.Line is null ? f.File : $"{f.File}:{f.Line}{(f.EndLine is null ? string.Empty : $"-{f.EndLine}")}";
            Console.WriteLine($"[{sev}] {loc}  {f.Title}");
            Console.WriteLine($"    {f.Explanation.Replace("\n", "\n    ")}");
            if (f.Suggestion is not null)
            {
                Console.WriteLine($"    Suggestion: {f.Suggestion.Replace("\n", "\n    ")}");
            }
        }
    }

    foreach (var w in r.Warnings)
    {
        Console.WriteLine($"(note) {w}");
    }

    Console.WriteLine();
    Console.WriteLine($"{r.Model} · {r.Turns} turn(s) · {r.ToolCalls.Count} tool call(s) · {r.Usage.InputTokens:N0} in / {r.Usage.OutputTokens:N0} out tokens (cache read {r.Usage.CacheReadInputTokens:N0}) · {r.DurationMs / 1000.0:F1}s");
}

static string Flatten(string s) => s.Length > 200 ? s[..200].Replace('\n', ' ') + "…" : s.Replace('\n', ' ');

static void PrintUsage() => Console.Error.WriteLine("""
    Usage:
      review-agent --diff <file|->  [--json] [--fail-on critical|high|medium|low|info] [--verbose]
      review-agent --pr <github-pr-url> [--json] [--fail-on ...] [--verbose]

    Environment:
      REVIEW_PROVIDER  gemini (default) | groq | openrouter | openai-compatible | anthropic
      API key          GEMINI_API_KEY | GROQ_API_KEY | OPENROUTER_API_KEY | LLM_API_KEY | ANTHROPIC_API_KEY
      REVIEW_MODEL, REVIEW_BASE_URL, REVIEW_EFFORT, GITHUB_TOKEN (all optional)
    """);
