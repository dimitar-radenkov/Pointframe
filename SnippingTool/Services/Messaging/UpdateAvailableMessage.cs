using SnippingTool.Models;

namespace SnippingTool.Services.Messaging;

public sealed record UpdateAvailableMessage(UpdateCheckResult Result);
