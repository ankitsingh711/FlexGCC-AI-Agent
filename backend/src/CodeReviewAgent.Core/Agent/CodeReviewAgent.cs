using System.Diagnostics;
using System.Text.Json;
using CodeReviewAgent.Core.Llm;
using CodeReviewAgent.Core.Models;
using CodeReviewAgent.Core.Sources;
using Microsoft.Extensions.Logging;

namespace CodeReviewAgent.Core.Agent;

/// <summary>Progress notification emitted while the agent runs (streamed to the UI / CLI).</summary>
public sealed record AgentEvent(string Type, int Turn, string Message, string? Tool = null, bool? IsError = null, long? DurationMs = null);

/// <summary>A failure the agent could not recover from (refusal, turn budget exhausted, no review submitted).</summary>
public sealed class AgentException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The code review agent: a manual tool-use loop over a provider-neutral <see cref="IChatModel"/>
/// (Claude, Gemini, Groq, OpenRouter, ...).
///
/// Why a manual loop rather than an SDK tool runner: the loop must (1) stop on a *terminal* tool (submit_review)
/// whose input is the product, (2) validate that input and bounce it back to the model on failure, (3) inject
/// budget warnings, and (4) stream per-tool progress events. Those are all loop-level control flow.
///
/// Conversation history is append-only: each assistant turn is echoed back in the provider's own representation,
/// and every tool call gets exactly one result in the next message.
/// </summary>
public sealed class CodeReviewAgent(IChatModel model, AgentOptions options, ILogger<CodeReviewAgent> logger)
{
    public async Task<ReviewResult> ReviewAsync(IChangeSource source, Func<AgentEvent, Task>? onEvent = null, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        var ct = timeout.Token;

        var stopwatch = Stopwatch.StartNew();
        var emit = onEvent ?? (_ => Task.CompletedTask);
        var tools = new ReviewTools(source, options);
        var specs = tools.GetSpecs();
        var validator = new FindingValidator(source.Diff, tools.FilesRead);

        var history = new List<ChatMessage>
        {
            new UserMessage(Prompts.BuildInitialMessage(source, options, specs.Select(s => s.Name).ToList())),
        };

        var usage = TokenUsage.Zero;
        var toolCalls = new List<ToolCallRecord>();
        var submitRejections = 0;
        var nudges = 0;
        var modelUsed = model.Model;

        await emit(new AgentEvent("status", 0, $"Reviewing {source.Label}: {source.Diff.Files.Count} file(s), +{source.Diff.TotalAdditions} -{source.Diff.TotalDeletions}"));

        try
        {
            for (var turn = 1; turn <= options.MaxTurns; turn++)
            {
                await emit(new AgentEvent("turn", turn, $"Turn {turn}: calling {model.Model}"));
                var response = await model.CompleteAsync(Prompts.System, specs, history, ct);
                usage = usage.Add(response.Usage);
                modelUsed = response.Model;
                if (response.FallbackNote is { } note)
                {
                    await emit(new AgentEvent("status", turn, note));
                }

                if (response.Stop == TurnStop.Refusal)
                {
                    throw new AgentException($"The model declined to review this change (refusal category: {response.RefusalCategory ?? "unspecified"}).");
                }

                foreach (var thought in response.Thoughts.Where(t => !string.IsNullOrWhiteSpace(t)))
                {
                    await emit(new AgentEvent("thinking", turn, thought));
                }

                foreach (var text in response.Texts.Where(t => !string.IsNullOrWhiteSpace(t)))
                {
                    await emit(new AgentEvent("message", turn, text));
                }

                history.Add(new AssistantMessage(response.AssistantState));

                if (response.ToolCalls.Count == 0)
                {
                    // end_turn (or max_tokens) without submitting: nudge once or twice, then give up.
                    if (++nudges > 2)
                    {
                        throw new AgentException("The model repeatedly finished without calling submit_review.");
                    }

                    logger.LogWarning("Turn {Turn} ended ({Stop}) without submit_review; nudging", turn, response.Stop);
                    await emit(new AgentEvent("status", turn, "Model stopped without submitting a review; asking it to submit."));
                    history.Add(new UserMessage(Prompts.NudgeSubmit));
                    continue;
                }

                var truncated = response.Stop == TurnStop.MaxTokens;
                var results = new List<ToolResult>();
                ReviewResult? final = null;

                foreach (var call in response.ToolCalls)
                {
                    var sw = Stopwatch.StartNew();
                    var inputSummary = SummarizeInput(call.Input);
                    ToolOutcome outcome;

                    if (truncated)
                    {
                        // A tool call cut off by max_tokens may have partial input that still parses: never run it.
                        outcome = ToolOutcome.Error("Your response hit the output token limit and this tool input may be truncated. Resend it, more concisely.");
                    }
                    else if (call.ParseError is not null)
                    {
                        outcome = ToolOutcome.Error($"INVALID_JSON: {call.ParseError}. Resend the call with valid JSON arguments.");
                    }
                    else if (call.Name == ReviewTools.SubmitReview)
                    {
                        await emit(new AgentEvent("tool_call", turn, "Submitting review", call.Name));
                        var verdict = validator.Validate(call.Input, allowRepair: submitRejections >= options.MaxSubmitRetries);
                        if (verdict.IsValid)
                        {
                            final = new ReviewResult
                            {
                                Summary = verdict.Summary,
                                Verdict = verdict.Verdict,
                                Findings = verdict.Findings,
                                Stats = new DiffStats(source.Diff.Files.Count, source.Diff.TotalAdditions, source.Diff.TotalDeletions),
                                Model = modelUsed,
                                Turns = turn,
                                Usage = usage,
                                ToolCalls = toolCalls,
                                Warnings = verdict.Warnings,
                                DurationMs = stopwatch.ElapsedMilliseconds,
                            };
                            outcome = ToolOutcome.Ok("Review recorded.");
                        }
                        else
                        {
                            submitRejections++;
                            logger.LogInformation("submit_review rejected ({Count}): {Error}", submitRejections, verdict.ErrorForModel);
                            outcome = ToolOutcome.Error(verdict.ErrorForModel!);
                        }
                    }
                    else if (toolCalls.Count(c => c.Name != ReviewTools.SubmitReview) >= options.MaxToolCalls)
                    {
                        outcome = ToolOutcome.Error($"Tool call budget ({options.MaxToolCalls}) exhausted. Call submit_review now.");
                    }
                    else
                    {
                        await emit(new AgentEvent("tool_call", turn, inputSummary, call.Name));
                        outcome = await tools.ExecuteAsync(call.Name, call.Input, ct);
                    }

                    sw.Stop();
                    toolCalls.Add(new ToolCallRecord(turn, call.Name, inputSummary, outcome.IsError, outcome.Content.Length, sw.ElapsedMilliseconds));
                    await emit(new AgentEvent("tool_result", turn, Preview(outcome.Content), call.Name, outcome.IsError, sw.ElapsedMilliseconds));
                    results.Add(new ToolResult(call.Id, outcome.Content, outcome.IsError));
                }

                if (final is not null)
                {
                    // The review is the product; there is no need for another model round-trip.
                    final = final with { ToolCalls = toolCalls.ToList(), DurationMs = stopwatch.ElapsedMilliseconds };
                    await emit(new AgentEvent("status", turn, $"Review complete: {final.Findings.Count} finding(s), verdict {final.Verdict}"));
                    logger.LogInformation(
                        "Review of {Label} by {Provider}/{Model} finished in {Turns} turns, {Tools} tool calls, {In}/{Out} tokens (cache read {Cache})",
                        source.Label, model.Provider, modelUsed, turn, toolCalls.Count, usage.InputTokens, usage.OutputTokens, usage.CacheReadInputTokens);
                    return final;
                }

                var turnsLeft = options.MaxTurns - turn;
                history.Add(new ToolResultsMessage(results, turnsLeft <= 2 ? Prompts.BudgetWarning(turnsLeft) : null));
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new AgentException($"The review did not finish within {options.TimeoutSeconds} seconds.");
        }

        throw new AgentException($"The review did not finish within {options.MaxTurns} turns.");
    }

    private static string SummarizeInput(IReadOnlyDictionary<string, JsonElement> input)
    {
        if (input.Count == 0)
        {
            return "{}";
        }

        var parts = input
            .Where(kv => kv.Key != "findings")
            .Select(kv => $"{kv.Key}={(kv.Value.ValueKind == JsonValueKind.String ? kv.Value.GetString() : kv.Value.GetRawText())}");
        var s = string.Join(", ", parts);
        return s.Length > 200 ? s[..200] + "…" : s;
    }

    private static string Preview(string s)
    {
        var firstLines = string.Join("\n", s.Split('\n').Take(6));
        return firstLines.Length > 600 ? firstLines[..600] + "…" : firstLines;
    }
}
