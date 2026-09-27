using LockscreenGif.Models;

namespace MainFlow.Tests;

internal static class Program
{
    private static int _checks;

    private static void Main()
    {
        InitialState();
        BreadcrumbNavigation();
        GifRoute();
        VideoRouteAndBack();
        EditInvalidationAndValidation();
        PendingEditValidation();
        TransactionalSelection();
        GenerationFailureAndStaleCompletion();
        BusyGuards();
        ApplyOutcomes();
        ResetReadiness();
        InvalidOperationTokens();
        Console.WriteLine($"All {_checks} main flow checks passed. No files, native APIs, or analytics transports were used.");
    }

    private static void InitialState()
    {
        var flow = new MainFlowState();
        Check(flow.Stage == MainFlowStage.Choose && flow.Source == MainFlowSource.None && !flow.IsBusy, "start with an empty Choose stage");
        Check(
            !flow.CanApply && !flow.CanSave && !flow.CanGenerate && !flow.CanContinue && !flow.CanGoBack,
            "empty draft cannot advance or apply"
        );
        Check(!flow.TryNavigate(MainFlowStage.Edit) && !flow.TryNavigate(MainFlowStage.Set), "empty draft cannot skip source preparation");
        Check(!flow.TryNavigate(MainFlowStage.Done), "completion cannot be reached through navigation");
        Check(!flow.TryBeginOperation(MainFlowOperation.None, out _), "None is not an operation");
        Check(!flow.TryBeginOperation((MainFlowOperation)999, out _), "unknown operation is rejected");
    }

    private static void BreadcrumbNavigation()
    {
        var flow = new MainFlowState();
        Check(
            !flow.CanNavigate(MainFlowStage.Choose) && !flow.TryNavigate(MainFlowStage.Choose),
            "current Choose step is not a navigation action"
        );
        flow = PreparedVideo();
        Check(!flow.CanNavigate(MainFlowStage.Set) && !flow.TryNavigate(MainFlowStage.Set), "current Set step is not a navigation action");
        Check(flow.TryNavigate(MainFlowStage.Edit), "prepared video can return to Edit through its breadcrumb");
        Check(
            !flow.CanNavigate(MainFlowStage.Edit) && !flow.TryNavigate(MainFlowStage.Edit),
            "current Edit step is not a navigation action"
        );
        Check(flow.TryNavigate(MainFlowStage.Set), "unchanged editor can return to Set through its breadcrumb");
        var token = Begin(flow, MainFlowOperation.Applying);
        Check(flow.TryCompleteApply(token, Success()), "successful apply reaches the Applied step");
        Check(
            !flow.CanNavigate(MainFlowStage.Done) && !flow.TryNavigate(MainFlowStage.Done),
            "current Applied step is not a navigation action"
        );
        Check(
            flow.CanNavigate(MainFlowStage.Choose) && flow.CanNavigate(MainFlowStage.Edit) && flow.CanNavigate(MainFlowStage.Set),
            "Applied keeps earlier breadcrumbs available without duplicate footer actions"
        );
        Check(flow.TryNavigate(MainFlowStage.Set) && flow.CanApply, "Set breadcrumb allows applying the prepared GIF again");
        Check(
            flow.TryNavigate(MainFlowStage.Choose) && flow.HasCurrentPreparedOutput,
            "Choose breadcrumb preserves the previous draft until replacement"
        );
    }

    private static void GifRoute()
    {
        var flow = Select(MainFlowSource.Gif);
        Check(
            flow.Stage == MainFlowStage.Set && flow.HasCurrentPreparedOutput && flow.CanApply,
            "GIF selection skips Edit and reaches the preview"
        );
        Check(flow.LastApplyOutcome == MainFlowApplyOutcome.None && !flow.IsBusy, "selecting a GIF does not apply it");
        Check(!flow.CanGenerate && !flow.CanNavigate(MainFlowStage.Edit), "GIF route cannot enter video editing");
        Check(
            !flow.CanSave && !flow.TryBeginOperation(MainFlowOperation.Saving, out _),
            "existing GIF does not expose generated-output saving"
        );
        var revision = flow.SourceRevision;
        Check(flow.TryGoBack() && flow.Stage == MainFlowStage.Choose, "GIF Back returns to Choose");
        Check(flow.CanContinue && flow.TryContinue() && flow.Stage == MainFlowStage.Set, "retained GIF can return to its preview");
        Check(flow.SourceRevision == revision && flow.HasCurrentPreparedOutput, "GIF Back preserves the source and prepared output");
        var token = Begin(flow, MainFlowOperation.Applying);
        Check(flow.TryCompleteApply(token, Success()), "GIF apply completes");
        Check(flow.Stage == MainFlowStage.Done, "only successful apply enters Done");
        Check(flow.TryGoBack() && flow.Stage == MainFlowStage.Set, "completed GIF returns to its preview");
    }

