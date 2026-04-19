namespace Pointframe.Services.Messaging;

public sealed record UndoGroupMessage(IReadOnlyList<object> Elements);
