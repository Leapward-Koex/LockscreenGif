namespace LockscreenGif.Services.Diagnostics;

public sealed record LockscreenVerificationResult(bool ReadConfirmed, bool UnlockObserved, string Title, string Message);