    private static void VideoRouteAndBack()
    {
        var flow = Select(MainFlowSource.Video);
        Check(
            flow.Stage == MainFlowStage.Edit && flow.IsEditValid && flow.CanGenerate,
            "prepared video starts in Edit with a valid full clip"
        );
        Check(!flow.CanApply && !flow.CanContinue && !flow.TryNavigate(MainFlowStage.Set), "video cannot skip GIF generation");
        var sourceRevision = flow.SourceRevision;
        Check(flow.TryUpdateEdit(true), "committed trim updates the draft");
        var editRevision = flow.EditRevision;
        var token = Begin(flow, MainFlowOperation.Generating);
        Check(flow.TryCompleteGeneration(token, true), "generation completes with its matching token");
        Check(
            flow.Stage == MainFlowStage.Set && flow.CanApply && !flow.IsBusy,
            "generation exposes preview before a separate apply action"
        );
        Check(flow.LastApplyOutcome == MainFlowApplyOutcome.None, "generation is not an apply outcome");
        Check(flow.CanSave, "generated preview can be saved");
        Check(flow.TryGoBack() && flow.Stage == MainFlowStage.Edit, "video Back returns to Edit");
        Check(flow.CanContinue && !flow.CanGenerate, "unchanged video can continue without regeneration");
        Check(flow.TryContinue() && flow.Stage == MainFlowStage.Set, "unchanged video returns to the prepared preview");
        Check(flow.SourceRevision == sourceRevision && flow.EditRevision == editRevision, "Back and Continue preserve source and edits");
        Check(
            flow.TryNavigate(MainFlowStage.Choose) && flow.HasCurrentPreparedOutput,
            "Choose navigation preserves an existing video draft"
        );
        Check(flow.TryContinue() && flow.Stage == MainFlowStage.Edit && flow.HasCurrentPreparedOutput, "retained video resumes its editor");
        Check(flow.TryContinue(), "retained prepared video returns to Set");
        token = Begin(flow, MainFlowOperation.Applying);
        Check(flow.TryCompleteApply(token, Success()), "video apply completes");
        Check(flow.TryGoBack() && flow.Stage == MainFlowStage.Edit, "Edit this one returns a completed video to Edit");
        Check(flow.CanContinue && flow.HasCurrentPreparedOutput, "editing a completed video retains its output until an actual change");
    }

    private static void EditInvalidationAndValidation()
    {
        var flow = PreparedVideo();
        Check(flow.TryGoBack(), "open the prepared video's editor");
        var revision = flow.EditRevision;
        Check(flow.TryUpdateEdit(true, changed: false), "preview-only changes leave edits valid");
        Check(
            flow.EditRevision == revision && flow.HasCurrentPreparedOutput && flow.CanContinue,
            "playhead movement does not invalidate output"
        );
        Check(flow.TryUpdateEdit(false, changed: false), "invalid typed time can be reported without committing a new trim");
        Check(
            !flow.CanContinue && !flow.CanGenerate && !flow.CanNavigate(MainFlowStage.Set),
            "invalid time blocks both cached and new output"
        );
        Check(flow.TryUpdateEdit(true, changed: false) && flow.CanContinue, "reverting invalid text reuses an unchanged output");
        foreach (var setting in new[] { "trim start", "trim end", "resolution", "frame rate" })
        {
            var preparedRevision = flow.PreparedEditRevision;
            Check(flow.TryUpdateEdit(true), setting + " commits an edit");
            Check(
                flow.EditRevision > preparedRevision && !flow.HasCurrentPreparedOutput,
                setting + " invalidates the previous generated output"
            );
            Check(flow.CanGenerate && !flow.CanApply && !flow.CanContinue, setting + " requires generation before Set");
            var token = Begin(flow, MainFlowOperation.Generating);
            Check(flow.TryCompleteGeneration(token, true) && flow.TryGoBack(), setting + " can regenerate and return to Edit");
        }
        Check(flow.TryUpdateEdit(false), "invalid committed edit is recorded");
        Check(
            !flow.TryBeginOperation(MainFlowOperation.Generating, out _) && !flow.TryNavigate(MainFlowStage.Set),
            "invalid edits cannot generate or reach Set"
        );
    }

