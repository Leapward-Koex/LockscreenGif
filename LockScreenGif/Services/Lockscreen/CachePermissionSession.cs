using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using LockscreenGif.Privileged;
using Microsoft.Win32.SafeHandles;

namespace LockscreenGif.Services.Lockscreen;

/// <summary>One authenticated helper connection, including its cached launch failure, for the entire operation.</summary>
internal sealed class CachePermissionSession(
    string root,
    string sid,
    Func<ProcessStartInfo, Process?>? launch = null,
    TimeSpan? connectionTimeout = null,
    TimeSpan? requestTimeout = null
) : IPrivilegedOperationSession
{
    private readonly SemaphoreSlim _serial = new(1, 1);
    private Task? _connection;
    private NamedPipeServerStream? _pipe;
    private Process? _process;
    private Exception? _broken;
    private long _requestId;
    private bool _disposed;

    public async Task<int> GrantAsync(string path, bool write, CancellationToken token) =>
        (await RequestAsync(new(1, 0, "Grant", path, write), token)).ExitCode;

    public Task<WindowsImageFeatureResult> DisableWindowsImageFeatureAsync(
        CancellationToken token,
        uint featureId = WindowsImageFeature.DefaultFeatureId
    ) => RequestFeatureAsync("DisableWindowsImageFeatureById", "Disabled", featureId, token);

    public Task<WindowsImageFeatureResult> EnableWindowsImageFeatureAsync(
        CancellationToken token,
        uint featureId = WindowsImageFeature.DefaultFeatureId
    ) => RequestFeatureAsync("EnableWindowsImageFeatureById", "Enabled", featureId, token);

    private async Task<WindowsImageFeatureResult> RequestFeatureAsync(
        string command,
        string desired,
        uint featureId,
        CancellationToken token
    )
    {
        ArgumentOutOfRangeException.ThrowIfZero(featureId);
        var reply = await RequestAsync(new(1, 0, command, FeatureId: featureId), token);
        try
        {
            EnsureSuccess(reply);
            var result = reply.WindowsImageFeature ?? throw new InvalidDataException("Missing Windows image feature response.");
            if (
                result.FeatureId != featureId
                || result.DesiredState != desired
                || (result.Before is not null && result.Before.FeatureId != featureId)
                || (result.After is not null && result.After.FeatureId != featureId)
            )
            {
                throw new InvalidDataException("The Windows image feature response does not match the selected identifier and state.");
            }
            return result;
        }
        catch (Exception ex)
        {
            throw new WindowsImageFeatureDispatchException("The dispatched Windows feature request returned an invalid result.", ex);
        }
    }

    public async Task StartTraceAsync(TraceScope scope, CancellationToken token) =>
        EnsureSuccess(await RequestAsync(new(1, 0, "StartTrace", Scope: scope), token));

    public async Task<TraceBatch> ReadTraceAsync(CancellationToken token)
    {
        var reply = await RequestAsync(new(1, 0, "ReadTrace"), token);
        EnsureSuccess(reply);
        return reply.Batch ?? throw new InvalidDataException("Missing trace response.");
    }

    public async Task StopTraceAsync(CancellationToken token) => EnsureSuccess(await RequestAsync(new(1, 0, "StopTrace"), token));

    private static void EnsureSuccess(HelperReply reply)
    {
        if (reply.ExitCode != 0)
        {
            throw new IOException(reply.Error ?? $"Helper operation failed ({reply.ExitCode}).");
        }
    }

    private async Task<HelperReply> RequestAsync(HelperRequest request, CancellationToken token)
    {
        await _serial.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_broken is not null)
            {
                throw new IOException("The helper is unavailable; elevation will not be retried.", _broken);
            }

            await (_connection ??= ConnectAsync(token));
            token.ThrowIfCancellationRequested();
            request = request with { Id = ++_requestId };
            var replyConsumed = false;
            var dispatchAttempted = false;
            try
            {
                // Once a repair is dispatched, drain it before observing cancellation.
                // Other operations are bounded too; a protocol failure permanently disables this connection.
                using var timeout = new CancellationTokenSource();
                if (request.Command != "Grant")
                {
                    timeout.CancelAfter(requestTimeout ?? TimeSpan.FromSeconds(30));
                }

                dispatchAttempted = true;
                await PipeProtocol.WriteAsync(_pipe!, request, timeout.Token);
                var reply = await PipeProtocol.ReadAsync<HelperReply>(_pipe!, timeout.Token);
                if (reply.Version != 1 || reply.Id != request.Id)
                {
                    throw new InvalidDataException("Unexpected helper reply.");
                }

                replyConsumed = true;
                // A trace read removes records from the helper queue. Always deliver its reply,
                // including during shutdown; cancellation must not silently discard that batch.
                if (request.Command == "Grant")
                {
                    token.ThrowIfCancellationRequested();
                }

                return reply;
            }
            catch (OperationCanceledException ex) when (!replyConsumed && request.Command != "Grant")
            {
                // Dispatched requests use only our internal deadline. Caller cancellation is
                // observed separately so a consumed trace batch is never discarded.
                var failure = new TimeoutException("The permission helper did not reply before the deadline.", ex);
                _broken = failure;
                _pipe?.Dispose();
                throw failure;
            }
            catch (Exception ex) when (!replyConsumed)
            {
                _broken = ex;
                _pipe?.Dispose();
                if (dispatchAttempted && request.Command is "DisableWindowsImageFeatureById" or "EnableWindowsImageFeatureById")
                {
                    throw new WindowsImageFeatureDispatchException(
                        "The Windows feature request was dispatched, but its result could not be received.",
                        ex
                    );
                }
                throw;
            }
        }
        finally
        {
            _serial.Release();
        }
    }

    private async Task ConnectAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var pipeName = "LockscreenGif-access-" + Guid.NewGuid().ToString("N");
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new(new SecurityIdentifier(sid), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(
            new(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                PipeAccessRights.ReadWrite,
                AccessControlType.Allow
            )
        );
        _pipe = NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            4096,
            4096,
            security
        );
        try
        {
            _process =
                (launch ?? Process.Start)(PermissionWorkerCommand.Create(root, sid, pipeName))
                ?? throw new IOException("The helper did not start.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new OperationCanceledException("The Windows permission request was cancelled.", ex);
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(connectionTimeout ?? TimeSpan.FromSeconds(30));
        var connected = _pipe.WaitForConnectionAsync(timeout.Token);
        if (await Task.WhenAny(connected, _process.WaitForExitAsync()) != connected)
        {
            timeout.Cancel();
            try
            {
                await connected;
            }
            catch (OperationCanceledException) { }
            throw new IOException("The helper exited before connecting.");
        }
        try
        {
            await connected;
        }
        catch (OperationCanceledException ex) when (!token.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            // An internal deadline is a failed helper connection, not a user cancellation.
            throw new TimeoutException("The permission helper did not connect before the deadline.", ex);
        }
        if (!GetNamedPipeClientProcessId(_pipe.SafePipeHandle, out var clientId) || clientId != _process.Id)
        {
            throw new IOException("Unexpected helper peer.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _serial.WaitAsync();
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _pipe?.Dispose();
            if (_process is not null)
            {
                try
                {
                    await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                }
                catch (TimeoutException)
                {
                    throw new IOException("The helper has not finished shutting down.");
                }
                finally
                {
                    _process.Dispose();
                }
            }
        }
        finally
        {
            _serial.Release();
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
}
