using LockscreenGif.Services.Analytics;

namespace LockscreenGif.Contracts.Services;

/// <summary>Reports an actual failure without retaining diagnostic evidence or user data.</summary>
public interface IErrorReporter
{
    void CaptureException(Exception exception, AnalyticsErrorContext context, AnalyticsWorkflow workflow);
}
