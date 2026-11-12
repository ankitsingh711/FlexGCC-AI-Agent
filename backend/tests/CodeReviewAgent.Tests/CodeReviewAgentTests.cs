using CodeReviewAgent.Core.Agent;
using CodeReviewAgent.Core.Llm;
using CodeReviewAgent.Core.Models;
using CodeReviewAgent.Core.Sources;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeReviewAgent.Tests;

/// <summary>Exercises the agent loop end to end through the Claude provider against scripted responses (no network).</summary>
public class CodeReviewAgentTests
{
    private static CodeReviewAgent.Core.Agent.CodeReviewAgent Agent(IMessagesClient client, AgentOptions? options = null)
    {
        options ??= new AgentOptions();
        return new(new AnthropicChatModel(client, options), options, NullLogger<CodeReviewAgent.Core.Agent.CodeReviewAgent>.Instance);
    }

    [Fact]
    public async Task Investigates_with_a_tool_then_submits_a_validated_review()
    {
        var client = new ScriptedMessagesClient(
            ScriptedMessagesClient.ToolUse("toolu_1", "get_file_diff", """{"path":"src/calc.py"}"""),
            ScriptedMessagesClient.ToolUse("toolu_2", "submit_review", Fixtures.Review("request_changes", Fixtures.Finding())));
        var events = new List<AgentEvent>();

        var result = await Agent(client).ReviewAsync(new PastedDiffSource(Fixtures.CalcDiff), e => { events.Add(e); return Task.CompletedTask; });

        Assert.Equal(ReviewVerdict.RequestChanges, result.Verdict);
        Assert.Equal(5, Assert.Single(result.Findings).Line);
        Assert.Equal(2, result.Turns);
        Assert.Equal("claude-opus-5-5", result.Model);
        Assert.Equal(2000, result.Usage.InputTokens);
        Assert.Equal(1600, result.Usage.CacheReadInputTokens);
        Assert.Equal(["get_file_diff", "submit_review"], result.ToolCalls.Select(t => t.Name));

        // The second request echoes the assistant tool_use and answers it with a matching tool_result.
        var second = client.MessagesJson(1);
        Assert.Contains("\"toolu_1\"", second);
        Assert.Contains("\"tool_use_id\":\"toolu_1\"", second);
        Assert.Contains(events, e => e.Type == "tool_call" && e.Tool == "get_file_diff");
    }

    [Fact]
    public async Task Sends_request_with_cached_system_prompt_strict_tools_and_fallbacks()
    {
        var client = new ScriptedMessagesClient(ScriptedMessagesClient.ToolUse("t", "submit_review", Fixtures.Review("approve")));
        await Agent(client).ReviewAsync(new PastedDiffSource(Fixtures.CalcDiff));

        var json = System.Text.Json.JsonSerializer.Serialize(client.Requests[0]);
        Assert.Contains("\"model\":\"claude-opus-5-5\"", json);
        Assert.Contains("\"strict\":true", json);
        Assert.Contains("\"cache_control\":{\"type\":\"ephemeral\"}", json);
        Assert.Contains("\"effort\":\"high\"", json);
        Assert.Contains("\"fallbacks\":\"default\"", json);
    }

    [Fact]
    public async Task Invalid_submission_is_returned_to_the_model_as_a_tool_error_then_corrected()
    {
        var client = new ScriptedMessagesClient(
            ScriptedMessagesClient.ToolUse("t1", "submit_review", Fixtures.Review("comment", Fixtures.Finding(file: "hallucinated.py"))),
            ScriptedMessagesClient.ToolUse("t2", "submit_review", Fixtures.Review("comment", Fixtures.Finding())));

        var result = await Agent(client).ReviewAsync(new PastedDiffSource(Fixtures.CalcDiff));

        Assert.Single(result.Findings);
        var second = client.MessagesJson(1);
        Assert.Contains("\"is_error\":true", second);
        Assert.Contains("hallucinated.py", second);
        Assert.True(result.ToolCalls[0].IsError);
    }

