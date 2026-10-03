using System.Globalization;
using System.Text.Json;
using LockscreenGif.Privileged;

namespace LockscreenGif.Services.Lockscreen;

/// <summary>A per-user choice of which Windows feature to inspect or explicitly configure.</summary>
public sealed class WindowsImageFeatureSettings
{
    private static readonly Lazy<WindowsImageFeatureSettings> CurrentSettings = new(() =>
        new WindowsImageFeatureSettings(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LockscreenGif",
                "windows-image-feature.json"
            )
        )
    );

    private readonly object _gate = new();
    private readonly string _path;
    private uint _featureId = WindowsImageFeature.DefaultFeatureId;

    public static WindowsImageFeatureSettings Current => CurrentSettings.Value;

    public WindowsImageFeatureSettings(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        Load();
    }

    public uint FeatureId
    {
        get
        {
            lock (_gate)
            {
                return _featureId;
            }
        }
    }

    public WindowsImageFeatureState Read()
    {
        var featureId = FeatureId;
        return WindowsImageFeature.Read(featureId);
    }

    public static bool TryParseFeatureId(string? text, out uint featureId)
    {
        featureId = 0;
        return text is not null
            && uint.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out featureId)
            && featureId > 0;
    }

    public void Save(uint featureId)
    {
        ArgumentOutOfRangeException.ThrowIfZero(featureId);
        lock (_gate)
        {
            var directory = Path.GetDirectoryName(_path)!;
            var temporary = Path.Combine(directory, Path.GetFileName(_path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                Directory.CreateDirectory(directory);
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, new Preference { FeatureId = featureId });
                    stream.Flush(flushToDisk: true);
                }
                // Replace within one directory so a failed save leaves the previous preference intact.
                File.Move(temporary, _path, overwrite: true);
                _featureId = featureId;
                Logger.Info($"Windows image feature preference saved: FeatureId={featureId}.");
            }
            catch (Exception ex)
            {
                Logger.Error("Windows image feature preference could not be saved; the active feature ID is unchanged.", ex);
                throw;
            }
            finally
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (Exception ex)
                {
                    Logger.Error("Temporary Windows image feature preference file could not be removed.", ex);
                }
            }
        }
    }

    public void Reset() => Save(WindowsImageFeature.DefaultFeatureId);

    private void Load()
    {
        try
        {
            var preference = JsonSerializer.Deserialize<Preference>(File.ReadAllText(_path));
            if (preference is null || preference.FeatureId == 0)
            {
                throw new InvalidDataException("The saved feature ID must be a positive unsigned integer.");
            }
            _featureId = preference.FeatureId;
            Logger.Info($"Windows image feature preference loaded: FeatureId={_featureId}.");
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            Logger.Info($"No Windows image feature preference is saved; using default FeatureId={WindowsImageFeature.DefaultFeatureId}.");
        }
        catch (Exception ex)
        {
            Logger.Error(
                $"Windows image feature preference is unavailable or invalid; using default FeatureId={WindowsImageFeature.DefaultFeatureId}.",
                ex
            );
        }
    }

    private sealed class Preference
    {
        public uint FeatureId { get; set; }
    }
}
