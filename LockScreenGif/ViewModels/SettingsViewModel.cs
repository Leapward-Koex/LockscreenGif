using CommunityToolkit.Mvvm.ComponentModel;
using LockscreenGif.Services;

namespace LockscreenGif.ViewModels;

public sealed class SettingsViewModel(LockscreenPreferences preferences) : ObservableObject
{
    public LockscreenPreferences Preferences { get; } = preferences;
}
