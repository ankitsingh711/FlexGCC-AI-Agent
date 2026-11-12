using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeReviewAgent.Core.Agent;
using CodeReviewAgent.Core.Models;
using Microsoft.Extensions.Logging;

namespace CodeReviewAgent.Core.Llm;

/// <summary>Settings for an OpenAI-compatible Chat Completions endpoint.</summary>
public sealed record OpenAiCompatibleSettings(
    string Provider,
    Uri BaseUrl,
    string ApiKey,
    string Model,
    int MaxTokens,
    string? ReasoningEffort,
    int MaxRetries = 4,
    TimeSpan? MaxRetryDelay = null);

/// <summary>
/// Any OpenAI-compatible Chat Completions endpoint: Google Gemini, Groq, OpenRouter, a local Ollama, etc.
///
/// Differences from the Claude provider, and how they are handled:
/// <list type="bullet">
/// <item>Strict tool schemas are not guaranteed across providers, so schemas are simplified to the portable subset
/// and the agent's <see cref="FindingValidator"/> catches what the model gets wrong.</item>
/// <item>Tool arguments arrive as a JSON string; unparseable arguments are surfaced as <see cref="ToolCall.ParseError"/>
/// so the loop can return an error result instead of running the tool.</item>
/// <item>Free tiers rate-limit aggressively: 429 and 5xx responses are retried with backoff, honouring Retry-After.</item>
/// <item>The assistant message is echoed back verbatim (its tool_calls included), so provider-specific fields such as
/// Gemini's thought signatures survive the round trip.</item>
/// </list>
/// </summary>
public sealed class OpenAiCompatibleChatModel(HttpClient http, OpenAiCompatibleSettings settings, ILogger<OpenAiCompatibleChatModel> logger) : IChatModel
{
    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };

    private JsonArray? _toolCache;
    private IReadOnlyList<ToolSpec>? _toolCacheKey;

    public string Provider => settings.Provider;

    public string Model => settings.Model;

    /// <summary>Overridable for tests so retries don't actually sleep.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    public async Task<ModelTurn> CompleteAsync(string system, IReadOnlyList<ToolSpec> tools, IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken)
    {
        var body = BuildRequest(system, tools, messages).ToJsonString(Compact);
        using var doc = await SendWithRetryAsync(body, cancellationToken);
        return ParseResponse(doc.RootElement);
    }

    public JsonObject BuildRequest(string system, IReadOnlyList<ToolSpec> tools, IReadOnlyList<ChatMessage> history)
    {
        var messages = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = system } };
        foreach (var m in history)
        {
            switch (m)
            {
                case UserMessage u:
                    messages.Add(new JsonObject { ["role"] = "user", ["content"] = u.Text });
                    break;
                case AssistantMessage a:
                    messages.Add(((JsonObject)a.ProviderState).DeepClone());
                    break;
                case ToolResultsMessage t:
                    foreach (var r in t.Results)
                    {
                        messages.Add(new JsonObject
                        {
                            ["role"] = "tool",
                            ["tool_call_id"] = r.ToolCallId,
                            ["content"] = r.IsError ? $"ERROR: {r.Content}" : r.Content,
                        });
                    }

                    if (!string.IsNullOrEmpty(t.Note))
                    {
                        messages.Add(new JsonObject { ["role"] = "user", ["content"] = t.Note });
                    }

                    break;
            }
        }

        var request = new JsonObject
        {
            ["model"] = settings.Model,
            ["messages"] = messages,
            ["tools"] = ToolDefinitions(tools).DeepClone(),
            ["tool_choice"] = "auto",
            ["max_tokens"] = settings.MaxTokens,
        };
        if (!string.IsNullOrEmpty(settings.ReasoningEffort))
        {
            request["reasoning_effort"] = settings.ReasoningEffort;
        }

        return request;
    }

    public ModelTurn ParseResponse(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
        {
            throw new ModelProviderException(settings.Provider, 502, $"{settings.Provider} returned no choices.");
        }

        var choice = choices[0];
        var message = choice.GetProperty("message");
        var finish = choice.TryGetProperty("finish_reason", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;

        var texts = new List<string>();
        if (message.TryGetProperty("content", out var content))
        {
            if (content.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(content.GetString()))
            {
                texts.Add(content.GetString()!);
            }
            else if (content.ValueKind == JsonValueKind.Array)
            {
                texts.AddRange(content.EnumerateArray()
                    .Where(p => p.TryGetProperty("text", out _))
                    .Select(p => p.GetProperty("text").GetString() ?? string.Empty)
                    .Where(t => t.Length > 0));
            }
        }

        var thoughts = new List<string>();
        foreach (var key in new[] { "reasoning_content", "reasoning" })
        {
            if (message.TryGetProperty(key, out var r) && r.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(r.GetString()))
            {
                thoughts.Add(r.GetString()!);
            }
        }

        var calls = new List<ToolCall>();
        var echoCalls = new JsonArray();
        if (message.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var tc in toolCalls.EnumerateArray())
            {
                var raw = JsonNode.Parse(tc.GetRawText())!.AsObject();
                // Some providers omit ids; the follow-up tool message must reference one, so synthesize it.
                var id = tc.TryGetProperty("id", out var idEl) && idEl.GetString() is { Length: > 0 } s ? s : $"call_{index}";
                raw["id"] = id;
                raw["type"] ??= "function";
                echoCalls.Add(raw);

                var fn = tc.GetProperty("function");
                var name = fn.GetProperty("name").GetString() ?? string.Empty;
                calls.Add(ParseArguments(id, name, fn.TryGetProperty("arguments", out var args) ? args : default));
                index++;
            }
        }

        // Echo only what a request message may carry: role, content, tool_calls (with any provider extras inside them),
        // and a message-level extra_content if the provider uses one. Response-only fields (refusal, reasoning, annotations)
        // are dropped because some endpoints reject them as input.
        var echo = new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = texts.Count > 0 ? string.Join("\n", texts) : null,
        };
        if (echoCalls.Count > 0)
        {
            echo["tool_calls"] = echoCalls;
        }

        if (message.TryGetProperty("extra_content", out var extra))
        {
            echo["extra_content"] = JsonNode.Parse(extra.GetRawText());
        }

        var stop = finish switch
        {
            "length" => TurnStop.MaxTokens,
            "content_filter" => TurnStop.Refusal,
            _ when calls.Count > 0 => TurnStop.ToolUse,
            _ => TurnStop.EndTurn,
        };

        return new ModelTurn(
            root.TryGetProperty("model", out var model) && model.GetString() is { Length: > 0 } ms ? ms : settings.Model,
            stop,
            texts,
            thoughts,
            calls,
            ParseUsage(root),
            echo,
            stop == TurnStop.Refusal ? "content_filter" : null);
    }

    private static ToolCall ParseArguments(string id, string name, JsonElement args)
    {
        try
        {
            var json = args.ValueKind switch
            {
                JsonValueKind.String => args.GetString() is { Length: > 0 } s ? s : "{}",
                JsonValueKind.Object => args.GetRawText(), // a few providers send an object instead of a string
                _ => "{}",
            };
            var input = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)
                ?? throw new JsonException("arguments were null");
            return new ToolCall(id, name, input);
        }
        catch (JsonException ex)
        {
            var raw = args.ValueKind == JsonValueKind.String ? args.GetString() : args.GetRawText();
            return new ToolCall(id, name, new Dictionary<string, JsonElement>(), $"Arguments are not valid JSON ({ex.Message}): {Truncate(raw, 300)}");
        }
    }

    private static TokenUsage ParseUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var u) || u.ValueKind != JsonValueKind.Object)
        {
            return TokenUsage.Zero;
        }

        long Get(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.TryGetInt64(out var n) ? n : 0;
        var prompt = Get(u, "prompt_tokens");
        var completion = Get(u, "completion_tokens");
        var cached = u.TryGetProperty("prompt_tokens_details", out var d) && d.ValueKind == JsonValueKind.Object ? Get(d, "cached_tokens") : 0;

        // Match Anthropic semantics: InputTokens excludes cache reads.
        return new TokenUsage(Math.Max(0, prompt - cached), completion, cached, 0);
    }

    private async Task<JsonDocument> SendWithRetryAsync(string body, CancellationToken ct)
    {
        var maxDelay = settings.MaxRetryDelay ?? TimeSpan.FromSeconds(30);
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(settings.BaseUrl, "chat/completions"))
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);

            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, ct);
            }
            catch (HttpRequestException ex) when (attempt < settings.MaxRetries)
            {
                logger.LogWarning(ex, "{Provider} request failed (attempt {Attempt}); retrying", settings.Provider, attempt + 1);
                await Delay(Backoff(attempt, null, maxDelay), ct);
                continue;
            }
            catch (HttpRequestException ex)
            {
                throw new ModelProviderException(settings.Provider, 0, $"Could not reach {settings.Provider}: {ex.Message}", ex);
            }

            using (response)
            {
                var text = await response.Content.ReadAsStringAsync(ct);
                if (response.IsSuccessStatusCode)
                {
                    return JsonDocument.Parse(text);
                }

                var status = (int)response.StatusCode;
                var retryable = status is 408 or 429 or >= 500;
                if (retryable && attempt < settings.MaxRetries)
                {
                    var delay = Backoff(attempt, response.Headers.RetryAfter, maxDelay);
                    logger.LogWarning("{Provider} returned {Status}; retrying in {Delay}s (attempt {Attempt})", settings.Provider, status, delay.TotalSeconds, attempt + 1);
                    await Delay(delay, ct);
                    continue;
                }

                var detail = ErrorDetail(text);
                // Gemini reports a bad key as 400 INVALID_ARGUMENT ("Please pass a valid API key"); treat it as 401.
                if (status == 400 && detail?.Contains("API key", StringComparison.OrdinalIgnoreCase) == true)
                {
                    status = 401;
                }

                throw new ModelProviderException(settings.Provider, status, Describe((HttpStatusCode)status, detail));
            }
        }
    }

    private static TimeSpan Backoff(int attempt, RetryConditionHeaderValue? retryAfter, TimeSpan max)
    {
        var hinted = retryAfter?.Delta ?? (retryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
        var delay = hinted ?? TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));
        return delay < TimeSpan.Zero ? TimeSpan.Zero : delay > max ? max : delay;
    }

    /// <summary>Provider error bodies are usually {"error":{"message":"..."}} (Gemini wraps it in an array).</summary>
    private static string? ErrorDetail(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0 ? doc.RootElement[0] : doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var e))
            {
                return e.ValueKind == JsonValueKind.String ? e.GetString() : e.TryGetProperty("message", out var m) ? m.GetString() : null;
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private string Describe(HttpStatusCode status, string? detail)
    {
        var prefix = status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => $"{settings.Provider} rejected the API key",
            HttpStatusCode.TooManyRequests => $"{settings.Provider} rate limit or free-tier quota exceeded",
            HttpStatusCode.NotFound => $"{settings.Provider} does not know model '{settings.Model}'",
            _ => $"{settings.Provider} returned HTTP {(int)status}",
        };
        return detail is null ? prefix + "." : $"{prefix}: {Truncate(detail, 400)}";
    }

    private JsonArray ToolDefinitions(IReadOnlyList<ToolSpec> tools)
    {
        if (!ReferenceEquals(tools, _toolCacheKey))
        {
            _toolCache = new JsonArray(tools.Select(t => (JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["parameters"] = SimplifySchema(JsonNode.Parse(t.JsonSchema)!),
                },
            }).ToArray());
            _toolCacheKey = tools;
        }

        return _toolCache!;
    }

    /// <summary>
    /// Reduces a strict JSON schema to the subset every OpenAI-compatible provider accepts: drops
    /// <c>additionalProperties</c>, and turns <c>anyOf: [X, null]</c> into plain X while removing it from
    /// <c>required</c> (a missing value then means null, which the validator already treats that way).
    /// </summary>
    public static JsonNode SimplifySchema(JsonNode node)
    {
        if (node is JsonArray array)
        {
            return new JsonArray(array.Select(n => n is null ? null : SimplifySchema(n)).ToArray());
        }

        if (node is not JsonObject obj)
        {
            return node.DeepClone();
        }

        var result = new JsonObject();
        var nullableProps = new HashSet<string>();
        foreach (var (key, value) in obj)
        {
            if (key == "additionalProperties" || value is null)
            {
                continue;
            }

            if (key == "properties" && value is JsonObject props)
            {
                var simplified = new JsonObject();
                foreach (var (name, prop) in props)
                {
                    if (prop is JsonObject p && TryUnwrapNullable(p, out var inner))
                    {
                        nullableProps.Add(name);
                        simplified[name] = SimplifySchema(inner);
                    }
                    else
                    {
                        simplified[name] = prop is null ? null : SimplifySchema(prop);
                    }
                }

                result[key] = simplified;
                continue;
            }

            result[key] = SimplifySchema(value);
        }

        if (nullableProps.Count > 0 && result["required"] is JsonArray required)
        {
            result["required"] = new JsonArray(required
                .Select(r => r?.GetValue<string>())
                .Where(r => r is not null && !nullableProps.Contains(r))
                .Select(r => (JsonNode)JsonValue.Create(r)!)
                .ToArray());
        }

        return result;
    }

    private static bool TryUnwrapNullable(JsonObject prop, out JsonObject inner)
    {
        inner = prop;
        if (prop["anyOf"] is not JsonArray anyOf || anyOf.Count != 2)
        {
            return false;
        }

        var nonNull = anyOf.OfType<JsonObject>().Where(o => o["type"]?.GetValue<string>() != "null").ToList();
        if (nonNull.Count != 1)
        {
            return false;
        }

        inner = (JsonObject)nonNull[0].DeepClone();
        foreach (var (key, value) in prop)
        {
            if (key != "anyOf" && value is not null)
            {
                inner[key] = value.DeepClone(); // keep the description
            }
        }

        return true;
    }

    private static string Truncate(string? s, int max) => s is null ? string.Empty : s.Length <= max ? s : s[..max] + "…";
}
