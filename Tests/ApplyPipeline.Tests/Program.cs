using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using LockscreenGif.Models;
using LockscreenGif.Services.Lockscreen;

var tests = new (string Name, Func<Task> Run)[]
{
    ("The Windows API preference defaults off and persists both directions", LockscreenPreferencesTests.Persistence),
    ("Invalid Windows API preferences safely default off", LockscreenPreferencesTests.InvalidSettings),
    ("Failed Windows API saves retain the session value and support retry", LockscreenPreferencesTests.SaveFailure),
    ("Shared preference changes notify all current consumers", LockscreenPreferencesTests.SharedNotifications),
    ("All destinations are copied and hash verified", CompleteApply),
    ("Empty cache is a failed apply", EmptyCache),
    ("A locked destination produces a partial failure", PartialFailure),
    ("Committed hash mismatches retain a safe operation failure", CommittedHashMismatch),
    ("Existing read-only files do not require elevation", ReadOnlyFile),
    ("Cancellation before apply makes no changes", CancelledBeforeApply),
    ("Cancellation at a copy boundary preserves remaining destinations", CancelBetweenCopies),
    ("Cancellation before commit preserves the previous image", CancelBeforeCommit),
    ("Cancellation while staging preserves the previous image", CancelDuringStaging),
    ("Invalid source format is rejected", InvalidSource),
    ("Source size uses GIF canvas dimensions and omits unknown dimensions", SourceSize),
    ("Source cannot change while it is being applied", SourceIsStable),
    ("Targets outside the cache are rejected", OutsideRoot),
    ("Structured apply outcomes survive JSON round trip", Serialization),
    ("Invalid staging hash preserves the previous image", InvalidStagingHash),
    ("Isolated operations reject all permission elevation", ElevationGate),
    ("Removal preserves the main image and reports locked variants", RemoveVariants),
    ("Atomic replacement preserves the destination ACL", PreserveDestinationAcl),
    ("Permission helper retains exact paths and operation lifetime", PermissionSessionTests.ScopeAndLifetime),
    ("Protected native ancestors allow scoped recovery with metadata-only parent access", ProtectedMetadataTests.RunAsync),
    ("Denied cache-root attributes can reach scoped repair", PermissionRecoveryTests.DeniedRootAttributesRecover),
    ("Denied cache-folder attributes can reach scoped repair", PermissionRecoveryTests.DeniedFolderAttributesRecover),
    ("Denied cache-file attributes can reach scoped repair", PermissionRecoveryTests.DeniedFileAttributesRecover),
    ("Denied staging attributes can reach scoped folder repair", PermissionRecoveryTests.DeniedStagingAttributesRecover),
    ("Denied attributes at commit can reach scoped destination repair", PermissionRecoveryTests.DeniedCommitAttributesRecover),
    ("Destination repair revalidates staged links before committing", PermissionRecoveryTests.CommitRepairRevalidatesStagedPath),
    ("Rejected permission repair never writes cache files", PermissionRecoveryTests.RejectedRepairMakesNoWrites),
    ("Unreadable ancestors still fail closed after scoped repair", PermissionRecoveryTests.StillDeniedAncestorFailsClosed),
    ("Unsafe paths never reach permission repair", PermissionRecoveryTests.UnsafePathsNeverReachHelper),
    ("Helper success still requires client link validation", PermissionRecoveryTests.RepairSuccessStillRejectsLinks),
    ("Cancellation at a denied attribute check never starts repair", PermissionRecoveryTests.CancellationNeverStartsRepair),
    ("Apply failures retain actionable typed reasons", FailureReasonTests.RunAsync),
    ("Declined elevation is not repeated", PermissionSessionTests.DeclinedElevationIsNotRepeated),
    ("Failed helper launch is not repeated", PermissionSessionTests.FailedLaunchIsNotRepeated),
    ("Precancelled permission repair does not launch", PermissionSessionTests.PrecancelledRepairDoesNotLaunch),
    ("Permission helper connection deadlines remain failures and do not relaunch", PermissionTransportTests.ConnectionDeadlineIsFailure),
    ("Cancelling a pending permission helper connection remains cancellation", PermissionTransportTests.ConnectionCancellation),
    ("Permission helper request deadlines remain failures and break the connection", PermissionTransportTests.RequestDeadlineIsFailure),
    ("Many path repairs share one helper process", PermissionTransportTests.ReusesOneProcess),
    ("Disconnected helper never relaunches", PermissionTransportTests.DisconnectedHelperDoesNotRelaunch),
    ("Trace startup failure and cancelled draining preserve the session", PermissionTransportTests.TraceFailureAndCancelledRead),
    ("Applying with an enabled feature reads once without feature helpers", WindowsImageFeatureTests.EnabledFeatureIsReadOnly),
    ("Unavailable feature evidence never prevents GIF copying", WindowsImageFeatureTests.UnavailableFeatureStillApplies),
    ("Invalid and precancelled applies do not read the feature", WindowsImageFeatureTests.InvalidAndCancelledDoNotRead),
    ("The apply-time feature snapshot survives copy failure and JSON serialization", WindowsImageFeatureTests.CopyFailureRetainsSnapshot),
    ("Cancellation after the read preserves the feature snapshot", WindowsImageFeatureTests.CancellationPreservesSnapshot),
    ("Explicit feature actions dispatch the requested direction", WindowsImageFeatureActionTests.ExplicitDirections),
    ("Explicit feature no-ops and precancellation never launch a helper", WindowsImageFeatureActionTests.NoopAndCancellation),
    ("Explicit feature actions refuse apply overlap", WindowsImageFeatureActionTests.RefusesApplyOverlap),
    ("Explicit feature actions serialize and drain dispatched changes", WindowsImageFeatureActionTests.SerializesAndDrains),
    ("Feature errors before dispatch do not imply an unknown mutation", WindowsImageFeatureActionTests.PreDispatchFailureIsKnown),
    (
        "Explicit feature actions retain the selected ID and reject mismatched observations",
        WindowsImageFeatureActionTests.SelectedIdIsCaptured
    ),
    (
        "Feature and permission requests share one connection and preserve cancelled replies",
        PermissionTransportTests.FeatureRepairReusesConnection
    ),
    ("A disconnected feature reply retains change uncertainty", PermissionTransportTests.DisconnectedFeatureRetainsUncertainty),
    ("Custom feature requests reject invalid IDs and mismatched replies", PermissionTransportTests.FeatureScopeValidation),
};
foreach (var test in tests)
{
    await test.Run();
    Console.WriteLine($"PASS {test.Name}");
}
Console.WriteLine($"{tests.Length} apply pipeline tests passed.");

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static async Task CompleteApply()
{
    using var fixture = new CacheFixture();
    var oddVariant = Path.Combine(fixture.Folder, "LockScreen___1919_1080_notdimmed.jpg");
    await File.WriteAllTextAsync(oddVariant, "old image");
    var result = await fixture.Apply();
    Assert(result.Success && result.Files.Count == 3, "Expected main, display and existing variant targets.");
    Assert(
        result.Files.All(file => file.Copied && file.Verified && file.VerifiedAt is not null && file.Error is null),
        "Every destination must verify with a completion timestamp."
    );
    Assert(result.Files.Select(file => file.Sha256).Distinct().Count() == 1, "All hashes must match.");
    Assert(!result.ApiRequested && !result.ApiCompleted, "An API-off apply must not request Windows image API.");
    Assert(result.SourceSizeBytes == new FileInfo(fixture.Source).Length, "Size describes one source GIF, not all cache copies.");
    Assert(result.SourceWidth == 1 && result.SourceHeight == 1, "GIF logical-screen dimensions are read from the source header.");
    Assert(
        result.FailureException is null && result.Files.All(file => file.FailureException is null),
        "Success retains no failure exception."
    );
}

