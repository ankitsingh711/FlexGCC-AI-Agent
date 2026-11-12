using System.Text.Json;
using Anthropic;
using Anthropic.Models.Beta.Messages;
using CodeReviewAgent.Core.Agent;
using CodeReviewAgent.Core.Models;

namespace CodeReviewAgent.Core.Llm;

/// <summary>Seam over the Anthropic Messages API so the Claude provider can be tested without network calls.</summary>
public interface IMessagesClient
{
    Task<BetaMessage> CreateAsync(MessageCreateParams parameters, CancellationToken cancellationToken);
}

public sealed class AnthropicMessagesClient(AnthropicClient client) : IMessagesClient
{
    public Task<BetaMessage> CreateAsync(MessageCreateParams parameters, CancellationToken cancellationToken) =>
        client.Beta.Messages.Create(parameters, cancellationToken);
}

/// <summary>
/// Claude via the Anthropic Messages API: adaptive thinking, effort, strict tool schemas, prompt caching and
/// server-side refusal fallbacks. Assistant turns (including thinking blocks and their signatures) are echoed back exactly.
/// </summary>
public sealed class AnthropicChatModel(IMessagesClient messages, AgentOptions options) : IChatModel
{
    private List<BetaToolUnion>? _toolCache;
    private IReadOnlyList<ToolSpec>? _toolCacheKey;

    public string Provider => "anthropic";

    public string Model => options.Model;

    public async Task<ModelTurn> CompleteAsync(string system, IReadOnlyList<ToolSpec> tools, IReadOnlyList<ChatMessage> history, CancellationToken cancellationToken)
    {
        var response = await messages.CreateAsync(BuildParams(system, tools, history), cancellationToken);

        var assistantBlocks = new List<BetaContentBlockParam>();
        var texts = new List<string>();
        var thoughts = new List<string>();
        var calls = new List<ToolCall>();
        foreach (var block in response.Content)
        {
            if (block.TryPickText(out var text))
            {
                assistantBlocks.Add(new BetaTextBlockParam { Text = text.Text });
                texts.Add(text.Text);
            }
            else if (block.TryPickThinking(out var thinking))
            {
                // Signature must be echoed unchanged; the API rejects tampered thinking blocks.
                assistantBlocks.Add(new BetaThinkingBlockParam { Thinking = thinking.Thinking, Signature = thinking.Signature });
                thoughts.Add(thinking.Thinking);
            }
            else if (block.TryPickRedactedThinking(out var redacted))
            {
                assistantBlocks.Add(new BetaRedactedThinkingBlockParam { Data = redacted.Data });
            }
            else if (block.TryPickToolUse(out var toolUse))
            {
                assistantBlocks.Add(new BetaToolUseBlockParam { ID = toolUse.ID, Name = toolUse.Name, Input = toolUse.Input });
                calls.Add(new ToolCall(toolUse.ID, toolUse.Name, toolUse.Input));
            }
        }

        var stopRaw = response.StopReason?.Raw();
        var stop = stopRaw switch
        {
            "refusal" => TurnStop.Refusal,
            "max_tokens" => TurnStop.MaxTokens,
            _ when calls.Count > 0 => TurnStop.ToolUse,
            _ => TurnStop.EndTurn,
        };

        var u = response.Usage;
        return new ModelTurn(
            response.Model.Raw() is { Length: > 0 } m ? m : options.Model,
            stop,
            texts,
            thoughts,
            calls,
            new TokenUsage(u.InputTokens, u.OutputTokens, u.CacheReadInputTokens ?? 0, u.CacheCreationInputTokens ?? 0),
            assistantBlocks,
            stop == TurnStop.Refusal ? response.StopDetails?.Category?.Raw() ?? "unspecified" : null);
    }

    private MessageCreateParams BuildParams(string system, IReadOnlyList<ToolSpec> tools, IReadOnlyList<ChatMessage> history)
    {
        var p = new MessageCreateParams
        {
            Model = options.Model,
            MaxTokens = options.MaxTokensPerTurn,
            // Stable prefix (tools + system) is cached; top-level cache_control also moves a breakpoint to the end of the
            // conversation each turn, so the diff and earlier tool results are read from cache on every later turn.
            System = new List<BetaTextBlockParam> { new() { Text = system, CacheControl = new BetaCacheControlEphemeral() } },
            CacheControl = new BetaCacheControlEphemeral(),
            Thinking = new BetaThinkingConfigAdaptive { Display = Display.Summarized },
            OutputConfig = new BetaOutputConfig { Effort = ParseEffort(options.Effort) },
            Tools = ToolDefinitions(tools),
            Messages = history.Select(ToParam).ToList(),
        };

        if (options.EnableServerSideFallback)
        {
            p = p with { Betas = ["server-side-fallback-2026-07-01"], Fallbacks = new Default() };
        }

        return p;
    }

    private List<BetaToolUnion> ToolDefinitions(IReadOnlyList<ToolSpec> tools)
    {
        // Same instance every turn: tool definitions are part of the cached prefix.
        if (!ReferenceEquals(tools, _toolCacheKey))
        {
            _toolCache = tools.Select(ToBetaTool).ToList();
            _toolCacheKey = tools;
        }

        return _toolCache!;
    }

    private static BetaMessageParam ToParam(ChatMessage message) => message switch
    {
        UserMessage u => new BetaUserMessageParam { Content = u.Text },
        AssistantMessage a => new BetaAssistantMessageParam { Content = (List<BetaContentBlockParam>)a.ProviderState },
        ToolResultsMessage t => new BetaUserMessageParam { Content = ToolResultBlocks(t) },
        _ => throw new ArgumentOutOfRangeException(nameof(message)),
    };

    private static List<BetaContentBlockParam> ToolResultBlocks(ToolResultsMessage t)
    {
        var blocks = new List<BetaContentBlockParam>();
        foreach (var r in t.Results)
        {
            blocks.Add(new BetaToolResultBlockParam { ToolUseID = r.ToolCallId, Content = r.Content, IsError = r.IsError });
        }

        if (!string.IsNullOrEmpty(t.Note))
        {
            blocks.Add(new BetaTextBlockParam { Text = t.Note });
        }

        return blocks;
    }

    private static BetaToolUnion ToBetaTool(ToolSpec spec)
    {
        var schema = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(spec.JsonSchema)!;
        return new BetaTool
        {
            Name = spec.Name,
            Description = spec.Description,
            InputSchema = InputSchema.FromRawUnchecked(schema),
            Strict = spec.Strict,
        };
    }

    private static Effort ParseEffort(string effort) => effort.ToLowerInvariant() switch
    {
        "low" => Effort.Low,
        "medium" => Effort.Medium,
        "xhigh" => Effort.Xhigh,
        "max" => Effort.Max,
        _ => Effort.High,
    };
}
