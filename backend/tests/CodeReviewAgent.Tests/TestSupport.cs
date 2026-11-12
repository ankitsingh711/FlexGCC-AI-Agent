using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using CodeReviewAgent.Core.Agent;
using CodeReviewAgent.Core.Diff;
using CodeReviewAgent.Core.Llm;
using CodeReviewAgent.Core.Sources;

namespace CodeReviewAgent.Tests;

internal static class Fixtures
{
    public const string CalcDiff = """
        diff --git a/src/calc.py b/src/calc.py
        --- a/src/calc.py
        +++ b/src/calc.py
        @@ -1,4 +1,6 @@
         def add(a, b):
             return a + b
        +
        +def div(a, b):
        +    return a / b
         def sub(a, b):
             return a - b
        """;

    public static Dictionary<string, JsonElement> Json(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    public static string Finding(string file = "src/calc.py", string line = "5", string category = "bug", string severity = "high", string title = "Division by zero") => $$"""
        {"category":"{{category}}","severity":"{{severity}}","confidence":"high","file":"{{file}}","line":{{line}},"end_line":null,
         "title":"{{title}}","explanation":"div(1, 0) raises ZeroDivisionError.","suggestion":"Guard b == 0."}
        """;

    public static string Review(string verdict = "request_changes", params string[] findings) =>
        $$"""{"summary":"Adds div().","verdict":"{{verdict}}","findings":[{{string.Join(",", findings)}}]}""";
}

/// <summary>A source with a fake repository, for exercising the repository tools without GitHub.</summary>
internal sealed class FakeRepoSource(string diff, Dictionary<string, string> files) : IChangeSource
{
    public string Kind => "github_pr";

    public string Label => "fake/repo#1";

    public string RawDiff { get; } = diff;

    public ParsedDiff Diff { get; } = UnifiedDiffParser.Parse(diff);

    public string? Title => "Add div";

    public string? Description => null;

    public bool SupportsRepositoryAccess => true;

    public List<string> Reads { get; } = [];

    public Task<string?> ReadFileAsync(string path, CancellationToken cancellationToken)
    {
        Reads.Add(path);
        return Task.FromResult(files.TryGetValue(path, out var c) ? c : null);
    }

    public Task<IReadOnlyList<string>> ListRepositoryFilesAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>(files.Keys.ToList());
}

/// <summary>Replays scripted model responses and records every request the agent sends.</summary>
internal sealed class ScriptedMessagesClient(params string[] responses) : IMessagesClient
{
    private readonly Queue<string> _responses = new(responses);

    public List<MessageCreateParams> Requests { get; } = [];

    public Task<BetaMessage> CreateAsync(MessageCreateParams parameters, CancellationToken cancellationToken)
    {
        Requests.Add(parameters);
        if (_responses.Count == 0)
        {
            throw new InvalidOperationException("No more scripted responses.");
        }

        return Task.FromResult(BetaMessage.FromRawUnchecked(Fixtures.Json(_responses.Dequeue())));
    }

    /// <summary>The wire JSON of the messages in request <paramref name="index"/>.</summary>
    public string MessagesJson(int index) => JsonSerializer.Serialize(Requests[index].Messages);

    public static string ToolUse(string id, string name, string inputJson, string stopReason = "tool_use") => Message(
        $$"""[{"type":"tool_use","id":"{{id}}","name":"{{name}}","input":{{inputJson}}}]""", stopReason);

    public static string Text(string text, string stopReason = "end_turn") => Message(
        $$"""[{"type":"text","text":{{JsonSerializer.Serialize(text)}}}]""", stopReason);

    public static string Message(string contentJson, string stopReason) => $$$"""
        {"id":"msg_test","type":"message","role":"assistant","model":"claude-opus-5-5","content":{{{contentJson}}},
         "stop_reason":"{{{stopReason}}}","stop_sequence":null,"stop_details":null,
         "usage":{"input_tokens":1000,"output_tokens":100,"cache_read_input_tokens":800,"cache_creation_input_tokens":0}}
        """;
}
