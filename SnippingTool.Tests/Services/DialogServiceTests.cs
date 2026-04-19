using System.Reflection;
using Pointframe.Services;
using Pointframe.Tests.Services.Handlers;
using Xunit;
using Forms = System.Windows.Forms;

namespace Pointframe.Tests.Services;

public sealed class DialogServiceTests
{
    [Fact]
    public void GetOwnerWindowHandle_WithoutApplication_ReturnsZero()
    {
        StaTestHelper.Run(() =>
        {
            var handle = InvokeGetOwnerWindowHandle();

            Assert.Equal(IntPtr.Zero, handle);
        });
    }

    [Fact]
    public void CreateDialogOwner_WithoutApplication_CreatesTemporaryOwner()
    {
        StaTestHelper.Run(() =>
        {
            var owner = InvokeCreateDialogOwner();

            try
            {
                var windowOwner = Assert.IsAssignableFrom<Forms.IWin32Window>(owner);
                Assert.NotEqual(IntPtr.Zero, windowOwner.Handle);
                Assert.Equal("TemporaryDialogOwner", owner.GetType().Name);
            }
            finally
            {
                ((IDisposable)owner).Dispose();
            }
        });
    }

    private static IntPtr InvokeGetOwnerWindowHandle()
    {
        var method = typeof(DialogService).GetMethod("GetOwnerWindowHandle", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (IntPtr)method.Invoke(null, null)!;
    }

    private static object InvokeCreateDialogOwner()
    {
        var method = typeof(DialogService).GetMethod("CreateDialogOwner", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return method.Invoke(null, null)!;
    }
}