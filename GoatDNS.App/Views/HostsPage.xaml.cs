using GoatDNS.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace GoatDNS.App.Views;

/// <summary>Hosts files, edited inline. The path picker is a view concern handled here.</summary>
public sealed partial class HostsPage : Page
{
    public HostsPage()
    {
        ViewModel = App.Vm.Hosts;
        InitializeComponent();
    }

    public HostsViewModel ViewModel { get; }

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Selected is not { } entry) return;

        var picker = new FileOpenPicker();
        // Unpackaged apps must parent WinRT pickers to the window HWND.
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
        picker.FileTypeFilter.Add("*");

        var file = await picker.PickSingleFileAsync();
        if (file is not null) entry.Path = file.Path;
    }
}
