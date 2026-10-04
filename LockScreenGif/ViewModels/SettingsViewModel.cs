using CommunityToolkit.Mvvm.ComponentModel;
using LockscreenGif.Services;

namespace LockscreenGif.ViewModels;

public sealed class SettingsViewModel(LockscreenPreferences preferences, VideoEditingPreferences videoEditingPreferences) : ObservableObject
{
    public LockscreenPreferences Preferences { get; } = preferences;

    public VideoEditingPreferences VideoEditingPreferences { get; } = videoEditingPreferences;
}
