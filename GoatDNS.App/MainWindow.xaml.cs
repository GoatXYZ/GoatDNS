using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using GoatDNS.App.ViewModels;
using GoatDNS.App.Views;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace GoatDNS.App;

/// <summary>
/// Shell window: NavigationView + page host + status bar, and the owner of the tray icon.
/// Closing the window hides it to the tray; real exit goes through the tray's Exit item.
/// </summary>
public sealed partial class MainWindow : Window
{
    // Set true only when the user chooses Exit, so the Closing handler knows to actually close.
    private bool _exiting;

    public MainWindow()
    {
        ViewModel = App.Vm;
        ShowFromTrayCommand = new RelayCommand(ShowFromTray);
        InitializeComponent();

        AppWindow.Resize(new SizeInt32 { Width = 1100, Height = 760 });

        // Unpackaged apps don't reliably pick up the exe icon for the title bar; set it explicitly.
        var icon = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "goatdns-app-icon.ico");
        if (System.IO.File.Exists(icon)) AppWindow.SetIcon(icon);

        // Intercept the close box: hide to tray instead of exiting.
        AppWindow.Closing += OnAppWindowClosing;

        // Land on the first page.
        ContentFrame.Navigate(typeof(ServersPage));
        Nav.SelectedItem = Nav.MenuItems[0];
    }

    public MainViewModel ViewModel { get; }

    /// <summary>Left-clicking the tray icon reopens the window (bound in XAML).</summary>
    public ICommand ShowFromTrayCommand { get; }

    /// <summary>Called by <see cref="App"/> after Activate: realize the tray icon and start the view-model.</summary>
    public void Start()
    {
        TrayIcon.ForceCreate();
        ViewModel.ExitRequested += ExitApp; // Quit (stop service + exit) is driven from the view-model
        ViewModel.Start();
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_exiting) return;
        args.Cancel = true;      // keep the process (and the log stream) alive in the tray
        AppWindow.Hide();
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem { Tag: string tag }) return;

        Type? page = tag switch
        {
            "Servers" => typeof(ServersPage),
            "Pools" => typeof(PoolsPage),
            "Rules" => typeof(RulesPage),
            "Hosts" => typeof(HostsPage),
            "Log" => typeof(LogPage),
            "Options" => typeof(OptionsPage),
            _ => null,
        };

        // Avoid a redundant re-navigation when the constructor already put us on the page.
        if (page is not null && ContentFrame.CurrentSourcePageType != page)
            ContentFrame.Navigate(page);
    }

    private void ErrorBar_CloseButtonClick(InfoBar sender, object args) => ViewModel.DismissErrorCommand.Execute(null);

    private void TrayToggle_Click(object sender, RoutedEventArgs e) => ViewModel.ToggleInterceptionCommand.Execute(null);

    private void TrayOpen_Click(object sender, RoutedEventArgs e) => ShowFromTray();

    /// <summary>Tray "Quit": stop the service/interception, then exit (routed through the view-model).</summary>
    private void TrayQuit_Click(object sender, RoutedEventArgs e) => ViewModel.QuitCommand.Execute(null);

    /// <summary>Tray "Hide": just close the window to the tray (interception keeps running).</summary>
    private void TrayExit_Click(object sender, RoutedEventArgs e) => ExitApp();

    /// <summary>Actually tear down the app: drop the tray icon and close the window for real.</summary>
    private void ExitApp()
    {
        _exiting = true;
        TrayIcon.Dispose(); // remove the tray icon before the process goes away
        Close();
    }

    private void ShowFromTray()
    {
        AppWindow.Show();
        Activate();
    }
}
