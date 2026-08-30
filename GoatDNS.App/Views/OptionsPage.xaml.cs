using GoatDNS.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace GoatDNS.App.Views;

/// <summary>Options page. File pickers (log path, config import/export) are handled here as a view concern.</summary>
public sealed partial class OptionsPage : Page
{
    public OptionsPage()
    {
        ViewModel = App.Vm.Options;
        InitializeComponent();
    }

    public OptionsViewModel ViewModel { get; }

    private async void BrowseLog_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
        picker.FileTypeChoices.Add("Log file", [".log", ".txt"]);
        picker.SuggestedFileName = "goatdns";

        var file = await picker.PickSaveFileAsync();
        if (file is not null) ViewModel.LogFilePath = file.Path;
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
        picker.FileTypeFilter.Add(".json");

        var file = await picker.PickSingleFileAsync();
        if (file is not null) ViewModel.ImportFrom(file.Path);
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
        picker.FileTypeChoices.Add("GoatDNS config", [".json"]);
        picker.SuggestedFileName = "goatdns-config";

        var file = await picker.PickSaveFileAsync();
        if (file is not null) ViewModel.ExportTo(file.Path);
    }
}
