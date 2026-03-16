using SnippingTool.Models;

namespace SnippingTool.Services;

public interface IThemeService
{
    /// <summary>Applies the given theme, resolving <see cref="AppTheme.System"/> to Light or Dark
    /// by reading the Windows "AppsUseLightTheme" registry value.</summary>
    void Apply(AppTheme theme);

    /// <summary>Returns <c>true</c> when the effective theme is Dark.</summary>
    bool IsDark(AppTheme theme);
}
