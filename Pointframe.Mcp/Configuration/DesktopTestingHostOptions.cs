namespace Pointframe.Mcp.Configuration;

public sealed record DesktopTestingHostOptions(
    bool Enabled,
    bool WorkerMode,
    string? PolicyPath,
    string? WorkerPipeName,
    int? ParentProcessId)
{
    public static DesktopTestingHostOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var enabled = false;
        var worker = false;
        string? policyPath = null;
        string? pipeName = null;
        int? parentPid = null;

        for (var index = 0; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "--desktop-testing":
                    enabled = true;
                    break;
                case "--desktop-worker":
                    worker = true;
                    break;
                case "--desktop-policy":
                    policyPath = ReadValue(args, ref index, args[index]);
                    enabled = true;
                    break;
                case "--desktop-testing-config":
                    policyPath = ReadValue(args, ref index, args[index]);
                    enabled = true;
                    break;
                case "--desktop-pipe":
                    pipeName = ReadValue(args, ref index, "--desktop-pipe");
                    break;
                case "--desktop-parent-pid":
                    if (!int.TryParse(ReadValue(args, ref index, "--desktop-parent-pid"), out var parsedPid) || parsedPid <= 0)
                    {
                        throw new ArgumentException("The desktop worker parent PID must be positive.", nameof(args));
                    }

                    parentPid = parsedPid;
                    break;
            }
        }

        if (worker && string.IsNullOrWhiteSpace(pipeName))
        {
            throw new ArgumentException("Desktop worker mode requires --desktop-pipe.", nameof(args));
        }

        return new DesktopTestingHostOptions(enabled, worker, policyPath, pipeName, parentPid);
    }

    private static string ReadValue(IReadOnlyList<string> args, ref int index, string flag)
    {
        if (++index >= args.Count || string.IsNullOrWhiteSpace(args[index]))
        {
            throw new ArgumentException($"{flag} requires a value.", nameof(args));
        }

        return args[index];
    }
}