    private static void PendingEditValidation()
    {
        var flow = PreparedVideo();
        Check(flow.TryGoBack(), "open editor to type a pending trim");
        var revision = flow.EditRevision;
        Check(
            flow.TryUpdateEdit(true, changed: false, hasPendingEdits: true),
            "valid pending text can be recorded without changing the committed trim"
        );
        Check(
            flow.HasPendingEdits && flow.CanGenerate && !flow.CanContinue,
            "valid pending text enables preparation but cannot reuse output directly"
        );
        Check(!flow.CanNavigate(MainFlowStage.Set) && !flow.CanApply && !flow.CanSave, "pending text blocks Set, apply, and save");
        Check(!flow.TryBeginOperation(MainFlowOperation.Generating, out _), "pending text must be committed before generation starts");
        Check(flow.EditRevision == revision && flow.HasCurrentPreparedOutput, "uncommitted text retains the previously generated revision");
        Check(flow.TryUpdateEdit(false, changed: false, hasPendingEdits: true), "invalid pending text can be reported");
        Check(
            !flow.CanGenerate && !flow.CanContinue && !flow.CanNavigate(MainFlowStage.Set),
            "invalid pending text blocks preparation and advancement"
        );
        Check(flow.TryUpdateEdit(true, changed: false, hasPendingEdits: false), "Escape can restore the committed trim");
        Check(
            !flow.HasPendingEdits && flow.EditRevision == revision && flow.CanContinue && !flow.CanGenerate,
            "Escape restores cached-output reuse without changing its revision"
        );
        Check(flow.TryUpdateEdit(true, changed: false, hasPendingEdits: true), "a pending replacement trim is recorded");
        Check(flow.TryUpdateEdit(true, changed: true, hasPendingEdits: false), "Continue can commit a changed trim before preparing it");
        Check(
            !flow.HasPendingEdits && flow.EditRevision > revision && !flow.HasCurrentPreparedOutput,
            "committed changed text invalidates the old output"
        );
        var token = Begin(flow, MainFlowOperation.Generating);
        Check(flow.TryCompleteGeneration(token, true) && flow.CanApply, "committed text can generate a fresh preview");
        Check(
            flow.TryGoBack() && flow.TryUpdateEdit(true, changed: false, hasPendingEdits: true),
            "pending text can remain while choosing a different source"
        );
        token = Begin(flow, MainFlowOperation.Selecting);
        Check(
            flow.TryCommitSelection(token, MainFlowSource.Gif) && !flow.HasPendingEdits && flow.CanApply,
            "successful replacement clears the old draft's pending text"
        );
        Check(flow.TryReset() && !flow.HasPendingEdits, "reset clears pending state");
    }

    private static void TransactionalSelection()
    {
        var flow = PreparedVideo();
        var sourceRevision = flow.SourceRevision;
        var editRevision = flow.EditRevision;
        var token = Begin(flow, MainFlowOperation.Selecting);
        Check(flow.TryFinishOperation(token), "cancelled file picker finishes without committing");
        Check(
            flow.Stage == MainFlowStage.Set && flow.Source == MainFlowSource.Video && flow.CanApply,
            "picker cancellation keeps the previous preview"
        );
        Check(flow.SourceRevision == sourceRevision && flow.EditRevision == editRevision, "cancelled selection preserves source and edits");
        token = Begin(flow, MainFlowOperation.Selecting);
        Check(flow.TryFinishOperation(token), "failed video loading finishes without committing");
        Check(
            flow.HasCurrentPreparedOutput && flow.SourceRevision == sourceRevision,
            "failed loading preserves the prior generated output"
        );
        var newerToken = Begin(flow, MainFlowOperation.Selecting);
        Check(
            !flow.TryCommitSelection(token, MainFlowSource.Gif) && flow.IsCurrentOperation(newerToken),
            "late selection cannot replace the newer operation"
        );
        Check(flow.TryCommitSelection(newerToken, MainFlowSource.Video), "successfully prepared replacement commits atomically");
        Check(flow.Stage == MainFlowStage.Edit && flow.SourceRevision > sourceRevision, "replacement video starts a new source revision");
        Check(!flow.HasCurrentPreparedOutput && !flow.CanApply && flow.EditRevision == 0, "replacement video cannot use an older GIF");
        token = Begin(flow, MainFlowOperation.Selecting);
        Check(flow.TryCommitSelection(token, MainFlowSource.Gif), "video draft can be replaced by a GIF");
        Check(
            flow.Stage == MainFlowStage.Set && flow.CanApply && !flow.CanNavigate(MainFlowStage.Edit),
            "GIF replacement removes video navigation"
        );
    }

