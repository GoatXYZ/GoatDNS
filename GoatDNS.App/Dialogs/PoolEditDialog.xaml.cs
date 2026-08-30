using GoatDNS.App.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace GoatDNS.App.Dialogs;

/// <summary>Add/edit a pool: name, strategy, and a checklist of server membership.</summary>
public sealed partial class PoolEditDialog : ContentDialog
{
    public PoolEditDialog(PoolItemViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public PoolItemViewModel ViewModel { get; }

    /// <summary>Shows the "add servers first" hint when there's nothing to pick.</summary>
    public bool HasNoChoices => ViewModel.ServerChoices.Count == 0;
}
