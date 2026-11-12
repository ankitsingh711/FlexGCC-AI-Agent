using System.Text.Json;
using CodeReviewAgent.Core.Agent;
using CodeReviewAgent.Core.Models;

namespace CodeReviewAgent.Core.Llm;

/// <summary>
/// A provider-neutral view of one model turn. The agent loop only sees these types, so the same loop runs on
/// Claude (Anthropic Messages API) or any OpenAI-compatible endpoint (Gemini, Groq, OpenRouter, ...).
/// </summary>
public interface IChatModel
{
    /// <summary>Short provider name for logs and the UI, e.g. "anthropic" or "gemini".</summary>
    string Provider { get; }

    /// <summary>The configured model id.</summary>
    string Model { get; }

    Task<ModelTurn> CompleteAsync(string system, IReadOnlyList<ToolSpec> tools, IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken);
}

public enum TurnStop
{
    EndTurn,
    ToolUse,
    MaxTokens,
    Refusal,
}

/// <summary>A tool call requested by the model. <see cref="ParseError"/> is set when its arguments were not valid JSON.</summary>
public sealed record ToolCall(string Id, string Name, IReadOnlyDictionary<string, JsonElement> Input, string? ParseError = null);

/// <summary>
/// One model response. <see cref="AssistantState"/> is the provider's own representation of the turn, echoed back
/// unchanged on the next request (Claude needs its thinking-block signatures, Gemini its tool-call thought signatures).
/// </summary>
public sealed record ModelTurn(
    string Model,
    TurnStop Stop,
    IReadOnlyList<string> Texts,
    IReadOnlyList<string> Thoughts,
    IReadOnlyList<ToolCall> ToolCalls,
    TokenUsage Usage,
    object AssistantState,
    string? RefusalCategory = null);

public abstract record ChatMessage;

public sealed record UserMessage(string Text) : ChatMessage;

/// <summary>A previous assistant turn, carried as the provider's own state (see <see cref="ModelTurn.AssistantState"/>).</summary>
public sealed record AssistantMessage(object ProviderState) : ChatMessage;

/// <summary>Results for every tool call of the previous assistant turn, plus an optional note (e.g. a budget warning).</summary>
public sealed record ToolResultsMessage(IReadOnlyList<ToolResult> Results, string? Note = null) : ChatMessage;

public sealed record ToolResult(string ToolCallId, string Content, bool IsError);

/// <summary>A provider error with an HTTP status, raised after retries are exhausted.</summary>
public sealed class ModelProviderException(string provider, int statusCode, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public string Provider { get; } = provider;

    public int StatusCode { get; } = statusCode;
}
