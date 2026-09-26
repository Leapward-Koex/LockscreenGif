using CommunityToolkit.Mvvm.ComponentModel;
using LockscreenGif.Models.Diagnostics;
using Microsoft.UI.Xaml;

namespace LockscreenGif.ViewModels;

/// <summary>WinUI status and expansion state, kept separate from persisted diagnostic evidence.</summary>
public sealed class DiagnosticFindingViewModel : ObservableObject
{
    private bool _isExpanded;

    public DiagnosticFindingViewModel(DiagnosticFinding finding, bool isFile = false)
    {
        Title = isFile ? FileLabel(finding.Title) : finding.Title;
        Detail = finding.Detail;
        Children = finding.Children.Select(child => new DiagnosticFindingViewModel(child, isFile: true)).ToArray();
        var warning = finding.Severity is "Warning" or "Error" || Children.Any(child => child.WarningVisibility == Visibility.Visible);
        SuccessVisibility = !warning && finding.Severity == "Success" ? Visibility.Visible : Visibility.Collapsed;
        WarningVisibility = warning ? Visibility.Visible : Visibility.Collapsed;
        InformationVisibility = !warning && finding.Severity != "Success" ? Visibility.Visible : Visibility.Collapsed;
        var status =
            warning ? "Needs attention"
            : finding.Severity == "Success" ? "Passed"
            : "Information";
        StatusDescription = finding.Confidence == "Observed" ? status : $"{status} · {finding.Confidence}";
        AccessibleStatus = $"{status}: {Title}";
    }

    public string Title { get; }
    public string Detail { get; }
    public string StatusDescription { get; }
    public string AccessibleStatus { get; }
    public IReadOnlyList<DiagnosticFindingViewModel> Children { get; }
    public Visibility SuccessVisibility { get; }
    public Visibility WarningVisibility { get; }
    public Visibility InformationVisibility { get; }
    public Visibility GroupVisibility => Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility LeafVisibility => Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    private static string FileLabel(string path)
    {
        var directory = Path.GetFileName(Path.GetDirectoryName(path));
        return string.IsNullOrWhiteSpace(directory) ? Path.GetFileName(path) : $"{directory} / {Path.GetFileName(path)}";
    }
}
