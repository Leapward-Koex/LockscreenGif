using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using LockscreenGif.Services.Lockscreen;

internal static class PermissionTransportTests
{
    // This is an unelevated echo peer, not the production permission worker.
    // It never invokes access tools, changes files, or changes the lock screen.
    private const string EchoPeer = """
        param([string]$PipeName, [bool]$ExitAfterReply, [bool]$DropFeatureReply, [bool]$MismatchFeatureReply, [bool]$LegacyFeaturePeer)
        $ErrorActionPreference = 'Stop'
        $pipe = New-Object IO.Pipes.NamedPipeClientStream('.', $PipeName, [IO.Pipes.PipeDirection]::InOut)
        try {
            $pipe.Connect(10000)
            function Read-Bytes([int]$length) {
                $bytes = New-Object byte[] $length
                $offset = 0
                while ($offset -lt $length) {
                    $count = $pipe.Read($bytes, $offset, $length-$offset)
                    if ($count -eq 0) { throw 'EOF' }
                    $offset += $count
                }
                return ,$bytes
            }
            while ($true) {
                $header = Read-Bytes 4
                $length = [BitConverter]::ToInt32($header,0)
                $request = [Text.Encoding]::UTF8.GetString((Read-Bytes $length)) | ConvertFrom-Json
                if ($DropFeatureReply -and $request.Command -in @('DisableWindowsImageFeatureById','EnableWindowsImageFeatureById')) { break }
                $response = @{Version=1; Id=$request.Id; ExitCode=$(if ($request.Write) {0} else {5})}
                if ($LegacyFeaturePeer) {
                    if ($request.Command -in @('DisableWindowsImageFeature','EnableWindowsImageFeature')) { throw 'A selected-ID request matched a legacy mutating command.' }
                    $response.ExitCode = 87
                    $response.Error = 'Unknown helper operation.'
                }
                if ($request.Command -eq 'ReadTrace') {
                    Start-Sleep -Milliseconds 150
                    $response.ExitCode = 0
                    $response.Batch = @{Evidence=@{State='Completed'}; Operations=@(); HasMore=$false}
                }
                if (-not $LegacyFeaturePeer -and $request.Command -in @('DisableWindowsImageFeatureById','EnableWindowsImageFeatureById')) {
                    if ($null -ne $request.Path -or $null -ne $request.Scope -or $request.Write -or $null -eq $request.FeatureId -or $request.FeatureId -le 0) { throw 'Feature request must have numeric selected scope.' }
                    Start-Sleep -Milliseconds 150
                    $response.ExitCode = 0
                    $desired = if ($request.Command -eq 'EnableWindowsImageFeatureById') { 2 } else { 1 }
                    $stateName = if ($desired -eq 2) { 'Enabled' } else { 'Disabled' }
                    $replyId = if ($MismatchFeatureReply) { [uint32]38943831 } else { [uint32]$request.FeatureId }
                    $response.WindowsImageFeature = @{FeatureId=$request.FeatureId; DesiredState=$stateName; Outcome=$stateName; ChangeAttempted=$true; Changed=$true; RuntimeChanged=$true; NativeSetStatus=0; Before=@{FeatureId=$request.FeatureId; QueryStatus=0; RuntimeState=(3-$desired)}; After=@{FeatureId=$replyId; QueryStatus=0; RuntimeState=$desired; OverrideExists=$true; OverrideState=$desired; OverrideOptions=0}}
                }
                $reply = $response | ConvertTo-Json -Compress -Depth 5
                $bytes = [Text.Encoding]::UTF8.GetBytes($reply)
                $pipe.Write([BitConverter]::GetBytes($bytes.Length),0,4)
                $pipe.Write($bytes,0,$bytes.Length)
                $pipe.Flush()
                if ($ExitAfterReply) { break }
            }
        } finally { $pipe.Dispose() }
        """;

