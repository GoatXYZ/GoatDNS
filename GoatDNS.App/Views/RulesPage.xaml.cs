using GoatDNS.App.Dialogs;
using GoatDNS.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GoatDNS.App.Views;

/// <summary>Rules list; add/edit uses a dialog populated with the current pools/servers/hosts-files.</summary>
public sealed partial class RulesPage : Page
{
    public RulesPage()
    {
        ViewModel = App.Vm.Rules;
        InitializeComponent();
    }

    public RulesViewModel ViewModel { get; }

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        var draft = new RuleItemViewModel();
        draft.BeginEdit(ViewModel.PoolAndServerTargets, ViewModel.HostsFileNames);
        if (await ShowAsync(draft)) { draft.EndEdit(); ViewModel.Commit(draft, original: null); }
    }

    private async void Edit_Click(object sender, RoutedEventArgs e) => await EditAsync();

    private async void List_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
        => await EditAsync();

    private async Task EditAsync()
    {
        if (ViewModel.Selected is not { } original) return;
        var draft = original.Clone();
        draft.BeginEdit(ViewModel.PoolAndServerTargets, ViewModel.HostsFileNames);
        if (await ShowAsync(draft)) { draft.EndEdit(); ViewModel.Commit(draft, original); }
    }

    private async Task<bool> ShowAsync(RuleItemViewModel draft)
    {
        var dialog = new RuleEditDialog(draft) { XamlRoot = XamlRoot };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    // ---- Row context menu: select the right-clicked row, then reuse the toolbar actions ----

    private RuleItemViewModel? MenuItemTarget(object sender) =>
        (sender as FrameworkElement)?.DataContext as RuleItemViewModel;

    private async void RowEdit_Click(object sender, RoutedEventArgs e)
    {
        if (MenuItemTarget(sender) is { } item) { ViewModel.Selected = item; await EditAsync(); }
    }

    private void RowClone_Click(object sender, RoutedEventArgs e)
    {
        if (MenuItemTarget(sender) is { } item) { ViewModel.Selected = item; ViewModel.CloneCommand.Execute(null); }
    }

    private void RowDelete_Click(object sender, RoutedEventArgs e)
    {
        if (MenuItemTarget(sender) is { } item) { ViewModel.Selected = item; ViewModel.DeleteCommand.Execute(null); }
    }
}
