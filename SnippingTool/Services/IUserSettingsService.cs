using SnippingTool.Models;

namespace SnippingTool.Services;

public interface IUserSettingsService
{
    UserSettings Current { get; }
    void Save(UserSettings settings);
    void Update(Action<UserSettings> mutate);
}
