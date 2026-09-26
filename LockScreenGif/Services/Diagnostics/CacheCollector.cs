using System.Collections.Concurrent;
using LockscreenGif.Models.Diagnostics;

namespace LockscreenGif.Services.Diagnostics;

/// <summary>Read-only cache inspection. Watcher notifications are hints, never proof of the writer.</summary>
internal sealed class CacheCollector : IDisposable
{
    private readonly string _root;
    private readonly Action<string, string> _notice;
    private readonly Action? _changed;
    private readonly ConcurrentDictionary<string, long> _dirty = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CacheFileEvidence> _previous = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _serial = new(1, 1);
    private FileSystemWatcher? _watcher;
    private readonly object _watcherGate = new();
    private int _watcherFaulted;
    private long _generation;
    private long _overflow;
    private long _observedOverflow;
    private bool _disposed;

    public CacheCollector(string root, Action<string, string> notice, Action? changed = null)
    {
        _root = root;
        _notice = notice;
        _changed = changed;
    }

    public void Start()
    {
        lock (_watcherGate)
        {
            StartCore();
        }
    }

    private void StartCore()
    {
        if (Interlocked.Exchange(ref _watcherFaulted, 0) != 0)
        {
            _watcher?.Dispose();
            _watcher = null;
        }
        if (_watcher is not null || _disposed)
        {
            return;
        }

        try
        {
            var watcher = new FileSystemWatcher(_root, "LockScreen*")
            {
                IncludeSubdirectories = true,
                NotifyFilter =
                    NotifyFilters.FileName
                    | NotifyFilters.DirectoryName
                    | NotifyFilters.LastWrite
                    | NotifyFilters.Size
                    | NotifyFilters.CreationTime
                    | NotifyFilters.Security,
            };
            watcher.Changed += (_, e) => Dirty(e.FullPath, e.ChangeType.ToString());
            watcher.Created += (_, e) => Dirty(e.FullPath, "Created");
            watcher.Deleted += (_, e) => Dirty(e.FullPath, "Deleted");
            watcher.Renamed += (_, e) =>
            {
                Dirty(e.OldFullPath, "Renamed from");
                Dirty(e.FullPath, "Renamed to");
            };
            watcher.Error += (_, e) =>
            {
                Interlocked.Exchange(ref _watcherFaulted, 1);
                Interlocked.Increment(ref _overflow);
                _notice($"File watcher lost coverage: {e.GetException().GetType().Name}. Reconciliation required.", "Warning");
                _changed?.Invoke();
            };
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
            Interlocked.Increment(ref _overflow);
            _notice("File watcher started. File events do not identify the responsible process.", "Info");
        }
        catch (Exception ex)
        {
            _notice($"File watcher unavailable: {ex.GetType().Name} (0x{ex.HResult:X8}). Polling will retry.", "Warning");
        }
    }

    private void Dirty(string path, string action)
    {
        _dirty[path] = Interlocked.Increment(ref _generation);
        _notice($"{action}: {path}", "Info");
        _changed?.Invoke();
    }

    public async Task<CacheSnapshot> CaptureAsync(string reason, bool force, CancellationToken token)
    {
        await _serial.WaitAsync(token);
        try
        {
            Start();
            var snapshot = new CacheSnapshot { Reason = reason };
            var overflow = Interlocked.Read(ref _overflow);
            force |= _watcher is null || overflow != _observedOverflow;
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
            budget.CancelAfter(TimeSpan.FromSeconds(15));
            string[] files;
            try
            {
                var inventory = new List<string>();
                foreach (
                    var path in Directory.EnumerateFiles(
                        _root,
                        "LockScreen*",
                        new EnumerationOptions
                        {
                            RecurseSubdirectories = true,
                            IgnoreInaccessible = false,
                            AttributesToSkip = FileAttributes.ReparsePoint,
                        }
                    )
                )
                {
                    token.ThrowIfCancellationRequested();
                    if (budget.IsCancellationRequested || inventory.Count >= 512)
                    {
                        snapshot.Complete = false;
                        snapshot.Errors.Add("Cache enumeration limit reached; inventory is incomplete.");
                        break;
                    }
                    inventory.Add(path);
                }
                files = inventory.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                snapshot.Complete = false;
                snapshot.Errors.Add(
                    $"Cache inventory unavailable: {ex.GetType().Name} (0x{ex.HResult:X8}). Missing files cannot be inferred."
                );
                return snapshot;
            }
            foreach (var path in files)
            {
                token.ThrowIfCancellationRequested();
                if (budget.IsCancellationRequested)
                {
                    snapshot.Complete = false;
                    snapshot.Errors.Add("Inspection time budget reached. Remaining files were not verified.");
                    break;
                }
                try
                {
                    var metadata = new FileInfo(path);
                    _dirty.TryGetValue(path, out var revision);
                    CacheFileEvidence state;
                    if (
                        !force
                        && revision == 0
                        && _previous.TryGetValue(path, out var cached)
                        && cached.Stable
                        && cached.Length == metadata.Length
                        && cached.LastWriteUtc == metadata.LastWriteTimeUtc
                        && cached.CreationUtc == metadata.CreationTimeUtc
                        && cached.HashReadAt > DateTimeOffset.UtcNow.AddSeconds(-15)
                    )
                    {
                        state = new CacheFileEvidence
                        {
                            Path = path,
                            Length = cached.Length,
                            LastWriteUtc = cached.LastWriteUtc,
                            CreationUtc = cached.CreationUtc,
                            Sha256 = cached.Sha256,
                            Format = cached.Format,
                            Stable = true,
                            HashReadAt = cached.HashReadAt,
                            HashSource = "Cached; unchanged metadata and no observed event",
                        };
                    }
                    else
                    {
                        state = await CacheFileReader.ReadAsync(path, budget.Token);
                    }

                    snapshot.Files.Add(state);
                    if (state.Stable)
                    {
                        _previous[path] = state;
                        ((ICollection<KeyValuePair<string, long>>)_dirty).Remove(new(path, revision));
                    }
                    else
                    {
                        snapshot.Complete = false;
                    }
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    snapshot.Complete = false;
                    snapshot.Errors.Add($"Hash time budget reached for {path}.");
                    break;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    snapshot.Complete = false;
                    snapshot.Files.Add(
                        new CacheFileEvidence { Path = path, Error = $"{ex.GetType().Name} (0x{ex.HResult:X8}): {ex.Message}" }
                    );
                    _dirty[path] = Interlocked.Increment(ref _generation);
                }
            }
            if (snapshot.Complete)
            {
                _observedOverflow = overflow;
            }

            return snapshot;
        }
        finally
        {
            _serial.Release();
        }
    }

    public void Dispose()
    {
        lock (_watcherGate)
        {
            _disposed = true;
            _watcher?.Dispose();
            _watcher = null;
        }
    }
}
