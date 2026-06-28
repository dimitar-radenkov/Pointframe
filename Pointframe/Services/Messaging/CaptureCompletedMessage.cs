namespace Pointframe.Services.Messaging;

public sealed record CaptureCompletedMessage(string? OutputPath, string CaptureAction);
