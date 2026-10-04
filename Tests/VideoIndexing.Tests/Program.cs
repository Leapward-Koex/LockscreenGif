await HardwareIndexingTests.RunAsync();
await IndexProgressTests.RunAsync();
if (args.Contains("--hardware"))
{
    await HardwareIntegrationTests.RunAsync(args);
}