    public static async Task ReusesOneProcess()
    {
        using var fixture = new CacheFixture();
        var launched = 0;
        var processId = 0;
        var session = new CachePermissionSession(
            fixture.Root,
            PermissionSessionTests.CurrentSid(),
            info =>
            {
                launched++;
                var process = LaunchEcho(info, false);
                processId = process.Id;
                return process;
            }
        );
        try
        {
            var results = new List<int>();
            foreach (var write in new[] { true, false, true })
            {
                results.Add(await session.GrantAsync(fixture.MainImage, write, default).WaitAsync(TimeSpan.FromSeconds(15)));
            }

            PermissionSessionTests.Check(
                launched == 1 && results.SequenceEqual(new[] { 0, 5, 0 }),
                "Many path repairs, including failures, must share one helper process."
            );
        }
        finally
        {
            await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));
        }
        try
        {
            using var process = Process.GetProcessById(processId);
            PermissionSessionTests.Check(process.HasExited, "The helper must exit when the operation finishes.");
        }
        catch (ArgumentException) { } // Process already exited and was reaped.
    }

    public static async Task DisconnectedHelperDoesNotRelaunch()
    {
        using var fixture = new CacheFixture();
        var launched = 0;
        await using var session = new CachePermissionSession(
            fixture.Root,
            PermissionSessionTests.CurrentSid(),
            info =>
            {
                launched++;
                return LaunchEcho(info, true);
            }
        );
        await session.GrantAsync(fixture.MainImage, true, default).WaitAsync(TimeSpan.FromSeconds(15));
        try
        {
            await session.GrantAsync(fixture.MainImage, true, default).WaitAsync(TimeSpan.FromSeconds(15));
            throw new Exception("Unexpected helper response.");
        }
        catch (IOException) { }
        PermissionSessionTests.Check(launched == 1, "A disconnected helper must not cause another prompt.");
    }

    public static async Task TraceFailureAndCancelledRead()
    {
        using var fixture = new CacheFixture();
        var launched = 0;
        await using var session = new CachePermissionSession(
            fixture.Root,
            PermissionSessionTests.CurrentSid(),
            info =>
            {
                launched++;
                return LaunchEcho(info, false);
            }
        );
        try
        {
            await session.StartTraceAsync(new(fixture.Root, fixture.MainImage), default);
            throw new Exception("Expected trace failure.");
        }
        catch (IOException) { }
        var granted = await session.GrantAsync(fixture.MainImage, true, default);
        PermissionSessionTests.Check(granted == 0 && launched == 1, "Trace startup failure must preserve usable helper permissions.");
        using var token = new CancellationTokenSource(50);
        var batch = await session.ReadTraceAsync(token.Token);
        PermissionSessionTests.Check(
            token.IsCancellationRequested && batch.Evidence.State == "Completed",
            "Cancellation after dispatch must deliver a consumed trace batch."
        );
    }

    public static async Task ConnectionDeadlineIsFailure()
    {
        using var fixture = new CacheFixture();
        var launches = 0;
        Process? peer = null;
        var session = new CachePermissionSession(
            fixture.Root,
            PermissionSessionTests.CurrentSid(),
            _ =>
            {
                launches++;
                return peer = LaunchUnconnectedPeer();
            },
            connectionTimeout: TimeSpan.FromMilliseconds(50)
        );
        TimeoutException? first = null;
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    await session.GrantAsync(fixture.MainImage, true, default).WaitAsync(TimeSpan.FromSeconds(5));
                    throw new Exception("The unconnected helper unexpectedly granted access.");
                }
                catch (TimeoutException ex) when (ex.InnerException is OperationCanceledException)
                {
                    first ??= ex;
                    PermissionSessionTests.Check(
                        ReferenceEquals(first, ex),
                        "A failed connection retains the original timeout without relaunching."
                    );
                }
            }
            PermissionSessionTests.Check(
                launches == 1 && first is not null,
                "The helper deadline is a real failure and elevation is not repeated."
            );
        }
        finally
        {
            await StopUnconnectedPeerAsync(peer, session);
        }
    }

    public static async Task ConnectionCancellation()
    {
        using var fixture = new CacheFixture();
        using var cancellation = new CancellationTokenSource();
        Process? peer = null;
        var session = new CachePermissionSession(
            fixture.Root,
            PermissionSessionTests.CurrentSid(),
            _ =>
            {
                peer = LaunchUnconnectedPeer();
                cancellation.CancelAfter(TimeSpan.FromMilliseconds(50));
                return peer;
            },
            connectionTimeout: TimeSpan.FromSeconds(5)
        );
        try
        {
            try
            {
                await session.GrantAsync(fixture.MainImage, true, cancellation.Token).WaitAsync(TimeSpan.FromSeconds(5));
                throw new Exception("Caller cancellation was ignored.");
            }
            catch (OperationCanceledException)
            {
                PermissionSessionTests.Check(
                    cancellation.IsCancellationRequested,
                    "Caller cancellation stays distinct from the internal deadline."
                );
            }
        }
        finally
        {
            await StopUnconnectedPeerAsync(peer, session);
        }
    }

    public static async Task RequestDeadlineIsFailure()
    {
        using var fixture = new CacheFixture();
        var launches = 0;
        await using var session = new CachePermissionSession(
            fixture.Root,
            PermissionSessionTests.CurrentSid(),
            info =>
            {
                launches++;
                return LaunchEcho(info, false);
            },
            requestTimeout: TimeSpan.FromMilliseconds(50)
        );
        // Grants have no request deadline and establish the authenticated connection first.
        PermissionSessionTests.Check(await session.GrantAsync(fixture.MainImage, true, default) == 0, "The connection is ready.");
        TimeoutException? failure = null;
        try
        {
            // The echo peer delays trace replies for 150 ms, exceeding the 50 ms test deadline.
            await session.ReadTraceAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
            throw new Exception("The slow trace reply unexpectedly completed.");
        }
        catch (TimeoutException ex) when (ex.InnerException is OperationCanceledException)
        {
            failure = ex;
        }
        try
        {
            await session.GrantAsync(fixture.MainImage, true, default);
            throw new Exception("A timed-out transport unexpectedly remained usable.");
        }
        catch (IOException ex) when (ReferenceEquals(ex.InnerException, failure)) { }
        PermissionSessionTests.Check(
            failure is not null && launches == 1,
            "A transport deadline stays a failure and never starts another helper."
        );
    }

    private static Process LaunchUnconnectedPeer()
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30" })
        {
            info.ArgumentList.Add(argument);
        }
        return Process.Start(info) ?? throw new Exception("Could not start the unelevated unconnected test peer.");
    }

    private static async Task StopUnconnectedPeerAsync(Process? peer, CachePermissionSession session)
    {
        if (peer is not null && !peer.HasExited)
        {
            peer.Kill();
            await peer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        await session.DisposeAsync();
    }

    public static async Task FeatureRepairReusesConnection()
    {
        using var fixture = new CacheFixture();
        var launched = 0;
        await using var session = new CachePermissionSession(
            fixture.Root,
            PermissionSessionTests.CurrentSid(),
            info =>
            {
                launched++;
                return LaunchEcho(info, false);
            }
        );
        await session.GrantAsync(fixture.MainImage, true, default);
        using var token = new CancellationTokenSource(50);
        var result = await session.DisableWindowsImageFeatureAsync(token.Token);
        var enabled = await session.EnableWindowsImageFeatureAsync(default);
        const uint selectedId = 61653826;
        var custom = await session.DisableWindowsImageFeatureAsync(default, selectedId);
        var customEnabled = await session.EnableWindowsImageFeatureAsync(default, selectedId);
        var exitCode = await session.GrantAsync(fixture.MainImage, true, default);
        PermissionSessionTests.Check(
            launched == 1
                && exitCode == 0
                && token.IsCancellationRequested
                && result.Changed
                && result.After?.RuntimeState == 1
                && result.After?.OverrideState == 1
                && enabled.DesiredState == "Enabled"
                && enabled.After?.RuntimeState == 2
                && enabled.After?.OverrideState == 2
                && custom.FeatureId == selectedId
                && custom.Before?.FeatureId == selectedId
                && custom.After?.FeatureId == selectedId
                && customEnabled.FeatureId == selectedId
                && customEnabled.After?.RuntimeState == 2,
            "A dispatched feature repair must drain its structured result despite cancellation and share the permission connection."
        );
    }

    public static async Task DisconnectedFeatureRetainsUncertainty()
    {
        using var fixture = new CacheFixture();
        var launched = 0;
        await using var session = new CachePermissionSession(
            fixture.Root,
            PermissionSessionTests.CurrentSid(),
            info =>
            {
                launched++;
                return LaunchEcho(info, false, true);
            }
        );
        var before = new LockscreenGif.Privileged.WindowsImageFeatureState
        {
            QueryStatus = 0,
            RuntimeState = 2,
            RuntimePriority = 0,
            OverrideExists = false,
        };
        var result = await new WindowsImageFeatureService(() => session, () => false, _ => before).SetEnabledAsync(false);
        PermissionSessionTests.Check(
            result.Outcome == "Failed"
                && result.ChangeAttempted
                && result.ChangeOutcomeUnknown
                && result.After is null
                && result.Before == before
                && launched == 1,
            "A disconnected feature response cannot establish whether the mutation completed and must retain that uncertainty."
        );
    }

    public static async Task FeatureScopeValidation()
    {
        using var fixture = new CacheFixture();
        var launched = 0;
        await using var session = new CachePermissionSession(
            fixture.Root,
            PermissionSessionTests.CurrentSid(),
            info =>
            {
                launched++;
                return LaunchEcho(info, false, mismatchFeatureReply: true);
            }
        );
        try
        {
            await session.DisableWindowsImageFeatureAsync(default, 0);
            throw new Exception("Expected zero feature identifier refusal.");
        }
        catch (ArgumentOutOfRangeException) { }
        PermissionSessionTests.Check(launched == 0, "An invalid selected feature ID cannot launch a helper.");
        try
        {
            await session.DisableWindowsImageFeatureAsync(default, 61653826);
            throw new Exception("Expected mismatched feature readback refusal.");
        }
        catch (LockscreenGif.Privileged.WindowsImageFeatureDispatchException) { }
        PermissionSessionTests.Check(launched == 1, "Readback for another ID must retain uncertainty after the selected request.");
        await using var legacySession = new CachePermissionSession(
            fixture.Root,
            PermissionSessionTests.CurrentSid(),
            info => LaunchEcho(info, false, legacyFeaturePeer: true)
        );
        try
        {
            await legacySession.DisableWindowsImageFeatureAsync(default, 61653826);
            throw new Exception("Expected an older fixed-ID helper to reject the selected-ID command.");
        }
        catch (LockscreenGif.Privileged.WindowsImageFeatureDispatchException ex)
        {
            PermissionSessionTests.Check(
                ex.InnerException?.Message == "Unknown helper operation.",
                "Older helpers must reject the new command before reaching their fixed-ID mutation handler."
            );
        }
    }

    private static Process LaunchEcho(
        ProcessStartInfo production,
        bool exitAfterReply,
        bool dropFeatureReply = false,
        bool mismatchFeatureReply = false,
        bool legacyFeaturePeer = false
    )
    {
        var pipeName = production.ArgumentList[0];
        PermissionSessionTests.Check(pipeName.StartsWith("LockscreenGif-access-"), "Missing generated pipe name.");
        var command =
            "& {\n"
            + EchoPeer
            + "\n} -PipeName '"
            + pipeName
            + "' -ExitAfterReply $"
            + (exitAfterReply ? "true" : "false")
            + " -DropFeatureReply $"
            + (dropFeatureReply ? "true" : "false")
            + " -MismatchFeatureReply $"
            + (mismatchFeatureReply ? "true" : "false")
            + " -LegacyFeaturePeer $"
            + (legacyFeaturePeer ? "true" : "false");
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (
            var argument in new[]
            {
                "-NoLogo",
                "-NoProfile",
                "-NonInteractive",
                "-EncodedCommand",
                Convert.ToBase64String(Encoding.Unicode.GetBytes(command)),
            }
        )
        {
            info.ArgumentList.Add(argument);
        }

        return Process.Start(info) ?? throw new Exception("Could not start the unelevated test peer.");
    }
}
