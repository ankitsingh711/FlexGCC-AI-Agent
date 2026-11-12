using CodeReviewAgent.Core.Agent;

namespace CodeReviewAgent.Core.Llm;

/// <summary>A resolved provider: which API to call, with which model and key.</summary>
public sealed record ProviderSelection(
    string Provider,
    bool IsAnthropic,
    Uri? BaseUrl,
    string Model,
    string ApiKeyEnvVar,
    string? ApiKey,
    string? ReasoningEffort)
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);
}

/// <summary>
/// Built-in providers. Gemini is the default because its free tier has the most token headroom for an agent that
/// resends the conversation every turn; model ids change over time, so every value can be overridden in configuration.
/// </summary>
public static class ProviderPresets
{
    private sealed record Preset(string BaseUrl, string DefaultModel, string KeyEnvVar, bool SendsReasoningEffort);

    private static readonly Dictionary<string, Preset> OpenAiCompatible = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gemini"] = new("https://generativelanguage.googleapis.com/v1beta/openai/", "gemini-3.8-flash", "GEMINI_API_KEY", true),
        ["groq"] = new("https://api.groq.com/openai/v1/", "openai/gpt-oss-120b", "GROQ_API_KEY", true),
        ["openrouter"] = new("https://openrouter.ai/api/v1/", string.Empty, "OPENROUTER_API_KEY", false),
        ["openai-compatible"] = new(string.Empty, string.Empty, "LLM_API_KEY", false),
    };

    public static IReadOnlyCollection<string> Names { get; } = ["gemini", "groq", "openrouter", "openai-compatible", "anthropic"];

    /// <param name="options">Agent options; <see cref="AgentOptions.Provider"/>, <see cref="AgentOptions.Model"/>,
    /// <see cref="AgentOptions.BaseUrl"/> and <see cref="AgentOptions.Effort"/> are read.</param>
    /// <param name="getSetting">Reads an environment variable or configuration value by name.</param>
    public static ProviderSelection Resolve(AgentOptions options, Func<string, string?> getSetting)
    {
        var provider = string.IsNullOrWhiteSpace(options.Provider) ? "gemini" : options.Provider.Trim().ToLowerInvariant();
        if (provider == "anthropic")
        {
            var model = string.IsNullOrWhiteSpace(options.Model) ? "claude-opus-5-5" : options.Model;
            return new ProviderSelection("anthropic", true, null, model, "ANTHROPIC_API_KEY", getSetting("ANTHROPIC_API_KEY"), null);
        }

        if (!OpenAiCompatible.TryGetValue(provider, out var preset))
        {
            throw new InvalidOperationException($"Unknown Agent:Provider '{options.Provider}'. Use one of: {string.Join(", ", Names)}.");
        }

        var baseUrl = string.IsNullOrWhiteSpace(options.BaseUrl) ? preset.BaseUrl : options.BaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException($"Agent:BaseUrl is required for provider '{provider}'.");
        }

        var resolvedModel = string.IsNullOrWhiteSpace(options.Model) || options.Model.StartsWith("claude-", StringComparison.Ordinal)
            ? preset.DefaultModel
            : options.Model;
        if (string.IsNullOrWhiteSpace(resolvedModel))
        {
            throw new InvalidOperationException($"Agent:Model is required for provider '{provider}' (pick a model id from the provider's model list).");
        }

        // Most OpenAI-compatible endpoints accept low/medium/high only.
        var effort = preset.SendsReasoningEffort
            ? options.Effort.ToLowerInvariant() switch { "low" => "low", "medium" => "medium", _ => "high" }
            : null;

        return new ProviderSelection(
            provider,
            false,
            new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/"),
            resolvedModel,
            preset.KeyEnvVar,
            getSetting(preset.KeyEnvVar),
            effort);
    }
}
