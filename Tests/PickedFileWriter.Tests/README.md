# Picked-file save regression checks

Run `dotnet run --project Tests/PickedFileWriter.Tests/PickedFileWriter.Tests.csproj -c Release`.

This isolated harness links the production `PickedFileWriter` and substitutes in-memory storage files and cached-file update APIs. It checks destination identity, complete byte copying, truncation, flush/close ordering, same-file protection, empty provider paths, exact local-provider recognition, unknown/cloud-provider statuses and COM errors, cancellation, and preservation of the primary failure when update cleanup also fails. Plain local files bypass provider-app updates while real local write failures still propagate.

It does not exercise the real Windows picker or provider synchronization. Manually save a generated GIF to a new local file and over an existing longer GIF, then check a provider-backed destination where available. Confirm a usable saved GIF, the success message, and unchanged generated-output resolution.
