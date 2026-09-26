namespace LockscreenGif.Services.Analytics;

public sealed class AnalyticsOptions
{
    public string ProjectToken { get; set; } = "";

    public string Host { get; set; } = "https://eu.i.posthog.com";

    public AnalyticsEnvironment Environment { get; set; } = AnalyticsEnvironment.Development;

    public static AnalyticsOptions ForBuild(
        bool isGitHubActionsBuild,
        string productionProjectToken,
        string developmentProjectToken,
        string host
    ) =>
        new()
        {
            Host = host,
            ProjectToken = isGitHubActionsBuild ? productionProjectToken : developmentProjectToken,
            Environment = isGitHubActionsBuild ? AnalyticsEnvironment.Production : AnalyticsEnvironment.Development,
        };
}

public enum AnalyticsEnvironment
{
    Development,
    Production,
}