    private static void GenerationFailureAndStaleCompletion()
    {
        var flow = PreparedVideo();
        Check(flow.TryGoBack() && flow.TryUpdateEdit(true), "change a previously generated video");
        var failedToken = Begin(flow, MainFlowOperation.Generating);
        Check(flow.TryCompleteGeneration(failedToken, false), "generation failure completes the active operation");
        Check(
            flow.Stage == MainFlowStage.Edit && flow.CanGenerate && !flow.CanApply && !flow.HasCurrentPreparedOutput,
            "generation failure cannot expose stale media"
        );
        var retryToken = Begin(flow, MainFlowOperation.Generating);
        Check(!flow.TryCompleteGeneration(failedToken, true), "late successful callback from failed generation is ignored");
        Check(
            flow.IsCurrentOperation(retryToken) && flow.Stage == MainFlowStage.Edit,
            "stale callback does not release a retry's busy state"
        );
        Check(flow.TryFinishOperation(retryToken), "interrupted generation can release its operation");
        Check(
            !flow.TryCompleteGeneration(retryToken, true) && !flow.HasCurrentPreparedOutput,
            "finished generation token cannot publish output later"
        );
        retryToken = Begin(flow, MainFlowOperation.Generating);
        Check(flow.TryCompleteGeneration(retryToken, true) && flow.CanApply, "fresh generation can publish the current output");
    }

    private static void BusyGuards()
    {
        foreach (
            var operation in new[]
            {
                MainFlowOperation.Selecting,
                MainFlowOperation.Generating,
                MainFlowOperation.Applying,
                MainFlowOperation.Saving,
            }
        )
        {
            var flow = operation == MainFlowOperation.Generating ? Select(MainFlowSource.Video) : PreparedVideo();
            var stage = flow.Stage;
            var revision = flow.EditRevision;
            var token = Begin(flow, operation);
            Check(
                !flow.TryNavigate(MainFlowStage.Choose) && !flow.TryNavigate(MainFlowStage.Edit) && !flow.TryNavigate(MainFlowStage.Set),
                operation + " blocks stage navigation"
            );
            Check(!flow.TryGoBack() && !flow.TryContinue() && !flow.TryReset(), operation + " blocks Back, Continue, and reset");
            Check(
                !flow.TryUpdateEdit(true) && !flow.TryUpdateEdit(false, changed: false),
                operation + " blocks edits and validation callbacks"
            );
            Check(
                !flow.TryBeginOperation(MainFlowOperation.Selecting, out _) && !flow.TryBeginOperation(operation, out _),
                operation + " blocks replacement and duplicate actions"
            );
            Check(!flow.CanApply && !flow.CanSave && !flow.CanGenerate && !flow.CanContinue, operation + " disables primary action gates");
            Check(
                flow.Stage == stage && flow.EditRevision == revision && flow.IsCurrentOperation(token),
                operation + " preserves the operation snapshot"
            );
            Check(flow.TryFinishOperation(token) && !flow.IsBusy, operation + " is released by its matching token");
        }
    }

