namespace Pointframe.Services.Messaging;

public sealed record RedoGroupMessage(IReadOnlyList<object> Elements);
