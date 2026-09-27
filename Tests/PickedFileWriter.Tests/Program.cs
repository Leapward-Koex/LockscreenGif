using System.Runtime.InteropServices;
using Windows.Storage;
using Windows.Storage.Provider;
using ExportWriter = LockscreenGif.Services.PickedFileWriter;

namespace PickedFileWriter.Tests;

internal static class Program
{
    private static int _checks;
    private static readonly byte[] GifBytes = [71, 73, 70, 56, 57, 97, 0, 255, 128, 59];

    private static async Task Main()
    {
        await SuccessfulCopyAsync();
        await LocalFilesAsync();
        await UnknownAndRemoteProvidersAsync();
        await SameFileAsync();
        await ProviderFailuresAsync();
        await WriteFailuresAsync();
        await DeferredUpdateFailureAsync();
        Console.WriteLine($"All {_checks} picked-file save checks passed. No native APIs, user files, or analytics transports were used.");
    }

    private static async Task SuccessfulCopyAsync()
    {
        foreach (var status in new[] { FileUpdateStatus.Complete, FileUpdateStatus.CompleteAndRenamed })
        {
            var (source, destination) = Setup();
            var identity = destination.Identity;
            CachedFileManager.Status = status;
            await ExportWriter.CopyAsync(source, destination);
            Check(destination.Stream.ToArray().SequenceEqual(GifBytes), "overwrite preserves all GIF bytes and removes the old file tail");
            Check(source.Stream.ToArray().SequenceEqual(GifBytes), "saving leaves generated source bytes unchanged");
            Check(ReferenceEquals(destination.Identity, identity), "save preserves picker destination identity");
            Check(
                destination.Stream.Flushed && destination.Stream.Disposed && source.Stream.Disposed,
                "flush and close before provider completion"
            );
            Check(CachedFileManager.CompletionCalls == 1 && Logger.Errors.Count == 0, status + " is a successful completed save");
            Check(source.Path.Length == 0 && destination.Path.Length == 0, "distinct provider files with empty paths are still copied");
        }
    }

    private static async Task LocalFilesAsync()
    {
        foreach (var providerId in new[] { "computer", "Computer", "local", "Local" })
        {
            var (source, destination) = Setup();
            destination.Provider!.Id = providerId;
            CachedFileManager.DeferError = new COMException("No provider app for a local file", unchecked((int)0x80070490));
            await ExportWriter.CopyAsync(source, destination);
            Check(destination.Stream.ToArray().SequenceEqual(GifBytes), providerId + " saves byte-exact data and truncates the old tail");
            Check(
                destination.Stream.Flushed && destination.Stream.Disposed && source.Stream.Disposed,
                providerId + " flushes and closes its streams"
            );
            Check(
                CachedFileManager.DeferredFile is null && CachedFileManager.CompletionCalls == 0,
                providerId + " never requests a provider-app update"
            );
        }

        foreach (var stage in new[] { "open", "write", "flush" })
        {
            var (source, destination) = Setup();
            destination.Provider!.Id = "computer";
            var failure = new IOException("Synthetic local " + stage + " failure");
            if (stage == "open")
            {
                destination.OpenError = failure;
            }
            else if (stage == "write")
            {
                destination.Stream.WriteError = failure;
            }
            else
            {
                destination.Stream.FlushError = failure;
            }
            var observed = await CaptureAsync(() => ExportWriter.CopyAsync(source, destination));
            Check(ReferenceEquals(observed, failure), "local " + stage + " errors still fail saving");
            Check(
                StorageFile.Files.All(item => !item.Opened || item.Stream.Disposed),
                "local " + stage + " errors close every opened stream"
            );
        }
    }

    private static async Task UnknownAndRemoteProvidersAsync()
    {
        foreach (var providerId in new[] { "OneDrive", "Network", "computer-other", "local-other", "", null })
        {
            var (source, destination) = Setup();
            destination.Provider = providerId is null ? null : new StorageProvider { Id = providerId };
            var failure = new COMException("Synthetic provider completion failure", unchecked((int)0x80070490));
            CachedFileManager.CompletionError = failure;
            var observed = await CaptureAsync(() => ExportWriter.CopyAsync(source, destination));
            Check(
                ReferenceEquals(observed, failure),
                "provider " + (providerId ?? "missing") + " errors cannot be mistaken for local success"
            );
            Check(
                ReferenceEquals(CachedFileManager.DeferredFile, destination) && CachedFileManager.CompletionCalls == 1,
                "unknown and remote providers retain the update protocol"
            );
        }
    }

