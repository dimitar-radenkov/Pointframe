using Pointframe.Cli;
using Xunit;

namespace Pointframe.Tests.Cli;

public sealed class VerificationNextStepTests
{
    [Theory]
    [InlineData("pass", null, "none")]
    [InlineData("fail", null, "fix_code")]
    [InlineData("error", "mcp_not_found", "run_command")]
    [InlineData("error", "desktop_busy", "wait")]
    [InlineData("error", "spec_untrusted", "needs_person")]
    [InlineData("error", "mcp_version_mismatch", "run_command")]
    [InlineData("error", "app_not_found", "run_command")]
    [InlineData("error", "app_running", "fix_code")]
    [InlineData("error", "approver_unavailable", "needs_person")]
    [InlineData("error", "not_approved", "needs_person")]
    [InlineData("error", "not_trusted", "needs_person")]
    [InlineData("error", "trust_needs_terminal", "needs_person")]
    [InlineData("error", "replace_not_approved", "needs_person")]
    [InlineData("error", "spec_invalid", "fix_code")]
    [InlineData("error", "scenario_not_found", "fix_code")]
    [InlineData("error", "task_invalid", "fix_code")]
    [InlineData("error", "task_file_not_found", "fix_code")]
    [InlineData("error", "task_id_invalid", "fix_code")]
    [InlineData("error", "task_exists", "fix_code")]
    [InlineData("error", "task_not_found", "run_command")]
    [InlineData("error", "task_not_covered", "run_command")]
    [InlineData("error", "verdict_for_another_task", "run_command")]
    [InlineData("error", "tree_changed", "run_command")]
    [InlineData("error", "verdict_for_another_spec", "run_command")]
    [InlineData("error", "examiner_failed", "needs_person")]
    [InlineData("error", "examiner_invalid", "needs_person")]
    [InlineData("error", "fail_before_rejected", "needs_person")]
    [InlineData("error", "not_a_real_code", "needs_person")]
    public void For_MapsOutcomeToKind(string status, string? code, string kind)
    {
        var result = VerificationNextSteps.For(status, code);
        Assert.Equal(kind, result.Kind);
    }

    [Fact]
    public void For_FailedGateNamesGate()
    {
        var result = VerificationNextSteps.For("fail", null, failureId: "build");
        Assert.Equal("fix_code", result.Kind);
        Assert.Contains("build", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void For_TreeChangedProvidesRunCommand()
    {
        var result = VerificationNextSteps.For("stale", "tree_changed");
        Assert.Equal("pointframe verify run", result.Command);
    }
}
