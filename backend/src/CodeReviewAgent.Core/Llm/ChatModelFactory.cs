using Anthropic;
using CodeReviewAgent.Core.Agent;
using Microsoft.Extensions.Logging;

namespace CodeReviewAgent.Core.Llm;

/// <summary>Builds the configured <see cref="IChatModel"/>; shared by the API and the CLI.</summary>
public static class ChatModelFactory
{
    public const string HttpClientName = "llm";

    public static IChatModel Create(ProviderSelection selection, AgentOptions options, HttpClient http, ILoggerFactory loggers)
    {
        if (selection.IsAnthropic)
        {
            options.Model = selection.Model;
            // The SDK retries 408/409/429/5xx and connection errors with backoff; we only raise the count.
            var client = new AnthropicClient { ApiKey = selection.ApiKey, MaxRetries = 3, Timeout = TimeSpan.FromMinutes(5) };
            return new AnthropicChatModel(new AnthropicMessagesClient(client), options);
        }

        return new OpenAiCompatibleChatModel(
            http,
            new OpenAiCompatibleSettings(
                selection.Provider,
                selection.BaseUrl!,
                selection.ApiKey ?? string.Empty,
                selection.Model,
                options.MaxTokensPerTurn,
                selection.ReasoningEffort),
            loggers.CreateLogger<OpenAiCompatibleChatModel>());
    }
}
