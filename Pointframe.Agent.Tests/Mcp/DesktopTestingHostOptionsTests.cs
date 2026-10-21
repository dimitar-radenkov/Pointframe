using Pointframe.Mcp.Configuration;
using Xunit;

namespace Pointframe.Tests.Mcp;

public sealed class DesktopTestingHostOptionsTests
{
    [Fact]
    public void Parse_AllowsDeprecatedConfigAlias()
    {
        var options = DesktopTestingHostOptions.Parse(["--desktop-testing-config", "desktop-policy.json"]);

        Assert.True(options.Enabled);
        Assert.Equal("desktop-policy.json", options.PolicyPath);
    }

    [Fact]
    public void Parse_ReportsTheActualFlagWhenValueIsMissing()
    {
        var exception = Assert.Throws<ArgumentException>(() => DesktopTestingHostOptions.Parse(["--desktop-testing-config"]));

        Assert.Contains("--desktop-testing-config", exception.Message, StringComparison.Ordinal);
    }
}
