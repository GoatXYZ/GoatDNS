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

    // ---- Row context menu: select the right-clicked row, then reuse the toolbar actions ----

    private ServerItemViewModel? MenuItemTarget(object sender) =>
        (sender as FrameworkElement)?.DataContext as ServerItemViewModel;

    private async void RowEdit_Click(object sender, RoutedEventArgs e)
    {
        if (MenuItemTarget(sender) is { } item) { ViewModel.Selected = item; await EditAsync(item); }
    }

    private void RowTest_Click(object sender, RoutedEventArgs e)
    {
        if (MenuItemTarget(sender) is { } item) { ViewModel.Selected = item; ViewModel.TestSelectedCommand.Execute(null); }
    }

    private void RowDelete_Click(object sender, RoutedEventArgs e)
    {
        if (MenuItemTarget(sender) is { } item) { ViewModel.Selected = item; ViewModel.DeleteCommand.Execute(null); }
    }

    /// <summary>Import menu → sdns:// stamp: prompt for the stamp, then hand it to the view-model.</summary>
    private async void ImportStamp_Click(object sender, RoutedEventArgs e)
    {
        var box = new TextBox { PlaceholderText = "sdns://…", Width = 440 };
        var dialog = new ContentDialog
        {
            Title = "Import from stamp",
            Content = box,
            PrimaryButtonText = "Import",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            ViewModel.StampInput = box.Text;
            ViewModel.ImportStampCommand.Execute(null);
        }
    }
}
