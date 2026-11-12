using CodeReviewAgent.Core.Agent;
using CodeReviewAgent.Core.Diff;
using CodeReviewAgent.Core.Models;

namespace CodeReviewAgent.Tests;

public class FindingValidatorTests
{
    private static readonly ParsedDiff Diff = UnifiedDiffParser.Parse(Fixtures.CalcDiff);

    private static FindingValidator Validator(Dictionary<string, int>? read = null) => new(Diff, read ?? []);

    [Fact]
    public void Accepts_grounded_finding()
    {
        var outcome = Validator().Validate(Fixtures.Json(Fixtures.Review("request_changes", Fixtures.Finding())), allowRepair: false);

        Assert.True(outcome.IsValid);
        var f = Assert.Single(outcome.Findings);
        Assert.Equal(FindingCategory.Bug, f.Category);
        Assert.Equal(Severity.High, f.Severity);
        Assert.Equal(5, f.Line);
        Assert.Empty(outcome.Warnings);
    }

    [Fact]
    public void Unknown_file_is_sent_back_to_the_model()
    {
        var outcome = Validator().Validate(Fixtures.Json(Fixtures.Review("comment", Fixtures.Finding(file: "src/made_up.py"))), allowRepair: false);

        Assert.False(outcome.IsValid);
        Assert.Contains("src/made_up.py", outcome.ErrorForModel);
        Assert.Contains("src/calc.py", outcome.ErrorForModel); // tells the model what the valid paths are
    }

    [Fact]
    public void Unknown_file_is_dropped_when_repair_is_allowed()
    {
        var outcome = Validator().Validate(
            Fixtures.Json(Fixtures.Review("comment", Fixtures.Finding(file: "nope.py"), Fixtures.Finding())), allowRepair: true);

        Assert.True(outcome.IsValid);
        Assert.Single(outcome.Findings);
        Assert.Contains(outcome.Warnings, w => w.Contains("nope.py"));
    }

    [Fact]
    public void Line_outside_diff_is_downgraded_to_file_level()
    {
        var outcome = Validator().Validate(Fixtures.Json(Fixtures.Review("comment", Fixtures.Finding(line: "42"))), allowRepair: false);

        Assert.True(outcome.IsValid);
        Assert.Null(Assert.Single(outcome.Findings).Line);
        Assert.Contains(outcome.Warnings, w => w.Contains("line 42"));
    }

    [Fact]
    public void Line_outside_diff_is_kept_when_agent_read_the_file()
    {
        var outcome = Validator(new() { ["src/calc.py"] = 50 })
            .Validate(Fixtures.Json(Fixtures.Review("comment", Fixtures.Finding(line: "42"))), allowRepair: false);

        Assert.Equal(42, Assert.Single(outcome.Findings).Line);
    }

    [Fact]
    public void Prefixed_paths_are_normalized()
    {
        var outcome = Validator().Validate(Fixtures.Json(Fixtures.Review("comment", Fixtures.Finding(file: "b/src/calc.py"))), allowRepair: false);
        Assert.Equal("src/calc.py", Assert.Single(outcome.Findings).File);
    }

    [Fact]
    public void Duplicates_are_removed_and_findings_sorted_by_severity()
    {
        var outcome = Validator().Validate(Fixtures.Json(Fixtures.Review(
            "request_changes",
            Fixtures.Finding(severity: "low", category: "style", title: "Name"),
            Fixtures.Finding(),
            Fixtures.Finding())), allowRepair: false);

        Assert.Equal(2, outcome.Findings.Count);
        Assert.Equal(Severity.High, outcome.Findings[0].Severity);
        Assert.Contains(outcome.Warnings, w => w.Contains("duplicate"));
    }

    [Fact]
    public void Approve_with_high_severity_bug_is_upgraded_to_request_changes()
    {
        var outcome = Validator().Validate(Fixtures.Json(Fixtures.Review("approve", Fixtures.Finding())), allowRepair: false);
        Assert.Equal(ReviewVerdict.RequestChanges, outcome.Verdict);
    }

    [Fact]
    public void Empty_findings_list_is_valid()
    {
        var outcome = Validator().Validate(Fixtures.Json(Fixtures.Review("approve")), allowRepair: false);
        Assert.True(outcome.IsValid);
        Assert.Empty(outcome.Findings);
        Assert.Equal(ReviewVerdict.Approve, outcome.Verdict);
    }

    [Fact]
    public void Invalid_enum_value_is_rejected_not_thrown()
    {
        var outcome = Validator().Validate(Fixtures.Json(Fixtures.Review("comment", Fixtures.Finding(severity: "catastrophic"))), allowRepair: false);
        Assert.False(outcome.IsValid);
    }
}
