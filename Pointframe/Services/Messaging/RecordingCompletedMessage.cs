namespace Pointframe.Services.Messaging;

public sealed record RecordingCompletedMessage(string OutputPath, string ElapsedText);