    [Fact]
    public async Task Repeated_invalid_submissions_are_repaired_instead_of_looping_forever()
    {
        var bad = ScriptedMessagesClient.ToolUse("t", "submit_review", Fixtures.Review("comment", Fixtures.Finding(file: "x.py"), Fixtures.Finding()));
        var client = new ScriptedMessagesClient(bad, bad, bad);

        var result = await Agent(client, new AgentOptions { MaxSubmitRetries = 2 }).ReviewAsync(new PastedDiffSource(Fixtures.CalcDiff));

        Assert.Equal(3, client.Requests.Count);
        Assert.Single(result.Findings);
        Assert.Contains(result.Warnings, w => w.Contains("x.py"));
    }

    [Fact]
    public async Task Nudges_when_model_ends_turn_without_submitting()
    {
        var client = new ScriptedMessagesClient(
            ScriptedMessagesClient.Text("Looks fine to me."),
            ScriptedMessagesClient.ToolUse("t", "submit_review", Fixtures.Review("approve")));

        var result = await Agent(client).ReviewAsync(new PastedDiffSource(Fixtures.CalcDiff));

        Assert.Equal(ReviewVerdict.Approve, result.Verdict);
        Assert.Contains("submit_review", client.MessagesJson(1));
    }

    [Fact]
    public async Task Refusal_stops_the_run_with_a_clear_error()
    {
        var client = new ScriptedMessagesClient(ScriptedMessagesClient.Message("[]", "refusal"));
        var ex = await Assert.ThrowsAsync<AgentException>(() => Agent(client).ReviewAsync(new PastedDiffSource(Fixtures.CalcDiff)));
        Assert.Contains("declined", ex.Message);
    }

    [Fact]
    public async Task Truncated_tool_call_is_not_executed()
    {
        var source = new FakeRepoSource(Fixtures.CalcDiff, new() { ["src/calc.py"] = "x" });
        var client = new ScriptedMessagesClient(
            ScriptedMessagesClient.ToolUse("t1", "read_file", """{"path":"src/calc.py"}""", stopReason: "max_tokens"),
            ScriptedMessagesClient.ToolUse("t2", "submit_review", Fixtures.Review("approve")));

        await Agent(client).ReviewAsync(source);

        Assert.Empty(source.Reads);
        Assert.Contains("output token limit", client.MessagesJson(1));
    }

    [Fact]
    public async Task Turn_budget_is_enforced_with_a_warning_before_the_end()
    {
        var call = ScriptedMessagesClient.ToolUse("t", "list_changed_files", "{}");
        var client = new ScriptedMessagesClient(call, call, call);

        var ex = await Assert.ThrowsAsync<AgentException>(() =>
            Agent(client, new AgentOptions { MaxTurns = 3 }).ReviewAsync(new PastedDiffSource(Fixtures.CalcDiff)));

        Assert.Contains("3 turns", ex.Message);
        Assert.Contains("Budget notice", client.MessagesJson(2));
    }

    [Fact]
    public async Task Tool_call_budget_returns_errors_instead_of_running_tools()
    {
        var source = new FakeRepoSource(Fixtures.CalcDiff, new() { ["src/calc.py"] = "x" });
        var read = ScriptedMessagesClient.ToolUse("t", "read_file", """{"path":"src/calc.py"}""");
        var client = new ScriptedMessagesClient(read, read, ScriptedMessagesClient.ToolUse("s", "submit_review", Fixtures.Review("approve")));

        var result = await Agent(client, new AgentOptions { MaxToolCalls = 1 }).ReviewAsync(source);

        Assert.Single(source.Reads);
        Assert.True(result.ToolCalls[1].IsError);
    }

    [Fact]
    public void Initial_prompt_wraps_untrusted_content_and_inlines_small_diffs()
    {
        var source = new PastedDiffSource(Fixtures.CalcDiff);
        var prompt = Prompts.BuildInitialMessage(source, new AgentOptions(), ["get_file_diff", "submit_review"]);

        Assert.Contains("<diff>", prompt);
        Assert.Contains("    5 +     return a / b", prompt);
        Assert.Contains("no repository access", prompt);

        var large = Prompts.BuildInitialMessage(source, new AgentOptions { InlineDiffChars = 10 }, ["get_file_diff"]);
        Assert.DoesNotContain("<diff>", large);
        Assert.Contains("get_file_diff", large);
    }
}
