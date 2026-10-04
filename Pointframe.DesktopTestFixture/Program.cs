namespace Pointframe.DesktopTestFixture;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new Form1(args.Contains("--scroll-fixture", StringComparer.OrdinalIgnoreCase)));
    }
}
