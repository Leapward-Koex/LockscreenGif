namespace LockscreenGif.Models;

public enum MainFlowStage
{
    Choose,
    Edit,
    Set,
    Done,
}

public enum MainFlowSource
{
    None,
    Gif,
    Video,
}

public enum MainFlowOperation
{
    None,
    Selecting,
    Generating,
    Applying,
    Saving,
}

public enum MainFlowApplyOutcome
{
    None,
    Succeeded,
    Failed,
    Partial,
    Cancelled,
}

public readonly record struct MainFlowOperationToken(long Id, MainFlowOperation Operation, long SourceRevision, long EditRevision);

/// <summary>
/// Owns draft navigation and readiness independently of WinUI and the source retained for diagnostics.
/// The page owns media resources and commits them only while its operation token is current.
/// </summary>
public sealed class MainFlowState
{
    private long _nextOperationId;
    private MainFlowOperationToken _activeOperation;

    public MainFlowStage Stage { get; private set; } = MainFlowStage.Choose;
    public MainFlowSource Source { get; private set; }
    public MainFlowOperation Operation { get; private set; }
    public long SourceRevision { get; private set; }
    public long EditRevision { get; private set; }
    public long PreparedSourceRevision { get; private set; } = -1;
    public long PreparedEditRevision { get; private set; } = -1;
    public bool IsEditValid { get; private set; }
    public bool HasPendingEdits { get; private set; }
    public MainFlowApplyOutcome LastApplyOutcome { get; private set; }
    public bool LastApplyChangedFiles { get; private set; }
    public string? LastApplyError { get; private set; }
    public bool IsBusy => Operation != MainFlowOperation.None;

    public bool HasCurrentPreparedOutput =>
        Source != MainFlowSource.None && PreparedSourceRevision == SourceRevision && PreparedEditRevision == EditRevision;

    public bool CanApply => !IsBusy && Stage == MainFlowStage.Set && IsEditValid && !HasPendingEdits && HasCurrentPreparedOutput;
    public bool CanSave =>
        !IsBusy
        && Source == MainFlowSource.Video
        && Stage is MainFlowStage.Set or MainFlowStage.Done
        && IsEditValid
        && !HasPendingEdits
        && HasCurrentPreparedOutput;
    public bool CanGoBack => !IsBusy && Stage != MainFlowStage.Choose;

    public bool CanGenerate =>
        !IsBusy
        && Stage == MainFlowStage.Edit
        && Source == MainFlowSource.Video
        && IsEditValid
        && (HasPendingEdits || !HasCurrentPreparedOutput);

    // Continuing an unprepared video requires an asynchronous generation operation instead.
    public bool CanContinue =>
        !IsBusy
        && (
            (Stage == MainFlowStage.Choose && Source != MainFlowSource.None)
            || (Stage == MainFlowStage.Edit && IsEditValid && !HasPendingEdits && HasCurrentPreparedOutput)
        );

    public bool CanNavigate(MainFlowStage stage) =>
        !IsBusy
        && stage != Stage
        && stage switch
        {
            MainFlowStage.Choose => true,
            MainFlowStage.Edit => Source == MainFlowSource.Video,
            MainFlowStage.Set => IsEditValid && !HasPendingEdits && HasCurrentPreparedOutput,
            _ => false,
        };

    public bool TryNavigate(MainFlowStage stage)
    {
        if (!CanNavigate(stage))
        {
            return false;
        }

        Stage = stage;
        return true;
    }

    public bool TryContinue()
    {
        if (!CanContinue)
        {
            return false;
        }

        Stage = Stage == MainFlowStage.Choose && Source == MainFlowSource.Video ? MainFlowStage.Edit : MainFlowStage.Set;
        return true;
    }

    public bool TryGoBack()
    {
        if (!CanGoBack)
        {
            return false;
        }

        Stage = Stage switch
        {
            MainFlowStage.Edit => MainFlowStage.Choose,
            MainFlowStage.Set => Source == MainFlowSource.Video ? MainFlowStage.Edit : MainFlowStage.Choose,
            MainFlowStage.Done => Source == MainFlowSource.Video ? MainFlowStage.Edit : MainFlowStage.Set,
            _ => MainFlowStage.Choose,
        };
        return true;
    }

    public bool TryReset()
    {
        if (IsBusy)
        {
            return false;
        }

        Stage = MainFlowStage.Choose;
        Source = MainFlowSource.None;
        SourceRevision++;
        EditRevision = 0;
        IsEditValid = false;
        HasPendingEdits = false;
        ClearPreparedOutput();
        ClearApplyOutcome();
        return true;
    }

