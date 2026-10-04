using Sample.Lib;
using Xunit;

namespace Sample.Tests;

public class CalculatorTests
{
    [Fact]
    public void Add_SumsTwoNumbers()
    {
        Assert.Equal(5, Calculator.Add(2, 3));
    }
}
