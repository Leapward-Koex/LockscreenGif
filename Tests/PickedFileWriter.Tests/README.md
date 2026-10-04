# Picked-file save checks

Run from the repository root with the .NET 10 SDK:

```powershell
dotnet run --project Tests/PickedFileWriter.Tests/PickedFileWriter.Tests.csproj -c Release
```

This harness links production `PickedFileWriter` to in-memory storage/provider doubles. It checks byte-exact copying, truncation, destination identity, same-file protection, flush/close ordering and provider completion. Exact `computer`/`local` provider IDs bypass updates; unknown/cloud providers still require a successful completion status. Open/write/flush errors and synthetic cancellation propagate, and secondary completion errors do not replace the original write failure.

The suite does not open the real picker, test picker cancellation or synchronize a real provider. Manually save a generated GIF to a new local file, overwrite a longer GIF and check a provider destination where available. Confirm usable bytes, accurate feedback and preserved output dimensions. Copying is not an atomic overwrite guarantee. See [saving generated output](../../docs/video-editing.md#saving-generated-output).
