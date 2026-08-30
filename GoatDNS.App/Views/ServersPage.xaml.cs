using GoatDNS.App.Dialogs;
using GoatDNS.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GoatDNS.App.Views;

/// <summary>
/// Servers list. Add/Edit go through a <see cref="ServerEditDialog"/> here in code-behind because
/// dialogs are a pure view concern (they need this page's <c>XamlRoot</c>); the view-model only
/// commits the resulting model.
/// </summary>
public sealed partial class ServersPage : Page
{
    public ServersPage()
    {
        ViewModel = App.Vm.Servers;
        InitializeComponent();
    }

    public ServersViewModel ViewModel { get; }

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        var draft = new ServerItemViewModel();
        var dialog = new ServerEditDialog(draft) { XamlRoot = XamlRoot };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            ViewModel.Commit(draft, original: null);
    }

    private Task EditAsync() => EditAsync(ViewModel.Selected);

    private async Task EditAsync(ServerItemViewModel? original)
    {
        if (original is null) return;
        var draft = original.Clone(); // edit a copy so Cancel discards changes
        var dialog = new ServerEditDialog(draft) { XamlRoot = XamlRoot };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            ViewModel.Commit(draft, original);
    }

    private async void Edit_Click(object sender, RoutedEventArgs e) => await EditAsync();

    private async void List_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
        => await EditAsync();
}
