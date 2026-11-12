using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeReviewAgent.Core.Agent;
using CodeReviewAgent.Core.Llm;
using CodeReviewAgent.Core.Models;
using CodeReviewAgent.Core.Sources;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeReviewAgent.Tests;

public class OpenAiCompatibleChatModelTests
{
    private static (OpenAiCompatibleChatModel Model, FakeHandler Handler) Create(params HttpResponseMessage[] responses)
    {
        var handler = new FakeHandler(responses);
        var model = new OpenAiCompatibleChatModel(
            new HttpClient(handler),
            new OpenAiCompatibleSettings("gemini", new Uri("https://example.test/v1beta/openai/"), "test-key", "gemini-test", 4000, "high"),
            NullLogger<OpenAiCompatibleChatModel>.Instance)
        {
            Delay = (_, _) => Task.CompletedTask,
        };
        return (model, handler);
    }

    private static CodeReviewAgent.Core.Agent.CodeReviewAgent Agent(IChatModel model) =>
        new(model, new AgentOptions(), NullLogger<CodeReviewAgent.Core.Agent.CodeReviewAgent>.Instance);

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static string ToolCallResponse(string id, string name, string argumentsJson, string finish = "tool_calls", JsonObject? extra = null)
    {
        var call = new JsonObject
        {
            ["id"] = id,
            ["type"] = "function",
            ["function"] = new JsonObject { ["name"] = name, ["arguments"] = argumentsJson },
        };
        if (extra is not null)
        {
            call["extra_content"] = extra;
        }

        return new JsonObject
        {
            ["model"] = "gemini-test",
            ["choices"] = new JsonArray(new JsonObject
            {
                ["index"] = 0,
                ["finish_reason"] = finish,
                ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = null, ["tool_calls"] = new JsonArray(call) },
            }),
            ["usage"] = new JsonObject
            {
                ["prompt_tokens"] = 1200,
                ["completion_tokens"] = 80,
                ["prompt_tokens_details"] = new JsonObject { ["cached_tokens"] = 200 },
            },
        }.ToJsonString();
    }

    [Fact]
    public void Request_uses_portable_tool_schemas_and_reasoning_effort()
    {
        var (model, _) = Create();
        var specs = new ReviewTools(new PastedDiffSource(Fixtures.CalcDiff), new AgentOptions()).GetSpecs();

        var request = model.BuildRequest("SYSTEM", specs, [new UserMessage("hi")]);

        Assert.Equal("gemini-test", request["model"]!.GetValue<string>());
        Assert.Equal("high", request["reasoning_effort"]!.GetValue<string>());
        Assert.Equal("system", request["messages"]![0]!["role"]!.GetValue<string>());
        var json = request.ToJsonString();
        Assert.DoesNotContain("additionalProperties", json);
        Assert.DoesNotContain("anyOf", json);

        var submit = request["tools"]!.AsArray().Single(t => t!["function"]!["name"]!.GetValue<string>() == "submit_review")!;
        var item = submit["function"]!["parameters"]!["properties"]!["findings"]!["items"]!;
        var required = item["required"]!.AsArray().Select(r => r!.GetValue<string>()).ToList();
        Assert.DoesNotContain("line", required);          // nullable → optional
        Assert.Contains("title", required);
        Assert.Equal("integer", item["properties"]!["line"]!["type"]!.GetValue<string>());
        Assert.NotNull(item["properties"]!["line"]!["description"]); // description survives unwrapping
    }

    [Fact]
    public async Task Agent_runs_end_to_end_and_echoes_provider_extras_back()
    {
        var signature = new JsonObject { ["google"] = new JsonObject { ["thought_signature"] = "sig-abc" } };
        var (model, handler) = Create(
            Json(ToolCallResponse("call_1", "get_file_diff", """{"path":"src/calc.py"}""", extra: signature)),
            Json(ToolCallResponse("call_2", "submit_review", Fixtures.Review("request_changes", Fixtures.Finding()))));

        var result = await Agent(model).ReviewAsync(new PastedDiffSource(Fixtures.CalcDiff));

        Assert.Equal(ReviewVerdict.RequestChanges, result.Verdict);
        Assert.Equal(5, Assert.Single(result.Findings).Line);
        Assert.Equal("gemini-test", result.Model);
        Assert.Equal(2000, result.Usage.InputTokens);           // (1200 - 200) × 2
        Assert.Equal(400, result.Usage.CacheReadInputTokens);

        var second = JsonNode.Parse(handler.Bodies[1])!["messages"]!.AsArray();
        var assistant = second.Single(m => m!["role"]!.GetValue<string>() == "assistant")!;
        Assert.Equal("sig-abc", assistant["tool_calls"]![0]!["extra_content"]!["google"]!["thought_signature"]!.GetValue<string>());
        var tool = second.Single(m => m!["role"]!.GetValue<string>() == "tool")!;
        Assert.Equal("call_1", tool["tool_call_id"]!.GetValue<string>());
        Assert.Contains("return a / b", tool["content"]!.GetValue<string>());
        Assert.Equal("Bearer test-key", handler.AuthHeaders[0]);
        Assert.EndsWith("/v1beta/openai/chat/completions", handler.Urls[0]);
    }

    [Fact]
    public async Task Invalid_tool_arguments_are_returned_as_an_error_not_executed()
    {
        var (model, handler) = Create(
            Json(ToolCallResponse("call_1", "get_file_diff", """{"path": "src/calc.py" """)),
            Json(ToolCallResponse("call_2", "submit_review", Fixtures.Review("approve"))));

        var result = await Agent(model).ReviewAsync(new PastedDiffSource(Fixtures.CalcDiff));

        Assert.True(result.ToolCalls[0].IsError);
        var tool = JsonNode.Parse(handler.Bodies[1])!["messages"]!.AsArray().Single(m => m!["role"]!.GetValue<string>() == "tool")!;
        Assert.Contains("INVALID_JSON", tool["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task Rate_limits_are_retried_honouring_retry_after()
    {
        var limited = Json("""{"error":{"message":"Resource exhausted"}}""", HttpStatusCode.TooManyRequests);
        limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
        var delays = new List<TimeSpan>();
        var handler = new FakeHandler([limited, Json(ToolCallResponse("c", "submit_review", Fixtures.Review("approve")))]);
        var model = new OpenAiCompatibleChatModel(
            new HttpClient(handler),
            new OpenAiCompatibleSettings("gemini", new Uri("https://example.test/"), "k", "m", 4000, null),
            NullLogger<OpenAiCompatibleChatModel>.Instance)
        {
            Delay = (d, _) => { delays.Add(d); return Task.CompletedTask; },
        };

        var result = await Agent(model).ReviewAsync(new PastedDiffSource(Fixtures.CalcDiff));

        Assert.Equal(ReviewVerdict.Approve, result.Verdict);
        Assert.Equal(2, handler.Bodies.Count);
        Assert.Equal(TimeSpan.FromSeconds(7), Assert.Single(delays));
    }

    [Fact]
    public async Task Exhausted_rate_limit_surfaces_a_clear_provider_error()
    {
        var responses = Enumerable.Range(0, 5).Select(_ => Json("""{"error":{"message":"Quota exceeded"}}""", HttpStatusCode.TooManyRequests)).ToArray();
        var (model, _) = Create(responses);

        var ex = await Assert.ThrowsAsync<ModelProviderException>(() => Agent(model).ReviewAsync(new PastedDiffSource(Fixtures.CalcDiff)));

        Assert.Equal(429, ex.StatusCode);
        Assert.Contains("Quota exceeded", ex.Message);
    }

    private static OpenAiCompatibleChatModel WithFallbacks(FakeHandler handler, List<TimeSpan> delays, Func<DateTimeOffset>? clock = null) =>
        new(new HttpClient(handler),
            new OpenAiCompatibleSettings("gemini", new Uri("https://example.test/"), "k", "primary", 4000, null, FallbackModels: ["fallback-1", "fallback-2"]),
            NullLogger<OpenAiCompatibleChatModel>.Instance)
        {
            Delay = (d, _) => { delays.Add(d); return Task.CompletedTask; },
            Clock = clock ?? (() => DateTimeOffset.UtcNow),
        };

    private static string ModelOf(string body) => JsonNode.Parse(body)!["model"]!.GetValue<string>();

    private static HttpResponseMessage Overloaded() =>
        Json("""[{"error":{"code":503,"message":"This model is currently experiencing high demand."}}]""", HttpStatusCode.ServiceUnavailable);

    [Fact]
    public async Task Overloaded_model_hands_the_request_to_the_next_model_without_waiting()
    {
        var delays = new List<TimeSpan>();
        var handler = new FakeHandler([Overloaded(), Json(ToolCallResponse("c", "submit_review", Fixtures.Review("approve")))]);
        var events = new List<AgentEvent>();

        await Agent(WithFallbacks(handler, delays)).ReviewAsync(new PastedDiffSource(Fixtures.CalcDiff), e => { events.Add(e); return Task.CompletedTask; });

        Assert.Equal(["primary", "fallback-1"], handler.Bodies.Select(ModelOf));
        Assert.Empty(delays);
        Assert.Contains(events, e => e.Type == "status" && e.Message == "primary was overloaded; answered by fallback-1");
    }

    [Fact]
    public async Task Overloaded_model_is_tried_last_until_its_cooldown_ends()
    {
        var now = DateTimeOffset.UtcNow;
        var delays = new List<TimeSpan>();
        var ok = () => Json(ToolCallResponse("c", "submit_review", Fixtures.Review("approve")));
        var handler = new FakeHandler([Overloaded(), ok(), ok(), ok()]);
        var agent = Agent(WithFallbacks(handler, delays, () => now));

        await agent.ReviewAsync(new PastedDiffSource(Fixtures.CalcDiff));
        await agent.ReviewAsync(new PastedDiffSource(Fixtures.CalcDiff));          // primary is cooling down
        now += OpenAiCompatibleChatModel.OverloadCooldown + TimeSpan.FromSeconds(1);
        await agent.ReviewAsync(new PastedDiffSource(Fixtures.CalcDiff));          // cooldown over

        Assert.Equal(["primary", "fallback-1", "fallback-1", "primary"], handler.Bodies.Select(ModelOf));
    }

    [Fact]
    public async Task When_every_model_is_overloaded_it_backs_off_and_then_reports_the_error()
    {
        var delays = new List<TimeSpan>();
        var handler = new FakeHandler(Enumerable.Range(0, 15).Select(_ => Overloaded()));

        var ex = await Assert.ThrowsAsync<ModelProviderException>(() => Agent(WithFallbacks(handler, delays)).ReviewAsync(new PastedDiffSource(Fixtures.CalcDiff)));

        Assert.Equal(503, ex.StatusCode);
        Assert.Contains("high demand", ex.Message);
        Assert.Equal(15, handler.Bodies.Count);          // 3 models x (1 try + 4 retries)
        Assert.Equal(4, delays.Count);                   // backoff only between full passes
    }

    [Fact]
    public async Task Rejected_key_is_not_retried()
    {
        var (model, handler) = Create(Json("""[{"error":{"code":401,"message":"API key not valid"}}]""", HttpStatusCode.Unauthorized));

        var ex = await Assert.ThrowsAsync<ModelProviderException>(() => Agent(model).ReviewAsync(new PastedDiffSource(Fixtures.CalcDiff)));

        Assert.Equal(401, ex.StatusCode);
        Assert.Contains("rejected the API key", ex.Message);
        Assert.Single(handler.Bodies);
    }

    [Fact]
    public async Task Gemini_style_bad_key_400_is_reported_as_a_rejected_key()
    {
        // Real response shape from generativelanguage.googleapis.com for an invalid key.
        var (model, _) = Create(Json("""[{"error":{"code":400,"message":"Please pass a valid API key","status":"INVALID_ARGUMENT"}}]""", HttpStatusCode.BadRequest));

        var ex = await Assert.ThrowsAsync<ModelProviderException>(() => Agent(model).ReviewAsync(new PastedDiffSource(Fixtures.CalcDiff)));

        Assert.Equal(401, ex.StatusCode);
        Assert.Equal("gemini rejected the API key: Please pass a valid API key", ex.Message);
    }

    [Fact]
    public void Parses_text_reasoning_and_length_stop()
    {
        var (model, _) = Create();
        using var doc = JsonDocument.Parse("""
            {"choices":[{"finish_reason":"length","message":{"role":"assistant","content":"Partial","reasoning":"Thinking about it"}}]}
            """);

        var turn = model.ParseResponse(doc.RootElement);

        Assert.Equal(TurnStop.MaxTokens, turn.Stop);
        Assert.Equal(["Partial"], turn.Texts);
        Assert.Equal(["Thinking about it"], turn.Thoughts);
        Assert.Equal("gemini-test", turn.Model);
        Assert.Null(((JsonObject)turn.AssistantState)["reasoning"]); // response-only field not echoed back
    }

    [Fact]
    public void Missing_tool_call_ids_are_synthesized()
    {
        var (model, _) = Create();
        using var doc = JsonDocument.Parse("""
            {"choices":[{"finish_reason":"stop","message":{"role":"assistant","tool_calls":[{"type":"function","function":{"name":"list_changed_files","arguments":"{}"}}]}}]}
            """);

        var turn = model.ParseResponse(doc.RootElement);

        Assert.Equal(TurnStop.ToolUse, turn.Stop);
        Assert.Equal("call_0", Assert.Single(turn.ToolCalls).Id);
        Assert.Equal("call_0", ((JsonObject)turn.AssistantState)["tool_calls"]![0]!["id"]!.GetValue<string>());
    }

    private sealed class FakeHandler(IEnumerable<HttpResponseMessage> responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);

        public List<string> Bodies { get; } = [];

        public List<string> Urls { get; } = [];

        public List<string?> AuthHeaders { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            Urls.Add(request.RequestUri!.ToString());
            AuthHeaders.Add(request.Headers.Authorization?.ToString());
            return _responses.Count > 0 ? _responses.Dequeue() : throw new InvalidOperationException("No more responses.");
        }
    }
}

public class ProviderPresetsTests
{
    private static ProviderSelection Resolve(string provider, string model = "", string? baseUrl = null, Dictionary<string, string>? env = null) =>
        ProviderPresets.Resolve(new AgentOptions { Provider = provider, Model = model, BaseUrl = baseUrl }, k => env?.GetValueOrDefault(k));

    [Fact]
    public void Gemini_is_the_default_with_its_own_key()
    {
        var s = ProviderPresets.Resolve(new AgentOptions(), k => k == "GEMINI_API_KEY" ? "g" : null);

        Assert.Equal("gemini", s.Provider);
        Assert.False(s.IsAnthropic);
        Assert.Equal("gemini-3.8-flash", s.Model); // default claude-* model id is replaced by the preset's
        Assert.Equal("GEMINI_API_KEY", s.ApiKeyEnvVar);
        Assert.True(s.IsConfigured);
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/openai/", s.BaseUrl!.ToString());
    }

    [Fact]
    public void Gemini_has_default_fallback_models_that_can_be_overridden_or_turned_off()
    {
        Assert.Equal(["gemini-3.6-flash", "gemini-3.5-flash-lite"], Resolve("gemini").FallbackModels);

        var custom = ProviderPresets.Resolve(new AgentOptions { Provider = "gemini", Model = "gemini-3.6-flash", FallbackModels = ["gemini-3.6-flash", " x ", "x", ""] }, _ => null);
        Assert.Equal(["x"], custom.FallbackModels);   // trimmed, de-duplicated, never the main model

        Assert.Empty(ProviderPresets.Resolve(new AgentOptions { Provider = "gemini", FallbackModels = [] }, _ => null).FallbackModels!);
        Assert.Empty(Resolve("groq").FallbackModels!);
    }

    [Fact]
    public void Anthropic_uses_claude_and_its_key()
    {
        var s = Resolve("anthropic");
        Assert.True(s.IsAnthropic);
        Assert.Equal("claude-opus-5-5", s.Model);
        Assert.False(s.IsConfigured);
        Assert.Equal("ANTHROPIC_API_KEY", s.ApiKeyEnvVar);
    }

    [Fact]
    public void Explicit_model_and_base_url_override_the_preset()
    {
        var s = Resolve("openai-compatible", "qwen2.5-coder:14b", "http://localhost:11434/v1", new() { ["LLM_API_KEY"] = "x" });
        Assert.Equal("qwen2.5-coder:14b", s.Model);
        Assert.Equal("http://localhost:11434/v1/", s.BaseUrl!.ToString());
        Assert.Null(s.ReasoningEffort);
    }

    [Theory]
    [InlineData("nope", "", null)]
    [InlineData("openrouter", "", null)]            // no default model for OpenRouter
    [InlineData("openai-compatible", "m", null)]    // no base URL
    public void Invalid_configuration_is_reported(string provider, string model, string? baseUrl)
    {
        Assert.Throws<InvalidOperationException>(() => Resolve(provider, model, baseUrl));
    }
}
