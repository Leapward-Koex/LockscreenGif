using CommunityToolkit.Mvvm.ComponentModel;
using LockscreenGif.Models;

namespace LockscreenGif.ViewModels;

public partial class MainViewModel : ObservableRecipient
{
    public MainFlowState Flow { get; } = new();
}
