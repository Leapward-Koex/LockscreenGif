using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using LockscreenGif.Services.Lockscreen;

internal static class PermissionTransportTests
{
    // This is an unelevated echo peer, not the production permission worker.
    // It never invokes access tools, changes files, or changes the lock screen.
    private const string EchoPeer = """
        param([string]$PipeName, [bool]$ExitAfterReply)
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
                $response = @{Version=1; Id=$request.Id; ExitCode=$(if ($request.Write) {0} else {5})}
                if ($request.Command -eq 'ReadTrace') {
                    Start-Sleep -Milliseconds 150
                    $response.ExitCode = 0
                    $response.Batch = @{Evidence=@{State='Completed'}; Operations=@(); HasMore=$false}
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

    private static Process LaunchEcho(ProcessStartInfo production, bool exitAfterReply)
    {
        var pipeName = production.ArgumentList[0];
        PermissionSessionTests.Check(pipeName.StartsWith("LockscreenGif-access-"), "Missing generated pipe name.");
        var command = "& {\n" + EchoPeer + "\n} -PipeName '" + pipeName + "' -ExitAfterReply $" + (exitAfterReply ? "true" : "false");
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
