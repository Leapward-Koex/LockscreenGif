namespace Windows.Storage.Provider
{
    public enum FileUpdateStatus
    {
        Incomplete,
        Complete,
        UserInputNeeded,
        CurrentlyUnavailable,
        Failed,
        CompleteAndRenamed,
    }
}

namespace Windows.Storage
{
    public sealed class StorageProvider
    {
        public string Id { get; set; } = "synthetic-provider";
    }

    public sealed class StorageFile
    {
        public static List<StorageFile> Files { get; } = [];
        public object Identity { get; init; } = new();
        public string Path { get; init; } = "";
        public StorageProvider? Provider { get; set; } = new();
        public PickedFileWriter.Tests.TrackingStream Stream { get; }
        public Exception? OpenError { get; set; }
        public bool Opened { get; private set; }

        public StorageFile(byte[] bytes)
        {
            Stream = new(bytes);
            Files.Add(this);
        }

        public bool IsEqual(StorageFile other) => ReferenceEquals(Identity, other.Identity);

        public Task<Stream> OpenStreamForReadAsync() => OpenAsync();

        public Task<Stream> OpenStreamForWriteAsync() => OpenAsync();

        public Task CopyAndReplaceAsync(StorageFile other) =>
            throw new InvalidOperationException("Replacement loses the picker-owned destination identity.");

        private Task<Stream> OpenAsync()
        {
            if (OpenError is not null)
            {
                return Task.FromException<Stream>(OpenError);
            }
            Opened = true;
            return Task.FromResult<Stream>(Stream);
        }
    }

    public static class CachedFileManager
    {
        public static StorageFile? DeferredFile { get; private set; }
        public static Exception? DeferError { get; set; }
        public static Exception? CompletionError { get; set; }
        public static Provider.FileUpdateStatus Status { get; set; }
        public static int CompletionCalls { get; private set; }

        public static void Reset()
        {
            DeferredFile = null;
            DeferError = null;
            CompletionError = null;
            Status = Provider.FileUpdateStatus.Complete;
            CompletionCalls = 0;
            StorageFile.Files.Clear();
            Logger.Errors.Clear();
        }

        public static void DeferUpdates(StorageFile file)
        {
            if (DeferError is not null)
            {
                throw DeferError;
            }
            DeferredFile = file;
        }

        public static Task<Provider.FileUpdateStatus> CompleteUpdatesAsync(StorageFile file)
        {
            CompletionCalls++;
            if (!ReferenceEquals(file, DeferredFile))
            {
                throw new InvalidOperationException("Completion must use the same picker-owned file.");
            }
            if (StorageFile.Files.Any(item => item.Opened && !item.Stream.Disposed))
            {
                throw new InvalidOperationException("Streams must be closed before completion.");
            }
            return CompletionError is null ? Task.FromResult(Status) : Task.FromException<Provider.FileUpdateStatus>(CompletionError);
        }
    }
}

namespace PickedFileWriter.Tests
{
    public sealed class TrackingStream : MemoryStream
    {
        public bool Disposed { get; private set; }
        public bool Flushed { get; private set; }
        public Exception? WriteError { get; set; }
        public Exception? FlushError { get; set; }

        public TrackingStream(byte[] bytes)
        {
            Write(bytes);
            Position = 0;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            WriteError is null ? base.WriteAsync(buffer, cancellationToken) : ValueTask.FromException(WriteError);

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            if (FlushError is not null)
            {
                return Task.FromException(FlushError);
            }
            Flushed = true;
            return base.FlushAsync(cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}

internal static class Logger
{
    public static List<Exception> Errors { get; } = [];

    public static void Error(string message, Exception error) => Errors.Add(error);
}
