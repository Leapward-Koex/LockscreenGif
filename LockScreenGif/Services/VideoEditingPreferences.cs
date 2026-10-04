using System.ComponentModel;
using System.Text.Json;

namespace LockscreenGif.Services;

/// <summary>Per-user video editing preferences, independent of current hardware availability.</summary>
public sealed class VideoEditingPreferences : INotifyPropertyChanged
{
    private readonly string _path;
    private bool _useHardwareDecoding = true;

    public VideoEditingPreferences(string path)
    {
        _path = Path.GetFullPath(path);
        try
        {
            _useHardwareDecoding = JsonSerializer.Deserialize<SavedPreferences>(File.ReadAllText(_path))?.UseHardwareDecoding ?? true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or JsonException)
        {
            // Availability is checked separately; missing or unreadable preferences use the default automatic decoding policy.
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool HasSaveError { get; private set; }

    public bool UseHardwareDecoding
    {
        get => _useHardwareDecoding;
        set
        {
            if (_useHardwareDecoding == value && !HasSaveError)
            {
                return;
            }
            _useHardwareDecoding = value;
            var temporaryPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(new SavedPreferences { UseHardwareDecoding = value }));
                File.Move(temporaryPath, _path, overwrite: true);
                HasSaveError = false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // Keep the preference for this session and leave any previously saved preference untouched.
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
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UseHardwareDecoding)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasSaveError)));
        }
    }

    private sealed class SavedPreferences
    {
        public bool UseHardwareDecoding { get; set; } = true;
    }
}
