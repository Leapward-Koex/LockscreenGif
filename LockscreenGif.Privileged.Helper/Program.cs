using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using LockscreenGif.Privileged;
using LockscreenGif.Privileged.Helper.Tracing;

namespace LockscreenGif.Privileged.Helper;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 3 || !int.TryParse(args[1], out var pid))
        {
            return 2;
        }

        EtwFileCollector? collector = null;
        Task? watch = null;
        using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        try
        {
            using var pipe = new NamedPipeClientStream(
                ".",
                args[0],
                PipeDirection.InOut,
                PipeOptions.Asynchronous,
                TokenImpersonationLevel.Identification
            );
            await pipe.ConnectAsync(10000, lifetime.Token);
            using var parent = ParentIdentity.Verify(pipe, pid, args[2]);
            var repair = new CacheAccessRepair(args[2], lifetime: lifetime.Token);
            watch = WatchAsync(parent, lifetime, () => collector);
            long last = 0;
            while (!lifetime.IsCancellationRequested)
            {
                var request = await PipeProtocol.ReadAsync<HelperRequest>(pipe, lifetime.Token);
                if (request.Version != PipeProtocol.Version || request.Id <= last)
                {
                    throw new InvalidDataException("Invalid helper request.");
                }

                last = request.Id;
                HelperReply reply;
                try
                {
                    switch (request.Command)
                    {
                        case "DisableWindowsImageFeatureById":
                        case "EnableWindowsImageFeatureById":
                            if (request.Path is not null || request.Scope is not null || request.Write || request.FeatureId is null or 0)
                            {
                                throw new InvalidDataException(
                                    "A feature command requires a positive identifier and does not accept paths, scopes or write flags."
                                );
                            }
                            reply = new(
                                1,
                                request.Id,
                                WindowsImageFeature: request.Command == "EnableWindowsImageFeatureById"
                                    ? WindowsImageFeatureRepair.EnsureEnabled(request.FeatureId.Value)
                                    : WindowsImageFeatureRepair.EnsureDisabled(request.FeatureId.Value)
                            );
                            break;
                        case "Grant":
                            if (request.Path is null || request.Path.Length > 4096)
                            {
                                throw new InvalidDataException("Invalid path.");
                            }

                            reply = new(1, request.Id, await repair.GrantAsync(request.Path, request.Write));
                            break;
                        case "StartTrace":
                            if (
                                collector is not null
                                || request.Scope is null
                                || !string.Equals(
                                    Path.GetFullPath(request.Scope.CacheDirectory).TrimEnd('\\'),
                                    repair.Root,
                                    StringComparison.OrdinalIgnoreCase
                                )
                                || request.Scope.SourcePath.Length > 4096
                                || !Path.IsPathFullyQualified(request.Scope.SourcePath)
                            )
                            {
                                throw new InvalidDataException("Invalid trace scope.");
                            }

                            collector = new();
                            await collector.StartAsync(request.Scope, pid);
                            reply = new(1, request.Id);
                            break;
                        case "ReadTrace":
                            reply = new(
                                1,
                                request.Id,
                                Batch: collector?.Drain() ?? throw new InvalidOperationException("Trace was not started.")
                            );
                            break;
                        case "StopTrace":
                            if (collector is not null)
                            {
                                await collector.StopAsync();
                            }

                            reply = new(1, request.Id);
                            break;
                        default:
                            throw new InvalidDataException("Unknown helper operation.");
                    }
                }
                catch (Exception ex)
                {
                    reply = new(
                        1,
                        request.Id,
                        5,
                        $"{ex.GetType().Name} (0x{ex.HResult:X8}): {ex.Message[..Math.Min(ex.Message.Length, 512)]}"
                    );
                }
                await PipeProtocol.WriteAsync(pipe, reply, lifetime.Token);
            }
            return 0;
        }
        catch (Exception)
        {
            return 2;
        }
        finally
        {
            lifetime.Cancel();
            if (collector is not null)
            {
                try
                {
                    await collector.StopAsync("Helper connection closed.");
                }
                catch (Exception) { }
            }

            if (watch is not null)
            {
                try
                {
                    await watch;
                }
                catch (Exception) { }
            }
        }
    }

    private static async Task WatchAsync(Process parent, CancellationTokenSource lifetime, Func<EtwFileCollector?> trace)
    {
        try
        {
            using var self = Process.GetCurrentProcess();
            while (!lifetime.IsCancellationRequested)
            {
                await Task.Delay(500, lifetime.Token);
                if (parent.HasExited)
                {
                    lifetime.Cancel();
                    return;
                }
                self.Refresh();
                if (self.PrivateMemorySize64 > 256L * 1024 * 1024 && trace() is { } collector)
                {
                    await collector.StopAsync("Helper memory exceeded 256 MiB.");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            lifetime.Cancel();
        }
    }
}
