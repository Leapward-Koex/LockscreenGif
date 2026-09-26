namespace LockscreenGif.Privileged.Helper.Tracing;

internal sealed record TraceIdentity(int Pid, long Instance, string Name, int? Session, bool IsApp, DateTimeOffset Started);

internal sealed class TraceIdentityMap(int appPid, int helperPid, Action? onLimit = null)
{
    private readonly Dictionary<int, TraceIdentity> _processes = new();
    private readonly Dictionary<int, (int Pid, long Instance, DateTimeOffset At)> _threads = new();
    private long _nextInstance;

    public void ProcessStart(int pid, int parent, string name, int? session, DateTimeOffset at, bool rundown = false)
    {
        if (rundown && _processes.ContainsKey(pid))
        {
            return;
        }

        if (_processes.Count >= 65536)
        {
            _processes.Clear();
            onLimit?.Invoke();
        }
        var isApp = pid == appPid || pid == helperPid || (_processes.TryGetValue(parent, out var p) && p.IsApp);
        _processes[pid] = new(pid, ++_nextInstance, Path.GetFileName(name), session, isApp, at);
    }

    public void ProcessEnd(int pid) => _processes.Remove(pid);

    public void ThreadStart(int tid, int pid, DateTimeOffset at)
    {
        if (_threads.Count >= 131072)
        {
            _threads.Clear();
            onLimit?.Invoke();
        }
        _threads[tid] = (pid, _processes.GetValueOrDefault(pid)?.Instance ?? 0, at);
    }

    public void ThreadEnd(int tid) => _threads.Remove(tid);

    public TraceIdentity? Resolve(int pid, int tid, DateTimeOffset at)
    {
        if (pid < 0 && _threads.TryGetValue(tid, out var thread) && thread.At <= at)
        {
            pid = thread.Pid;
            if (_processes.GetValueOrDefault(pid)?.Instance != thread.Instance)
            {
                return null;
            }
        }
        return _processes.TryGetValue(pid, out var process) && process.Started <= at ? process : null;
    }
}
