using SnippingTool.Models;
using SnippingTool.Services;

namespace SnippingTool.Tests;

internal sealed class FakeUserSettingsService : IUserSettingsService
{
    public UserSettings Current { get; } = new UserSettings();
    public void Save(UserSettings settings) { }
}
