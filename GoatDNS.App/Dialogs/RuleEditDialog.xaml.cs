using GoatDNS.App.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace GoatDNS.App.Dialogs;

/// <summary>Add/edit a rule: matching (hosts/processes/interface), action, pool target, and DNSSEC.</summary>
public sealed partial class RuleEditDialog : ContentDialog
{
    public RuleEditDialog(RuleItemViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public RuleItemViewModel ViewModel { get; }
}
