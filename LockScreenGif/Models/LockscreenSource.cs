namespace LockscreenGif.Models;

public enum LockscreenSourceKind
{
    Unknown,
    Video,
    UserGif,
    BundledGif,
}

// A local snapshot of the selected file and its origin. Only Kind may enter analytics.
public sealed record LockscreenSource(string Path, LockscreenSourceKind Kind);
