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
        _path = Path.GetFullPath(path);
        try
        {
            _useWindowsApi = JsonSerializer.Deserialize<SavedPreferences>(File.ReadAllText(_path))?.UseWindowsApi == true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or JsonException)
        {
            // Missing, unreadable or malformed preferences must not enable the optional Windows API.
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
            var temporaryPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(new SavedPreferences { UseWindowsApi = value }));
                File.Move(temporaryPath, _path, overwrite: true);
                HasSaveError = false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // Keep the requested value for this session while preserving the prior saved file.
                HasSaveError = true;
            }
            finally
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
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
