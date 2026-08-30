using GoatDNS.App.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace GoatDNS.App.Dialogs;

/// <summary>Add/edit a server. The view-model is injected before InitializeComponent so compiled bindings see it.</summary>
public sealed partial class ServerEditDialog : ContentDialog
{
    public ServerEditDialog(ServerItemViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public ServerItemViewModel ViewModel { get; }
}
