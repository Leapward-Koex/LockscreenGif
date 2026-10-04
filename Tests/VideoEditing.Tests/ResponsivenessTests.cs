using LockscreenGif.Models;
using LockscreenGif.Services;

internal static class ResponsivenessTests
{
    public static async Task RunAsync(string video, VideoFrameIndex index)
    {
        // Model a UI synchronization context. Background services must not post
        // their continuations to it, even when called directly from that context.
        var context = new RecordingContext();
        var previous = SynchronizationContext.Current;
        Task<VideoFrameIndex> indexing;
        Task<IReadOnlyList<byte[]>> preview;
        Task<IReadOnlyList<byte[]>> indexedPreview;
        Task<IReadOnlyList<byte[]>> thumbnails;
        Task<(string Directory, double[] Timestamps)> export;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            indexing = VideoFrameService.IndexAsync(video, 30000d / 1001, null, CancellationToken.None);
            preview = VideoFrameService.PreviewWindowAsync(video, 0, 2, CancellationToken.None);
            indexedPreview = VideoFrameService.PreviewWindowAsync(video, index, 0, 2, CancellationToken.None);
            thumbnails = VideoFrameService.ThumbnailsAsync(video, index, CancellationToken.None);
            export = VideoFrameService.ExportAsync(video, index, 0, 2, 64, 0, _ => { });
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        await Task.WhenAll(indexing, preview, indexedPreview, thumbnails, export).WaitAsync(TimeSpan.FromSeconds(30));
        if (context.Posts != 0)
        {
            throw new InvalidOperationException($"Video processing posted {context.Posts} continuations onto the UI context.");
        }

        Console.WriteLine("PASS indexing, ordinal/indexed previews, thumbnail and export processing do not resume on the UI context");
    }

    private sealed class RecordingContext : SynchronizationContext
    {
        private int _posts;
        public int Posts => Volatile.Read(ref _posts);

        public override void Post(SendOrPostCallback callback, object? state)
        {
            Interlocked.Increment(ref _posts);
            ThreadPool.QueueUserWorkItem(_ => callback(state));
        }
    }
}
