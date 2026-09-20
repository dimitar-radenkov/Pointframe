using Pointframe.Engine;
using Pointframe.Engine.Automation.Models;
using Pointframe.Mcp;
using Pointframe.Mcp.Automation;
using Xunit;

namespace Pointframe.Tests.Mcp;

public class DesktopUiCheckEvaluationTests
{
    [Fact]
    public void ExistsMatchesAnElementByAutomationId()
    {
        var result = WorkerUiCheckSource.Evaluate(
            [Element("el-1-0", automationId: "saveButton")],
            new DesktopUiCheckCondition.Exists(ById("saveButton")));

        Assert.True(result.StateAvailable);
        Assert.True(result.Matches);
        Assert.Equal(1, result.MatchCount);
    }

    [Fact]
    public void AbsentMatchesWhenNothingIsFound()
    {
        var result = WorkerUiCheckSource.Evaluate(
            [Element("el-1-0", automationId: "saveButton")],
            new DesktopUiCheckCondition.Absent(ById("deleteButton")));

        Assert.True(result.Matches);
        Assert.Equal(0, result.MatchCount);
    }

    [Fact]
    public void TextEqualsComparesTheElementValue()
    {
        var result = WorkerUiCheckSource.Evaluate(
            [Element("el-1-0", automationId: "search", text: "hello from agent")],
            new DesktopUiCheckCondition.TextEquals(ById("search"), "hello from agent"));

        Assert.True(result.Matches);
    }

    [Fact]
    public void TextEqualsNeverMatchesASensitiveField()
    {
        // A password field reports no text by design. Treating that as a mismatch would be merely
        // wrong; treating it as a match would assert something that was never read.
        var result = WorkerUiCheckSource.Evaluate(
            [Element("el-1-0", automationId: "password", text: null, isSensitive: true)],
            new DesktopUiCheckCondition.TextEquals(ById("password"), string.Empty));

        Assert.False(result.Matches);
    }

    [Fact]
    public void ToggleEqualsIgnoresCase()
    {
        var result = WorkerUiCheckSource.Evaluate(
            [Element("el-1-0", automationId: "check", toggleState: "On")],
            new DesktopUiCheckCondition.ToggleEquals(ById("check"), "on"));

        Assert.True(result.Matches);
    }

    [Fact]
    public void EnabledReportsTheRealMatchCountWhenAmbiguous()
    {
        // The service rejects a count other than one for state predicates, so the count has to be
        // reported honestly rather than collapsed to the first hit.
        var result = WorkerUiCheckSource.Evaluate(
            [
                Element("el-1-0", automationId: "row"),
                Element("el-1-1", automationId: "row"),
            ],
            new DesktopUiCheckCondition.Enabled(ById("row")));

        Assert.Equal(2, result.MatchCount);
        Assert.False(result.Matches);
    }

    [Fact]
    public void LocatorScopesToAWindowWhenGiven()
    {
        var result = WorkerUiCheckSource.Evaluate(
            [
                Element("el-1-0", automationId: "ok", windowRef: "window-a"),
                Element("el-1-1", automationId: "ok", windowRef: "window-b"),
            ],
            new DesktopUiCheckCondition.Exists(
                new DesktopLocator(DesktopLocatorKind.AutomationId, AutomationId: "ok", WindowRef: "window-b")));

        Assert.Equal(1, result.MatchCount);
    }

    [Theory]
    [InlineData("exists")]
    [InlineData("absent")]
    [InlineData("enabled")]
    public void BuildConditionAcceptsEveryLocatorKind(string kind)
    {
        var condition = DesktopTestingMcpTools.BuildCondition(new McpUiCheckRequest(kind, AutomationId: "save"));

        Assert.NotNull(condition);
    }

    [Fact]
    public void BuildConditionRejectsAnUnknownKind()
    {
        Assert.Throws<ArgumentException>(() =>
            DesktopTestingMcpTools.BuildCondition(new McpUiCheckRequest("teleport", AutomationId: "save")));
    }

    [Fact]
    public void BuildConditionRequiresAnExpectedValueWhereItIsMeaningful()
    {
        Assert.Throws<ArgumentException>(() =>
            DesktopTestingMcpTools.BuildCondition(new McpUiCheckRequest("textEquals", AutomationId: "search")));
    }

    [Fact]
    public void BuildConditionRequiresAWindowReferenceForWindowChecks()
    {
        Assert.Throws<ArgumentException>(() =>
            DesktopTestingMcpTools.BuildCondition(new McpUiCheckRequest("windowExists")));
    }

    [Fact]
    public void BuildConditionTreatsEnabledFalseAsExpectingDisabled()
    {
        var condition = DesktopTestingMcpTools.BuildCondition(
            new McpUiCheckRequest("enabled", AutomationId: "save", Expected: "false"));

        Assert.False(Assert.IsType<DesktopUiCheckCondition.Enabled>(condition).Expected);
    }

    private static DesktopLocator ById(string automationId) =>
        new(DesktopLocatorKind.AutomationId, AutomationId: automationId);

    private static DesktopUiElementSnapshot Element(
        string elementRef,
        string? automationId = null,
        string windowRef = "window-a",
        string? text = null,
        string? toggleState = null,
        bool isSensitive = false) =>
        new(
            elementRef,
            windowRef,
            "Button",
            "Save",
            automationId,
            new PixelBounds(0, 0, 10, 10),
            IsEnabled: true,
            ToggleState: toggleState,
            Selection: null,
            Text: text,
            IsSensitive: isSensitive);
}
