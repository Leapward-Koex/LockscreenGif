using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using LockscreenGif.Models.Diagnostics;
using LockscreenGif.Privileged;

namespace LockscreenGif.Services.Diagnostics;

/// <summary>Synchronizes in-memory evidence and immutable UI snapshots.</summary>
internal sealed class DiagnosticRecorder
{
    private readonly object _gate = new();
    private readonly DiagnosticSession _session;
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private long _lastNotification;
    public event Action? Changed;

    public DiagnosticRecorder(DiagnosticSession session)
    {
        _session = session;
    }

    private static readonly JsonSerializerOptions DisplayOptions = CreateDisplayOptions();

    private static JsonSerializerOptions CreateDisplayOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            if (info.Type != typeof(ProcessTraceEvidence))
            {
                return;
            }

            foreach (var property in info.Properties.Where(p => p.Name is "Operations" or "Files"))
            {
                property.ShouldSerialize = (_, _) => false;
            }
        });
        return new() { TypeInfoResolver = resolver };
    }

    public DiagnosticSession Snapshot(bool includeTraceDetails = true)
    {
        // The page needs status and findings, never thousands of raw operations.
        lock (_gate)
        {
            return JsonSerializer.Deserialize<DiagnosticSession>(
                JsonSerializer.Serialize(_session, includeTraceDetails ? null : DisplayOptions)
            )!;
        }
    }

    public void Update(Action<DiagnosticSession> update, bool notify = true)
    {
        lock (_gate)
        {
            update(_session);
        }
        if (notify)
        {
            Changed?.Invoke();
        }
    }

    public void Add(string category, string message, string severity = "Info")
    {
        var reachedLimit = false;
        lock (_gate)
        {
            if (_session.Events.Count >= 2000)
            {
                _session.MonitoringComplete = false;
                if (_session.Events.Count != 2000)
                {
                    return;
                }

                _session.Events.Add(
                    new(
                        DateTimeOffset.UtcNow,
                        _elapsed.ElapsedMilliseconds,
                        "Collector",
                        "Event limit reached; subsequent events omitted.",
                        "Warning"
                    )
                );
                // Notify the first loss immediately.
                reachedLimit = true;
            }
            else
            {
                _session.Events.Add(new(DateTimeOffset.UtcNow, _elapsed.ElapsedMilliseconds, category, message, severity));
            }
        }
        if (reachedLimit || _elapsed.ElapsedMilliseconds - Interlocked.Read(ref _lastNotification) > 150)
        {
            Interlocked.Exchange(ref _lastNotification, _elapsed.ElapsedMilliseconds);
            Changed?.Invoke();
        }
    }

    public void Phase(string phase)
    {
        Add("Session", phase);
        Update(s => s.Phase = phase);
    }
}
