using LockscreenGif.Contracts.Services;
using LockscreenGif.Helpers;

using Microsoft.UI.Xaml;

namespace LockscreenGif.Services;

public class ThemeSelectorService : IThemeSelectorService
{

    public ElementTheme Theme { get; set; } = ElementTheme.Default;


    public ThemeSelectorService()
    {
    }

    public async Task InitializeAsync()
    {
        Theme = await LoadThemeFromSettingsAsync();
        await Task.CompletedTask;
    }

    public async Task SetThemeAsync(ElementTheme theme)
    {
        Theme = theme;

        await SetRequestedThemeAsync();
    }

    public async Task SetRequestedThemeAsync()
    {
        if (App.MainWindow.Content is FrameworkElement rootElement)
        {
            rootElement.RequestedTheme = Theme;

            TitleBarHelper.UpdateTitleBar(Theme);
        }

        await Task.CompletedTask;
    }

    private Task<ElementTheme> LoadThemeFromSettingsAsync()
    {

        return Task.FromResult(ElementTheme.Default);
    }

}
