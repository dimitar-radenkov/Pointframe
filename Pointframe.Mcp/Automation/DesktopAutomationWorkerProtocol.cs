using System.Text.Json;

namespace Pointframe.Mcp.Automation;

public static class DesktopAutomationWorkerProtocol
{
    public const int Version = 1;
    public const int MaxMessageBytes = 64 * 1024;

    public static class Operations
    {
        public const string Focus = "input.focus";
        public const string Click = "input.click";
        public const string PressKeys = "input.press_keys";
        public const string Drag = "input.drag";
        public const string EnterText = "input.enter_text";
        public const string Scroll = "input.scroll";
        public const string Release = "input.release";
        public const string Invoke = "uia.invoke";
        public const string SetValue = "uia.set_value";
    }

    public static string Serialize<T>(T message)
    {
        var json = JsonSerializer.Serialize(message);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxMessageBytes)
        {
            throw new InvalidDataException("The worker message exceeds the protocol limit.");
        }

        return json;
    }

    public static T Deserialize<T>(string json)
    {
        if (string.IsNullOrWhiteSpace(json)
            || System.Text.Encoding.UTF8.GetByteCount(json) > MaxMessageBytes)
        {
            throw new InvalidDataException("The worker message is empty or exceeds the protocol limit.");
        }

        return JsonSerializer.Deserialize<T>(json)
            ?? throw new InvalidDataException("The worker message was invalid.");
    }
}

public sealed record DesktopAutomationWorkerHello(
    int ProtocolVersion,
    int WorkerProcessId,
    int WindowsSessionId,
    string UserSid);

public sealed record DesktopAutomationWorkerRequest(
    int ProtocolVersion,
    string RequestId,
    string Operation,
    string? Payload = null);

public sealed record DesktopAutomationWorkerResponse(
    int ProtocolVersion,
    string RequestId,
    bool Succeeded,
    string Code,
    string? Payload = null);

public sealed record DesktopAutomationWorkerInputRequest(
    string Operation,
    object Request,
    Pointframe.Engine.Automation.Models.DesktopProcessIdentity ExpectedProcess);
