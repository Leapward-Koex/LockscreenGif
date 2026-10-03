using LockscreenGif.Contracts.Services;
using LockscreenGif.Models;
using LockscreenGif.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage;

namespace LockscreenGif.Views;

public sealed partial class MainPage
{
    private StorageFile? _sourceFile;
    private StorageFile? _preparedGif;
    private (int Start, int End, int Width, double Fps)? _editSignature;
    private MainFlowStage? _displayedStage;
    private bool _updatingTimeText;
    private bool _flowReady;
    private MainFlowState Flow => ViewModel.Flow;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is "Choose")
        {
            Flow.TryNavigate(MainFlowStage.Choose);
        }
        RefreshFlowUi();
    }

    private void SyncDraftState()
    {
        if (!_flowReady || Flow.IsBusy || Flow.Source != MainFlowSource.Video || _frames is null || _updatingTimeText)
        {
            return;
        }

        var settingsReady = ComboResolution.SelectedItem is ComboBoxItem && ComboFps.SelectedItem is ComboBoxItem;
        var signature = (
            _startFrame,
            _endFrame,
            (int?)((ComboBoxItem?)ComboResolution.SelectedItem)?.Tag ?? 0,
            (double?)((ComboBoxItem?)ComboFps.SelectedItem)?.Tag ?? 0
        );
        var changed = _editSignature is not null && _editSignature != signature;
        _editSignature = signature;
        var startValid = TryReadTime(StartTimeTextBox, out var pendingStart, out _);
        var endValid = TryReadTime(EndTimeTextBox, out var pendingEnd, out _);
        // Pending text blocks the old preview; Escape can still restore it without regeneration.
        var pending = !startValid || !endValid || pendingStart != _startFrame || pendingEnd != _endFrame;
        Flow.TryUpdateEdit(_mediaReady && settingsReady && startValid && endValid && !_startInvalid && !_endInvalid, changed, pending);
        RefreshFlowUi();
    }

    private void RefreshFlowUi()
    {
        if (!_flowReady)
        {
            return;
        }

        var stage = Flow.Stage;
        var busy = Flow.IsBusy;
        var video = Flow.Source == MainFlowSource.Video;
        var preview = stage is MainFlowStage.Set or MainFlowStage.Done;
        ChooseStagePanel.Visibility = stage == MainFlowStage.Choose ? Visibility.Visible : Visibility.Collapsed;
        EditStagePanel.Visibility = stage == MainFlowStage.Edit ? Visibility.Visible : Visibility.Collapsed;
        PreviewStagePanel.Visibility = preview ? Visibility.Visible : Visibility.Collapsed;
        CompletionPanel.Visibility = stage == MainFlowStage.Done ? Visibility.Visible : Visibility.Collapsed;
        EditStepItem.Visibility = video ? Visibility.Visible : Visibility.Collapsed;
        UpdateStep(ChooseStepButton, ChooseStepCurrent, ChooseStepCurrentText, MainFlowStage.Choose, "1 · Choose");
        UpdateStep(EditStepButton, EditStepCurrent, EditStepCurrentText, MainFlowStage.Edit, "2 · Edit");
        UpdateStep(SetStepButton, SetStepCurrent, SetStepCurrentText, MainFlowStage.Set, $"{(video ? 3 : 2)} · Set");
        AppliedStepCurrent.Visibility = stage == MainFlowStage.Done ? Visibility.Visible : Visibility.Collapsed;

        StageHeading.Text = stage switch
        {
            MainFlowStage.Choose => "Choose your animation",
            MainFlowStage.Edit => "Edit your video",
            MainFlowStage.Set => "Ready to set",
            _ => "Applied successfully",
        };
        StageDescription.Text = stage switch
        {
            MainFlowStage.Choose => "Start with a video or an existing GIF.",
            MainFlowStage.Edit => "Choose your clip, then continue to preview the generated GIF.",
            MainFlowStage.Set => "Review the animation before setting your lock screen.",
            _ => "The lock-screen files were updated. Lock your PC to check the animation.",
        };
        CurrentSelectionSummary.Text = _sourceFile is null ? "" : $"Current selection: {_sourceFile.Name}";
        CurrentSelectionSummary.Visibility = _sourceFile is null ? Visibility.Collapsed : Visibility.Visible;
        var outputSummary = video
            ? $"{((ComboBoxItem?)ComboResolution.SelectedItem)?.Content} · {((ComboBoxItem?)ComboFps.SelectedItem)?.Content}"
            : "Existing GIF";
        OutputSettingsSummary.Text = outputSummary;
        PreparedSummary.Text = video ? $"{_sourceFile?.Name}\n{SelectionSummary.Text}\n{outputSummary}" : _sourceFile?.Name ?? "";

        BrowseVideoButton.IsEnabled = !busy;
        BrowseGifButton.IsEnabled = !busy;
        RemoveAnimationLink.IsEnabled = !busy;
        TrimEditorHost.IsEnabled = !busy;
        VideoSettingsHost.IsEnabled = !busy;
        ContinueSelectionButton.Visibility =
            stage == MainFlowStage.Choose && Flow.Source != MainFlowSource.None ? Visibility.Visible : Visibility.Collapsed;
        ContinueSelectionButton.IsEnabled = Flow.CanContinue;
        GenerateButton.Visibility = stage == MainFlowStage.Edit ? Visibility.Visible : Visibility.Collapsed;
        GenerateButton.IsEnabled = Flow.CanContinue || Flow.CanGenerate;
        ApplyButton.Visibility = stage == MainFlowStage.Set ? Visibility.Visible : Visibility.Collapsed;
        ApplyButton.IsEnabled = Flow.CanApply;
        ApplyButton.Content = Flow.LastApplyOutcome
            is MainFlowApplyOutcome.Failed
                or MainFlowApplyOutcome.Partial
                or MainFlowApplyOutcome.Cancelled
            ? "Retry"
            : "Set lock screen";
        BackButton.Visibility = stage is MainFlowStage.Edit or MainFlowStage.Set ? Visibility.Visible : Visibility.Collapsed;
        BackButton.IsEnabled = Flow.CanGoBack;
        SaveGeneratedGifPanel.Visibility =
            preview && _generatedGif is not null && Flow.HasCurrentPreparedOutput ? Visibility.Visible : Visibility.Collapsed;
        SaveGeneratedGifButton.IsEnabled = Flow.CanSave;
        DiagnosticsButton.Visibility =
            stage == MainFlowStage.Set && Flow.LastApplyOutcome is not MainFlowApplyOutcome.None
                ? Visibility.Visible
                : Visibility.Collapsed;
        DiagnosticsButton.IsEnabled = !busy;
        SetModeWarning.IsOpen = stage == MainFlowStage.Set && _prerequisites?.PictureAndCache.Satisfied != true;
        SetModeWarning.Message =
            _prerequisites?.PictureAndCache.Detail
            ?? "Picture mode and the lock-screen cache could not be checked. Open Windows lock-screen settings and choose Picture.";
        UpdatePrerequisiteButtons();

        var showProgress = Flow.Operation is MainFlowOperation.Generating or MainFlowOperation.Applying or MainFlowOperation.Saving;
        OperationProgressPanel.Visibility = showProgress ? Visibility.Visible : Visibility.Collapsed;
        OperationProgressRing.IsActive = showProgress;
        OperationProgressText.Text = Flow.Operation switch
        {
            MainFlowOperation.Generating => "Preparing your GIF…",
            MainFlowOperation.Applying => "Setting your lock screen…",
            MainFlowOperation.Saving => "Saving your GIF…",
            _ => "",
        };
        GenerateLoading.Visibility = Flow.Operation == MainFlowOperation.Generating ? Visibility.Visible : Visibility.Collapsed;

        if (_displayedStage != stage)
        {
            if (stage != MainFlowStage.Edit)
            {
                SuspendEditorPlayback();
            }
            _displayedStage = stage;
            StageScrollViewer.ChangeView(null, 0, null);
            if (IsLoaded)
            {
                FocusStage();
            }
        }
    }

    private void UpdateStep(Button button, Border currentIndicator, TextBlock currentLabel, MainFlowStage stage, string label)
    {
        var current = Flow.Stage == stage;
        button.Content = currentLabel.Text = label;
        button.IsEnabled = Flow.CanNavigate(stage);
        button.Visibility = current ? Visibility.Collapsed : Visibility.Visible;
        currentIndicator.Visibility = current ? Visibility.Visible : Visibility.Collapsed;
    }

    private void FocusStage()
    {
        if (IsLoaded && !_isVideoLoading)
        {
            StageFocusTarget.Focus(FocusState.Programmatic);
        }
    }

    private void NavigateStage(MainFlowStage stage)
    {
        if (Flow.IsBusy || Flow.Stage == stage)
        {
            return;
        }
        if (Flow.Stage == MainFlowStage.Edit)
        {
            var valid = CommitTime(StartTimeTextBox) & CommitTime(EndTimeTextBox);
            if (stage == MainFlowStage.Set && !valid)
            {
                return;
            }
        }
        if (Flow.TryNavigate(stage))
        {
            OperationStatus.IsOpen = false;
            SyncDraftState();
            RefreshFlowUi();
        }
    }

    private void ChooseStep_Click(object sender, RoutedEventArgs e) => NavigateStage(MainFlowStage.Choose);

    private void EditStep_Click(object sender, RoutedEventArgs e) => NavigateStage(MainFlowStage.Edit);

    private void SetStep_Click(object sender, RoutedEventArgs e) => NavigateStage(MainFlowStage.Set);

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (!Flow.IsBusy && Flow.Stage == MainFlowStage.Edit)
        {
            CommitTime(StartTimeTextBox);
            CommitTime(EndTimeTextBox);
        }
        if (Flow.TryGoBack())
        {
            OperationStatus.IsOpen = false;
            SyncDraftState();
            RefreshFlowUi();
        }
    }

    private void ContinueSelection_Click(object sender, RoutedEventArgs e)
    {
        if (Flow.TryContinue())
        {
            OperationStatus.IsOpen = false;
            SyncDraftState();
            RefreshFlowUi();
        }
    }

    private void ReleaseVideoDraft()
    {
        SuspendEditorPlayback();
        _editorCts?.Cancel();
        _editorCts?.Dispose();
        _editorCts = null;
        ReleasePreviewPlayer();
        _videoFile = null;
        _frames = null;
        _previewCache.Clear();
        TrimTimeline.Clear();
        PreviewStill.Source = null;
    }

    private void OpenDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        if (!Flow.IsBusy)
        {
            App.GetService<INavigationService>().NavigateTo(typeof(DiagnosticsViewModel).FullName!, clearNavigation: true);
        }
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        if (!Flow.IsBusy)
        {
            App.GetService<INavigationService>().NavigateTo(typeof(SettingsViewModel).FullName!, clearNavigation: true);
        }
    }
}
