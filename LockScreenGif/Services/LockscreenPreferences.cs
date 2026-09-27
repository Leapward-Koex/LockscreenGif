using System.ComponentModel;
using System.Text.Json;

namespace LockscreenGif.Services;

/// <summary>Shared, per-user preferences for normal applies and diagnostic tests.</summary>
public sealed class LockscreenPreferences : INotifyPropertyChanged
{
    private readonly string _path;
    private bool _useWindowsApi;

    public LockscreenPreferences(string path)
    {
        _path = path;
        try
        {
            if (File.Exists(path))
            {
                _useWindowsApi = JsonSerializer.Deserialize<SavedPreferences>(File.ReadAllText(path))?.UseWindowsApi == true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // An unreadable preference must never enable the optional Windows API.
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool HasSaveError { get; private set; }

    public bool UseWindowsApi
    {
        get => _useWindowsApi;
        set
        {
            if (_useWindowsApi == value && !HasSaveError)
            {
                return;
            }

            _useWindowsApi = value;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var temporaryPath = _path + ".tmp";
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(new SavedPreferences { UseWindowsApi = value }));
                File.Move(temporaryPath, _path, overwrite: true);
                HasSaveError = false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                HasSaveError = true;
            }

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UseWindowsApi)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasSaveError)));
        }
    }

    private sealed class SavedPreferences
    {
        public bool UseWindowsApi { get; set; }
    }
}
