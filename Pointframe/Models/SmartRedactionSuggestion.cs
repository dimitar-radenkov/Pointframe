using System.Windows;

namespace Pointframe.Models;

public sealed record SmartRedactionSuggestion(
    Int32Rect PixelBounds,
    SensitiveDataType Type);
