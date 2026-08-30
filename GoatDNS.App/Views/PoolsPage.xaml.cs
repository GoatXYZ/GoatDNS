using GoatDNS.App.Dialogs;
using GoatDNS.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GoatDNS.App.Views;

/// <summary>Pools list; add/edit uses a dialog that multi-selects server membership.</summary>
public sealed partial class PoolsPage : Page
{
    public PoolsPage()
    {
        ViewModel = App.Vm.Pools;
        InitializeComponent();
    }

    public PoolsViewModel ViewModel { get; }

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        var draft = new PoolItemViewModel();
        draft.BeginEdit(ViewModel.AllServerNames);
        if (await ShowAsync(draft)) { draft.EndEdit(); ViewModel.Commit(draft, original: null); }
    }

    private async void Edit_Click(object sender, RoutedEventArgs e) => await EditAsync();

    private async void List_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
        => await EditAsync();

    private async Task EditAsync()
    {
        if (ViewModel.Selected is not { } original) return;
        var draft = original.Clone();
        draft.BeginEdit(ViewModel.AllServerNames);
        if (await ShowAsync(draft)) { draft.EndEdit(); ViewModel.Commit(draft, original); }
    }

    private async Task<bool> ShowAsync(PoolItemViewModel draft)
    {
        var dialog = new PoolEditDialog(draft) { XamlRoot = XamlRoot };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
