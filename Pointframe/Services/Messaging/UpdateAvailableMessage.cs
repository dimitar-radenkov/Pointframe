namespace Pointframe.Services.Messaging;

public sealed record UpdateAvailableMessage(UpdateCheckResult Result, bool IsStartupCheck = false);
