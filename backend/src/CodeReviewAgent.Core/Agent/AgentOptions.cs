namespace CodeReviewAgent.Core.Agent;

/// <summary>Tunable limits for the review agent. Bound from the "Agent" configuration section.</summary>
public sealed class AgentOptions
{
    public const string SectionName = "Agent";

    /// <summary>gemini (default) | groq | openrouter | openai-compatible | anthropic. See ProviderPresets.</summary>
    public string Provider { get; set; } = "gemini";

    /// <summary>Model id. Empty (or a claude-* id with a non-Anthropic provider) means the provider's default model.</summary>
    public string Model { get; set; } = "claude-opus-5-5";

    /// <summary>
    /// Models to try, in order, when the main model is overloaded (503) or rate-limited (429). Null means the provider's
    /// defaults (for Gemini: gemini-3.6-flash, then gemini-3.5-flash-lite); an empty list turns fallback off.
    /// </summary>
    public string[]? FallbackModels { get; set; }

    /// <summary>Override the provider's base URL (required for openai-compatible, e.g. a local Ollama at http://localhost:11434/v1/).</summary>
    public string? BaseUrl { get; set; }

    /// <summary>low | medium | high | xhigh | max. Code review is intelligence-sensitive, so default to high.</summary>
    public string Effort { get; set; } = "high";

    /// <summary>Per-response output cap. Non-streaming requests stay well under SDK HTTP timeouts at this size.</summary>
    public int MaxTokensPerTurn { get; set; } = 16_000;

    /// <summary>Hard cap on model round-trips. Prevents runaway loops and bounds cost.</summary>
    public int MaxTurns { get; set; } = 12;

    /// <summary>Hard cap on investigation tool calls across the whole run.</summary>
    public int MaxToolCalls { get; set; } = 30;

    /// <summary>How many times a rejected submit_review is sent back to the model before repairing it ourselves.</summary>
    public int MaxSubmitRetries { get; set; } = 2;

    /// <summary>Largest diff accepted at all.</summary>
    public int MaxDiffChars { get; set; } = 400_000;

    /// <summary>Diffs up to this size are inlined in the first prompt; larger ones are given as a file list to pull from.</summary>
    public int InlineDiffChars { get; set; } = 60_000;

    public int MaxReadFileLines { get; set; } = 400;

    public int MaxDiffLinesPerFileTool { get; set; } = 1_500;

    /// <summary>Claude only: opt in to server-side refusal fallbacks (routes a declined request to a fallback model in the same call).</summary>
    public bool EnableServerSideFallback { get; set; } = true;

    /// <summary>Wall-clock limit for one review.</summary>
    public int TimeoutSeconds { get; set; } = 300;
}
