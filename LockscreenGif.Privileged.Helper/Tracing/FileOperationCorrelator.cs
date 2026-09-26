using LockscreenGif.Privileged;

namespace LockscreenGif.Privileged.Helper.Tracing;

internal sealed class FileOperationCorrelator(TracePathScope scope, TraceIdentityMap identities, TraceBuffer buffer)
{
    private sealed record Name(string? Path, DateTimeOffset At);

    private sealed record Pending(
        DateTimeOffset At,
        ulong Object,
        ulong Key,
        string Operation,
        int Pid,
        int Tid,
        TraceIdentity? Identity,
        long? Bytes,
        string? Path,
        uint? Status = null,
        long? Completed = null,
        DateTimeOffset? CompletedAt = null
    );

    private readonly HashSet<ulong> _unrelated = new();
    private readonly Dictionary<ulong, Name> _names = new();
    private readonly Dictionary<ulong, Name> _objects = new();
    private readonly Dictionary<ulong, Pending> _pending = new();
    private readonly Dictionary<ulong, HashSet<ulong>> _waitingForName = new();
    private DateTimeOffset _lastSweep;
    private readonly Dictionary<ulong, (DateTimeOffset At, uint Status, long Bytes)> _earlyEnds = new();

    public void NameFile(ulong key, string rawPath, DateTimeOffset at)
    {
        var path = scope.Normalize(rawPath);
        Put(_names, key, new(path is not null && scope.Contains(path) ? path : null, at));
        ResolvePending(key);
    }

    public void ForgetName(ulong key) => _names.Remove(key);

    public void Close(ulong fileObject) => _objects.Remove(fileObject);

    public void Begin(
        ulong irp,
        ulong fileObject,
        ulong key,
        string operation,
        int pid,
        int tid,
        DateTimeOffset at,
        long? bytes = null,
        string? rawPath = null
    )
    {
        Sweep(at);
        if (RemovePending(irp) is { } previous)
        {
            Emit(previous);
        }

        _unrelated.Remove(irp);
        string? path = null;
        if (rawPath is not null)
        {
            var normalized = scope.Normalize(rawPath);
            path = normalized is not null && scope.Contains(normalized) ? normalized : null;
            Put(_objects, fileObject, new(path, at));
            if (path is null)
            {
                Ignore(irp);
                return;
            }
        }
        else if (Lookup(fileObject, key, at, out path) && path is null)
        {
            Ignore(irp);
            return;
        }
        if (_pending.Count >= 4096)
        {
            buffer.Evidence.UnmatchedOperations++;
            return;
        }
        var item = new Pending(at, fileObject, key, operation, pid, tid, identities.Resolve(pid, tid, at), bytes, path);
        _pending[irp] = item;
        if (item.Path is null && key != 0)
        {
            if (!_waitingForName.TryGetValue(key, out var waiting))
            {
                _waitingForName[key] = waiting = [];
            }

            waiting.Add(irp);
        }
        if (_earlyEnds.Remove(irp, out var end) && end.At >= at && end.At - at <= TimeSpan.FromSeconds(5))
        {
            End(irp, end.Status, end.Bytes, end.At);
        }
    }

    public void End(ulong irp, uint status, long bytes, DateTimeOffset at)
    {
        if (_unrelated.Remove(irp))
        {
            return;
        }

        if (_pending.TryGetValue(irp, out var item) && at >= item.At)
        {
            item = item with { Status = status, Completed = item.Operation is "Read" or "Write" ? bytes : null, CompletedAt = at };
            if (item.Path is not null)
            {
                RemovePending(irp);
                Emit(item);
            }
            else
            {
                _pending[irp] = item;
            }
        }
        else
        {
            if (_earlyEnds.Count >= 4096)
            {
                buffer.Evidence.UnmatchedCompletions += _earlyEnds.Count;
                _earlyEnds.Clear();
            }
            _earlyEnds[irp] = (at, status, bytes);
        }
    }

    public void Finish()
    {
        foreach (var item in _pending.Values)
        {
            Emit(item);
        }

        buffer.Evidence.UnmatchedCompletions += _earlyEnds.Count;
        _pending.Clear();
        _waitingForName.Clear();
        _earlyEnds.Clear();
        _unrelated.Clear();
    }

    private void ResolvePending(ulong key)
    {
        // Rundown can contain thousands of unrelated filenames. Do not scan every
        // pending operation for each one while the five-second drain clock is running.
        if (!_waitingForName.TryGetValue(key, out var waiting))
        {
            return;
        }

        foreach (var irp in waiting.ToArray())
        {
            var pending = _pending[irp];
            if (!Lookup(pending.Object, pending.Key, pending.At, out var path))
            {
                continue;
            }

            RemovePending(irp);
            if (path is null)
            {
                Ignore(irp);
                continue;
            }
            var resolved = pending with { Path = path };
            if (resolved.Status is not null)
            {
                Emit(resolved);
            }
            else
            {
                _pending[irp] = resolved;
            }
        }
    }

    private bool Lookup(ulong obj, ulong key, DateTimeOffset at, out string? path)
    {
        var keyName = key != 0 ? _names.GetValueOrDefault(key) : null;
        var objectName = obj != 0 ? _objects.GetValueOrDefault(obj) : null;
        var name = keyName is not null && (objectName is null || keyName.At >= objectName.At) ? keyName : objectName;
        var valid = name is not null && name.At <= at;
        path = valid ? name!.Path : null;
        return valid;
    }

    private void Emit(Pending item)
    {
        if (item.Path is null)
        {
            buffer.Evidence.UnresolvedPaths++;
            return;
        }
        if (item.Status is null)
        {
            buffer.Evidence.UnmatchedOperations++;
        }

        var identity = item.Identity; // Never resolve an old event against a newer PID lifetime.
        buffer.Add(
            new(
                item.At,
                item.Path,
                item.Operation,
                identity?.Pid ?? item.Pid,
                identity?.Instance ?? 0,
                identity?.Name ?? "Unknown",
                identity?.Session,
                identity?.IsApp ?? false,
                identity is not null,
                item.Bytes,
                item.Completed,
                item.Status,
                CompletedAt: item.CompletedAt
            )
        );
    }

    private void Sweep(DateTimeOffset at)
    {
        if (_pending.Count < 2048 || at - _lastSweep < TimeSpan.FromMilliseconds(100))
        {
            return;
        }

        _lastSweep = at;
        foreach (var (key, item) in _pending.Where(p => at - p.Value.At > TimeSpan.FromSeconds(5)).ToArray())
        {
            RemovePending(key);
            Emit(item);
        }
    }

    private Pending? RemovePending(ulong irp)
    {
        if (!_pending.Remove(irp, out var item))
        {
            return null;
        }

        if (_waitingForName.TryGetValue(item.Key, out var waiting))
        {
            waiting.Remove(irp);
            if (waiting.Count == 0)
            {
                _waitingForName.Remove(item.Key);
            }
        }
        return item;
    }

    private void Ignore(ulong irp)
    {
        _earlyEnds.Remove(irp);
        if (_unrelated.Count >= 4096)
        {
            _unrelated.Clear();
        }

        _unrelated.Add(irp);
    }

    private void Put(Dictionary<ulong, Name> map, ulong key, Name name)
    {
        if (key == 0 || (map.TryGetValue(key, out var existing) && existing.At > name.At))
        {
            return;
        }

        if (map.Count >= 65536)
        {
            map.Clear();
            buffer.Evidence.UnresolvedPaths++;
        }
        map[key] = name;
    }
}
