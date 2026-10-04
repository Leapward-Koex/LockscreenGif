using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace LockscreenGif.Services;

internal sealed partial class FfmpegHardwareDecodingProbe : IHardwareDecodingProbe
{
    private readonly string _baseDirectory;

    public FfmpegHardwareDecodingProbe(string? baseDirectory = null) => _baseDirectory = baseDirectory ?? AppContext.BaseDirectory;

    public async Task<bool> IsSupportedAsync(int adapterIndex, string sampleName, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var input = Path.Combine(_baseDirectory, "Assets", "VideoDecoding", sampleName);
        if (!File.Exists(input))
        {
            throw new FileNotFoundException("Hardware decoding probe sample is missing.", input);
        }
        var start = new ProcessStartInfo(Path.Combine(_baseDirectory, "Vendor", "FFMPEG", "ffmpeg.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in CreateArguments(adapterIndex, input))
        {
            start.ArgumentList.Add(argument);
        }
        using var process = new Process { StartInfo = start };
        process.Start();
        using var cancellation = token.Register(() => Stop(process));
        var standardOutput = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
        var hardwareFrames = 0;
        var otherFrames = 0;
        try
        {
            while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                var format = ReadFrameFormat(line);
                if (format == "d3d11")
                {
                    hardwareFrames++;
                }
                else if (format != null)
                {
                    otherFrames++;
                }
            }
            await process.WaitForExitAsync().ConfigureAwait(false);
            await standardOutput.ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return IsVerifiedOutput(process.ExitCode, hardwareFrames, otherFrames);
        }
        finally
        {
            Stop(process);
            await process.WaitForExitAsync().ConfigureAwait(false);
            await standardOutput.ConfigureAwait(false);
        }
    }

    internal static bool IsVerifiedOutput(int exitCode, int hardwareFrames, int otherFrames) =>
        exitCode == 0 && hardwareFrames == 3 && otherFrames == 0;

    internal static string? ReadFrameFormat(string line)
    {
        var match = FrameFormatPattern().Match(line);
        return match.Success ? match.Groups[1].Value : null;
    }

    internal static string[] CreateArguments(int adapterIndex, string input) =>
        [
            "-hide_banner",
            "-nostdin",
            "-nostats",
            "-threads",
            "2",
            "-filter_threads",
            "1",
            "-noautorotate",
            "-xerror",
            "-hwaccel",
            "d3d11va",
            "-hwaccel_device",
            adapterIndex.ToString(CultureInfo.InvariantCulture),
            "-hwaccel_output_format",
            "d3d11",
            "-i",
            input,
            "-map",
            "0:v:0",
            "-vf",
            "showinfo=checksum=0",
            "-an",
            "-sn",
            "-dn",
            "-fps_mode",
            "passthrough",
            "-f",
            "null",
            "-threads",
            "1",
            "-",
        ];

    private static void Stop(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
    }

    [GeneratedRegex(@"\bn:\s*\d+\s+pts:.*?\bfmt:([^\s]+)", RegexOptions.CultureInvariant)]
    private static partial Regex FrameFormatPattern();
}
