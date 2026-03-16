using System.Windows;
using Microsoft.Win32;
using SnippingTool.Models;

namespace SnippingTool.Services;

public sealed class ThemeService : IThemeService
{
    private const string LightThemeUri = "pack://application:,,,/Themes/Light.xaml";
    private const string DarkThemeUri = "pack://application:,,,/Themes/Dark.xaml";

    public void Apply(AppTheme theme)
    {
        var uri = new Uri(IsDark(theme) ? DarkThemeUri : LightThemeUri, UriKind.Absolute);
        SwapThemeDictionary(uri);
    }

    public bool IsDark(AppTheme theme) =>
        theme switch
        {
            AppTheme.Dark => true,
            AppTheme.Light => false,
            AppTheme.System => !IsWindowsLightTheme(),
            _ => false,
        };

    private static void SwapThemeDictionary(Uri uri)
    {
        var merged = System.Windows.Application.Current.Resources.MergedDictionaries;

        // Remove any previously loaded theme dictionary.
        var existing = merged.FirstOrDefault(d => d.Source != null &&
            (d.Source.AbsoluteUri == LightThemeUri ||
             d.Source.AbsoluteUri == DarkThemeUri));

        if (existing != null)
        {
            merged.Remove(existing);
        }

        merged.Add(new ResourceDictionary { Source = uri });
    }

    private static bool IsWindowsLightTheme()
    {
        try
        {
            var value = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme",
                defaultValue: 1);

            return value is int i && i != 0;
        }
        catch
        {
            return true; // default to light on any registry error
        }
    }
}