    /// <summary>Only committed changes to trim or output settings change the edit revision; playhead movement does not.</summary>
    public bool TryUpdateEdit(bool isValid, bool changed = true, bool hasPendingEdits = false)
    {
        if (IsBusy || Stage != MainFlowStage.Edit || Source != MainFlowSource.Video)
        {
            return false;
        }

        IsEditValid = isValid;
        HasPendingEdits = hasPendingEdits;
        if (changed)
        {
            EditRevision++;
            ClearApplyOutcome();
        }
        return true;
    }

    public bool TryBeginOperation(MainFlowOperation operation, out MainFlowOperationToken token)
    {
        token = default;
        if (
            IsBusy
            || !(
                operation switch
                {
                    MainFlowOperation.Selecting => true,
                    MainFlowOperation.Generating => CanGenerate && !HasPendingEdits,
                    MainFlowOperation.Applying => CanApply,
                    MainFlowOperation.Saving => CanSave,
                    _ => false,
                }
            )
        )
        {
            return false;
        }

        Operation = operation;
        token = _activeOperation = new(++_nextOperationId, operation, SourceRevision, EditRevision);
        if (operation == MainFlowOperation.Generating)
        {
            ClearPreparedOutput();
        }
        else if (operation == MainFlowOperation.Applying)
        {
            ClearApplyOutcome();
        }
        return true;
    }

    public bool IsCurrentOperation(MainFlowOperationToken token) =>
        IsBusy && token == _activeOperation && token.SourceRevision == SourceRevision && token.EditRevision == EditRevision;

    /// <summary>Finishes a cancelled or failed operation without replacing the current draft.</summary>
    public bool TryFinishOperation(MainFlowOperationToken token)
    {
        if (!IsCurrentOperation(token))
        {
            return false;
        }

        EndOperation();
        return true;
    }

    /// <summary>Commit only after the chosen file and its preview have been prepared successfully.</summary>
    public bool TryCommitSelection(MainFlowOperationToken token, MainFlowSource source)
    {
        if (
            !IsCurrentOperation(token)
            || token.Operation != MainFlowOperation.Selecting
            || source is not (MainFlowSource.Gif or MainFlowSource.Video)
        )
        {
            return false;
        }

        EndOperation();
        Source = source;
        SourceRevision++;
        EditRevision = 0;
        IsEditValid = true;
        HasPendingEdits = false;
        ClearApplyOutcome();
        if (source == MainFlowSource.Gif)
        {
            MarkPreparedOutput();
            Stage = MainFlowStage.Set;
        }
        else
        {
            ClearPreparedOutput();
            Stage = MainFlowStage.Edit;
        }
        return true;
    }

    public bool TryCompleteGeneration(MainFlowOperationToken token, bool success)
    {
        if (!IsCurrentOperation(token) || token.Operation != MainFlowOperation.Generating)
        {
            return false;
        }

        EndOperation();
        if (success)
        {
            MarkPreparedOutput();
            Stage = MainFlowStage.Set;
        }
        return true;
    }

    public bool TryCompleteApply(MainFlowOperationToken token, LockscreenApplyResult result)
    {
        if (!IsCurrentOperation(token) || token.Operation != MainFlowOperation.Applying)
        {
            return false;
        }

        ArgumentNullException.ThrowIfNull(result);
        EndOperation();
        LastApplyOutcome = ClassifyApplyOutcome(result);
        LastApplyChangedFiles = result.Files.Any(file => file.Copied || file.Verified);
        LastApplyError =
            LastApplyOutcome is MainFlowApplyOutcome.Failed or MainFlowApplyOutcome.Partial && !string.IsNullOrWhiteSpace(result.Error)
                ? result.Error
                : null;
        Stage = LastApplyOutcome == MainFlowApplyOutcome.Succeeded ? MainFlowStage.Done : MainFlowStage.Set;
        return true;
    }

    public static MainFlowApplyOutcome ClassifyApplyOutcome(LockscreenApplyResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Cancelled)
        {
            return MainFlowApplyOutcome.Cancelled;
        }
        if (result.Success)
        {
            return MainFlowApplyOutcome.Succeeded;
        }
        return result.Files.Any(file => file.Copied || file.Verified) ? MainFlowApplyOutcome.Partial : MainFlowApplyOutcome.Failed;
    }

    private void EndOperation()
    {
        Operation = MainFlowOperation.None;
        _activeOperation = default;
    }

    private void MarkPreparedOutput()
    {
        PreparedSourceRevision = SourceRevision;
        PreparedEditRevision = EditRevision;
    }

    private void ClearPreparedOutput()
    {
        PreparedSourceRevision = -1;
        PreparedEditRevision = -1;
    }

    private void ClearApplyOutcome()
    {
        LastApplyOutcome = MainFlowApplyOutcome.None;
        LastApplyChangedFiles = false;
        LastApplyError = null;
    }
}