static async Task EmptyCache()
{
    using var fixture = new CacheFixture(createFolder: false);
    var result = await fixture.Apply();
    Assert(
        result.SourceSizeBytes == new FileInfo(fixture.Source).Length && result.SourceWidth == 1,
        "Source metadata survives a later apply failure."
    );
    Assert(
        !result.Success && result.Files.Count == 0 && result.Error!.Contains("No lock-screen cache"),
        "An empty cache must not report success."
    );
    Assert(result.FailureException is InvalidOperationException, "A top-level failure retains its original exception for error tracking.");
}

static async Task PartialFailure()
{
    using var fixture = new CacheFixture();
    await File.WriteAllTextAsync(fixture.MainImage, "old image");
    await using var locked = new FileStream(fixture.MainImage, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    var result = await fixture.Apply();
    Assert(!result.Success && result.Files.Any(file => file.Verified), "Other destinations should still be attempted.");
    Assert(result.Files.Single(file => file.Path == fixture.MainImage).VerifiedAt is null, "A failed copy has no verification timestamp.");
    Assert(
        result.Files.Single(file => file.Path == fixture.MainImage).Error!.Contains("in use"),
        "Locked file must retain its exception type."
    );
    var failedFile = result.Files.Single(file => file.Path == fixture.MainImage);
    Assert(
        failedFile.FailureException is IOException && ReferenceEquals(result.FailureException, failedFile.FailureException),
        "Partial failures preserve the first actual failed write at the operation boundary."
    );
}

static async Task CommittedHashMismatch()
{
    using var fixture = new CacheFixture();
    var result = await fixture.Apply(item =>
    {
        if (item.Stage == "Verifying" && item.Path == fixture.MainImage)
        {
            File.WriteAllText(fixture.MainImage, "changed after commit");
        }
    });
    var failedFile = result.Files.Single(file => file.Path == fixture.MainImage);
    Assert(failedFile.Copied && !failedFile.Verified && !result.Success, "A hash mismatch remains an unsuccessful verification.");
    Assert(
        failedFile.FailureException is InvalidDataException && ReferenceEquals(result.FailureException, failedFile.FailureException),
        "A mismatch without a thrown exception receives one known failure at the operation boundary."
    );
}

static async Task ReadOnlyFile()
{
    using var fixture = new CacheFixture();
    await File.WriteAllTextAsync(fixture.MainImage, "old image");
    File.SetAttributes(fixture.MainImage, FileAttributes.ReadOnly);
    var events = new List<LockscreenApplyEvent>();
    var result = await fixture.Apply(events.Add);
    Assert(result.Success && events.All(item => item.Stage != "Permissions"), "A writable read-only attribute needs no UAC.");
}

static async Task CancelledBeforeApply()
{
    using var fixture = new CacheFixture();
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    var result = await fixture.Apply(token: cancellation.Token);
    Assert(
        result.SourceSizeBytes is null && result.SourceWidth is null && result.SourceHeight is null,
        "Precancelled work invents no metadata."
    );
    Assert(
        result.Cancelled && !result.Success && Directory.GetFiles(fixture.Folder).Length == 0,
        "Pre-cancelled apply must not write cache files."
    );
    Assert(result.FailureException is null, "Expected cancellation does not create an error tracking failure.");
}

static async Task CancelBetweenCopies()
{
    using var fixture = new CacheFixture();
    using var cancellation = new CancellationTokenSource();
    var result = await fixture.Apply(
        item =>
        {
            if (item.Stage == "Verification")
            {
                cancellation.Cancel();
            }
        },
        cancellation.Token
    );
    Assert(
        result.Cancelled && !result.Success && result.Files.Count(file => file.Verified) == 1,
        "One completed copy and the cancellation must be retained."
    );
    Assert(result.Files.Count(file => file.Error == "Not attempted.") == 1, "Unattempted destination should be explicit.");
}

static async Task CancelBeforeCommit()
{
    using var fixture = new CacheFixture();
    await File.WriteAllTextAsync(fixture.MainImage, "previous image");
    using var cancellation = new CancellationTokenSource();
    var result = await fixture.Apply(
        item =>
        {
            if (item.Stage == "Staged")
            {
                cancellation.Cancel();
            }
        },
        cancellation.Token
    );
    Assert(
        result.Cancelled && await File.ReadAllTextAsync(fixture.MainImage) == "previous image",
        "A verified but uncommitted staging file must leave the old image unchanged."
    );
    Assert(!Directory.GetFiles(fixture.Folder, "*.tmp").Any(), "Staged file must be removed after cancellation.");
}

static async Task CancelDuringStaging()
{
    using var fixture = new CacheFixture();
    await File.WriteAllTextAsync(fixture.MainImage, "previous image");
    using (var source = new FileStream(fixture.Source, FileMode.Open, FileAccess.Write))
    {
        source.SetLength(64 * 1024 * 1024);
    }

    using var cancellation = new CancellationTokenSource();
    using var watcher = new FileSystemWatcher(fixture.Folder, "*.lockscreen.tmp");
    watcher.Created += (_, _) => cancellation.Cancel();
    watcher.EnableRaisingEvents = true;
    var result = await fixture.Apply(token: cancellation.Token);
    Assert(
        result.Cancelled && await File.ReadAllTextAsync(fixture.MainImage) == "previous image",
        "Cancellation during staging must preserve the previous image."
    );
    Assert(!Directory.GetFiles(fixture.Folder, "*.tmp").Any(), "Partial staging file must be removed.");
}

static async Task InvalidSource()
{
    using var fixture = new CacheFixture();
    await File.WriteAllTextAsync(fixture.Source, "not a GIF file");
    var result = await fixture.Apply();
    Assert(
        result.SourceSizeBytes is null && result.SourceWidth is null && result.SourceHeight is null,
        "Invalid GIF signatures produce no GIF size properties."
    );
    Assert(
        !result.Success && result.FailureReason == LockscreenApplyFailureReason.InvalidSource,
        "Invalid source must fail before writes."
    );
    Assert(result.FailureException is InvalidDataException, "Invalid input preserves the original source validation exception.");
    Assert(Directory.GetFiles(fixture.Folder).Length == 0, "Invalid source should not affect the cache.");
}

static async Task SourceSize()
{
    using var fixture = new CacheFixture(createFolder: false);
    var bytes = await File.ReadAllBytesAsync(fixture.Source);
    bytes[6] = 0x80;
    bytes[7] = 0x07;
    bytes[8] = 0x38;
    bytes[9] = 0x04;
    await File.WriteAllBytesAsync(fixture.Source, bytes);
    var sized = await fixture.Apply();
    Assert(
        sized.SourceSizeBytes == bytes.Length && sized.SourceWidth == 1920 && sized.SourceHeight == 1080,
        "Canvas dimensions must use little-endian words and remain distinct from frame dimensions."
    );

    bytes[6] = bytes[7] = 0;
    await File.WriteAllBytesAsync(fixture.Source, bytes);
    var zero = await fixture.Apply();
    Assert(
        zero.SourceSizeBytes == bytes.Length && zero.SourceWidth is null && zero.SourceHeight is null,
        "An invalid zero canvas leaves dimensions unknown while retaining file size."
    );

    await File.WriteAllBytesAsync(fixture.Source, bytes[..9]);
    var truncated = await fixture.Apply();
    Assert(
        truncated.SourceSizeBytes == 9 && truncated.SourceWidth is null && truncated.SourceHeight is null,
        "An incomplete descriptor must not invent dimensions."
    );
}

static async Task SourceIsStable()
{
    using var fixture = new CacheFixture();
    var blockedWrite = false;
    var result = await fixture.Apply(item =>
    {
        if (item.Stage != "Source")
        {
            return;
        }

        try
        {
            File.WriteAllText(fixture.Source, "changed");
        }
        catch (IOException)
        {
            blockedWrite = true;
        }
    });
    Assert(result.Success && blockedWrite, "Source must remain immutable while applying.");
}

static Task OutsideRoot()
{
    using var fixture = new CacheFixture();
    try
    {
        fixture.Permissions.ValidatePath(Path.Combine(fixture.Root, "..", "outside.jpg"));
        throw new InvalidOperationException("An escaped path was accepted.");
    }
    catch (InvalidOperationException ex) when (ex.Message.Contains("outside the current user's")) { }
    return Task.CompletedTask;
}

static async Task Serialization()
{
    using var fixture = new CacheFixture();
    var result = await fixture.Apply();
    const string privateDetail = @"synthetic-secret C:\Users\private-person\private-animation.gif";
    result.FailureException = new IOException(privateDetail);
    result.Files[0].FailureException = new UnauthorizedAccessException(privateDetail);
    var json = JsonSerializer.Serialize(result);
    Assert(!json.Contains(privateDetail) && !json.Contains("FailureException"), "Transient exceptions never enter exported result JSON.");
    var restored = JsonSerializer.Deserialize<LockscreenApplyResult>(json)!;
    Assert(
        restored.SourceSizeBytes == result.SourceSizeBytes
            && restored.SourceWidth == result.SourceWidth
            && restored.SourceHeight == result.SourceHeight,
        "Source size metadata survives result serialization."
    );
    Assert(
        restored.Files.Count == result.Files.Count && restored.Files.All(file => file.Verified),
        "Cloning a report must preserve file outcomes."
    );
    Assert(
        restored.FailureException is null && restored.Files.All(file => file.FailureException is null),
        "Report clones retain no exceptions."
    );
    var removal = new LockscreenGif.Services.DeleteFilesResult { FailedDeletions = 1, FailureException = new IOException(privateDetail) };
    var removalJson = JsonSerializer.Serialize(removal);
    Assert(
        !removalJson.Contains(privateDetail) && !removalJson.Contains("FailureException"),
        "Removal exceptions never enter result JSON."
    );
}

static async Task InvalidStagingHash()
{
    using var fixture = new CacheFixture();
    await File.WriteAllTextAsync(fixture.MainImage, "previous image");
    var result = new LockscreenFileResult { Path = fixture.MainImage };
    await new VerifiedCacheWriter(fixture.Permissions).WriteAsync(
        fixture.Source,
        "incorrect hash",
        result,
        new ApplyProgress(null),
        CancellationToken.None
    );
    Assert(
        !result.Copied && !result.Verified && await File.ReadAllTextAsync(fixture.MainImage) == "previous image",
        "Failed staging verification must preserve the previous image."
    );
    Assert(!Directory.GetFiles(fixture.Folder, "*.tmp").Any(), "Rejected staging file must be removed.");
    Assert(result.FailureException is IOException, "Staging failure retains the actual exception without cleanup replacing it.");
}

static async Task ElevationGate()
{
    using var fixture = new CacheFixture();
    try
    {
        await fixture.Permissions.GrantAsync(fixture.Folder, true, new ApplyProgress(null), CancellationToken.None);
        throw new InvalidOperationException("The isolated-operation elevation gate was bypassed.");
    }
    catch (UnauthorizedAccessException ex) when (ex.Message.Contains("disabled for this isolated operation")) { }
}

static async Task RemoveVariants()
{
    using var fixture = new CacheFixture();
    await fixture.Apply();
    var lockedPath = Path.Combine(fixture.Folder, "locked_notdimmed.jpg");
    await File.WriteAllTextAsync(lockedPath, "locked image");
    await using var locked = new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    var result = await fixture.Remove();
    Assert(
        result.SuccessfulDeletions == 1 && result.FailedDeletions == 1,
        "Removal should delete the available variant and report the locked variant."
    );
    Assert(File.Exists(fixture.MainImage), "Removing variants must preserve the main image.");
    Assert(result.FailureException is IOException, "Partial removal retains its first actual failure for error tracking.");
}

static async Task PreserveDestinationAcl()
{
    using var fixture = new CacheFixture();
    await File.WriteAllTextAsync(fixture.MainImage, "previous image");
    var info = new FileInfo(fixture.MainImage);
    var acl = info.GetAccessControl(AccessControlSections.Access);
    using var user = WindowsIdentity.GetCurrent();
    acl.AddAccessRule(new FileSystemAccessRule(user.User!, FileSystemRights.ReadData, AccessControlType.Allow));
    info.SetAccessControl(acl);
    var before = info.GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access);
    var result = await fixture.Apply();
    var after = info.GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access);
    Assert(result.Success && before == after, "Replacing cache contents must preserve the destination ACL.");
}