    private static void ApplyOutcomes()
    {
        var cases = new (string Name, LockscreenApplyResult Result, MainFlowApplyOutcome Outcome, bool Changed)[]
        {
            ("verified success", Success(), MainFlowApplyOutcome.Succeeded, true),
            ("failure before copying", new(), MainFlowApplyOutcome.Failed, false),
            (
                "partially verified files",
                new() { Files = [new() { Copied = true, Verified = true }, new()] },
                MainFlowApplyOutcome.Partial,
                true
            ),
            ("copied but unverified file", new() { Files = [new() { Copied = true }] }, MainFlowApplyOutcome.Partial, true),
            ("cancelled before copying", new() { Cancelled = true }, MainFlowApplyOutcome.Cancelled, false),
            (
                "cancelled after copying",
                new() { Cancelled = true, Files = [new() { Copied = true }] },
                MainFlowApplyOutcome.Cancelled,
                true
            ),
        };
        foreach (var item in cases)
        {
            var flow = PreparedVideo();
            var token = Begin(flow, MainFlowOperation.Applying);
            Check(flow.TryCompleteApply(token, item.Result), item.Name + " completes with the current apply token");
            Check(
                flow.LastApplyOutcome == item.Outcome && flow.LastApplyChangedFiles == item.Changed,
                item.Name + " has precise outcome metadata"
            );
            Check(
                flow.Stage == (item.Outcome == MainFlowApplyOutcome.Succeeded ? MainFlowStage.Done : MainFlowStage.Set),
                item.Name + " has the correct destination stage"
            );
            Check(flow.HasCurrentPreparedOutput && flow.CanSave, item.Name + " retains the generated GIF");
            if (item.Outcome != MainFlowApplyOutcome.Succeeded)
            {
                Check(flow.CanApply, item.Name + " permits retry");
                var retryToken = Begin(flow, MainFlowOperation.Applying);
                Check(
                    flow.LastApplyOutcome == MainFlowApplyOutcome.None && !flow.LastApplyChangedFiles,
                    item.Name + " clears previous result while retrying"
                );
                Check(
                    !flow.TryCompleteApply(token, Success()) && flow.IsCurrentOperation(retryToken),
                    item.Name + " ignores an old success while retrying"
                );
                Check(
                    flow.TryCompleteApply(retryToken, Success()) && flow.Stage == MainFlowStage.Done,
                    item.Name + " can succeed on retry"
                );
            }
        }
    }

    private static void ResetReadiness()
    {
        var flow = PreparedVideo();
        var token = Begin(flow, MainFlowOperation.Applying);
        Check(flow.TryCompleteApply(token, Success()) && flow.TryReset(), "Set another animation resets a completed draft");
        Check(
            flow.Stage == MainFlowStage.Choose && flow.Source == MainFlowSource.None && !flow.HasCurrentPreparedOutput,
            "reset clears source navigation and prepared readiness"
        );
        Check(flow.LastApplyOutcome == MainFlowApplyOutcome.None && !flow.LastApplyChangedFiles, "reset clears the previous apply outcome");
        Check(
            !flow.CanApply && !flow.CanNavigate(MainFlowStage.Set) && !flow.CanContinue,
            "reset blocks reuse of the previous source through every navigation route"
        );
        Check(!flow.TryCompleteApply(token, Success()), "completion from before reset cannot restore Done");
        var nextToken = Begin(flow, MainFlowOperation.Selecting);
        Check(flow.TryCommitSelection(nextToken, MainFlowSource.Video), "new source can be committed after reset");
        Check(
            !flow.CanApply && !flow.HasCurrentPreparedOutput && flow.CanGenerate,
            "a new source cannot inherit the prior completion's GIF"
        );
    }

    private static void InvalidOperationTokens()
    {
        var flow = Select(MainFlowSource.Video);
        var token = Begin(flow, MainFlowOperation.Generating);
        Check(!flow.TryFinishOperation(default), "default token cannot finish work");
        Check(
            !flow.TryCommitSelection(token, MainFlowSource.Gif) && !flow.TryCompleteApply(token, Success()),
            "generation token cannot commit a different kind of result"
        );
        Check(flow.IsCurrentOperation(token), "rejected result preserves the active operation");
        Check(flow.TryCompleteGeneration(token, true), "correct result still completes the operation");
        token = Begin(flow, MainFlowOperation.Selecting);
        Check(!flow.TryCommitSelection(token, MainFlowSource.None), "selection cannot commit an empty source");
        Check(!flow.TryCommitSelection(token, (MainFlowSource)999), "selection cannot commit an unknown source type");
        Check(flow.IsCurrentOperation(token) && flow.TryFinishOperation(token), "invalid selection result does not lose the active token");
    }

    private static MainFlowState Select(MainFlowSource source)
    {
        var flow = new MainFlowState();
        var token = Begin(flow, MainFlowOperation.Selecting);
        Check(flow.TryCommitSelection(token, source), "commit " + source + " selection");
        return flow;
    }

    private static MainFlowState PreparedVideo()
    {
        var flow = Select(MainFlowSource.Video);
        var token = Begin(flow, MainFlowOperation.Generating);
        Check(flow.TryCompleteGeneration(token, true), "prepare video output");
        return flow;
    }

    private static MainFlowOperationToken Begin(MainFlowState flow, MainFlowOperation operation)
    {
        Check(flow.TryBeginOperation(operation, out var token), "begin " + operation);
        return token;
    }

    private static LockscreenApplyResult Success() => new() { Success = true, Files = [new() { Copied = true, Verified = true }] };

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("FAILED: " + message);
        }
        _checks++;
        Console.WriteLine("PASS " + message);
    }
}