    private static async Task SameFileAsync()
    {
        CachedFileManager.Reset();
        var source = new StorageFile(GifBytes);
        var destination = new StorageFile(GifBytes) { Identity = source.Identity };
        await ExportWriter.CopyAsync(source, destination);
        Check(!source.Opened && !destination.Opened, "saving onto the same storage item cannot truncate its source");
        Check(CachedFileManager.DeferredFile is null && CachedFileManager.CompletionCalls == 0, "same-file save needs no provider update");
    }

    private static async Task ProviderFailuresAsync()
    {
        foreach (
            var status in new[]
            {
                FileUpdateStatus.Incomplete,
                FileUpdateStatus.UserInputNeeded,
                FileUpdateStatus.CurrentlyUnavailable,
                FileUpdateStatus.Failed,
            }
        )
        {
            var (source, destination) = Setup();
            CachedFileManager.Status = status;
            var error = await CaptureAsync(() => ExportWriter.CopyAsync(source, destination));
            Check(
                error is IOException && error.Message.Contains(status.ToString()),
                status + " cannot report success even after writing bytes"
            );
            Check(
                destination.Stream.ToArray().SequenceEqual(GifBytes) && CachedFileManager.CompletionCalls == 1,
                "provider failure is distinct from a copy failure"
            );
        }

        var (comSource, comDestination) = Setup();
        var comError = new COMException("Synthetic provider error", unchecked((int)0x80070490));
        CachedFileManager.CompletionError = comError;
        var observed = await CaptureAsync(() => ExportWriter.CopyAsync(comSource, comDestination));
        Check(ReferenceEquals(observed, comError), "real completion COM errors are propagated rather than ignored by HRESULT");
    }

    private static async Task WriteFailuresAsync()
    {
        foreach (var stage in new[] { "source open", "destination open", "copy", "flush", "cancellation" })
        {
            foreach (var completionFails in new[] { false, true })
            {
                var (source, destination) = Setup();
                Exception failure =
                    stage == "cancellation"
                        ? new OperationCanceledException("Synthetic cancellation")
                        : new IOException("Synthetic " + stage + " failure");
                switch (stage)
                {
                    case "source open":
                        source.OpenError = failure;
                        break;
                    case "destination open":
                        destination.OpenError = failure;
                        break;
                    case "flush":
                        destination.Stream.FlushError = failure;
                        break;
                    default:
                        destination.Stream.WriteError = failure;
                        break;
                }
                if (completionFails)
                {
                    CachedFileManager.CompletionError = new COMException("Synthetic cleanup failure");
                }
                var observed = await CaptureAsync(() => ExportWriter.CopyAsync(source, destination));
                Check(ReferenceEquals(observed, failure), stage + " keeps its original exception when completing updates");
                Check(CachedFileManager.CompletionCalls == 1, stage + " still releases deferred updates");
                Check(StorageFile.Files.All(item => !item.Opened || item.Stream.Disposed), stage + " closes every opened stream");
                Check(
                    Logger.Errors.Count == (completionFails ? 1 : 0),
                    stage + " logs secondary completion failure without masking the write failure"
                );
            }
        }
    }

    private static async Task DeferredUpdateFailureAsync()
    {
        var (source, destination) = Setup();
        var failure = new COMException("Synthetic defer failure");
        CachedFileManager.DeferError = failure;
        var observed = await CaptureAsync(() => ExportWriter.CopyAsync(source, destination));
        Check(ReferenceEquals(observed, failure), "failure to defer remains a failed save");
        Check(
            !source.Opened && !destination.Opened && CachedFileManager.CompletionCalls == 0,
            "failed deferral neither writes nor completes an unstarted update"
        );
    }

    private static (StorageFile Source, StorageFile Destination) Setup()
    {
        CachedFileManager.Reset();
        return (new StorageFile(GifBytes), new StorageFile(Enumerable.Repeat((byte)42, 100).ToArray()));
    }

    private static async Task<Exception> CaptureAsync(Func<Task> operation)
    {
        try
        {
            await operation();
        }
        catch (Exception error)
        {
            return error;
        }
        throw new InvalidOperationException("Expected the save to fail.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("FAILED: " + message);
        }
        _checks++;
        Console.WriteLine("PASS " + message);
    }
}
