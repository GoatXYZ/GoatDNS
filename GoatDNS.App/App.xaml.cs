using GoatDNS.App.Services;
using GoatDNS.App.ViewModels;
using Microsoft.UI.Xaml;

namespace GoatDNS.App;

/// <summary>
/// App entry point. Creates the single <see cref="MainViewModel"/> (and its <see cref="IpcClient"/>)
/// on the UI thread so the dispatcher captured there is correct, then shows the main window.
/// The window itself owns the tray icon and hide-to-tray behaviour.
/// </summary>
public partial class App : Application
{
    private Window? _window;
    private MainViewModel? _main;

    public App() => InitializeComponent();

    private static App Instance => (App)Current;

    /// <summary>The shared root view-model, resolved by pages (no DI container needed for one window).</summary>
    public static MainViewModel Vm => Instance._main!;

    /// <summary>HWND of the main window, needed to parent unpackaged file pickers.</summary>
    public static IntPtr WindowHandle { get; private set; }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _main = new MainViewModel(new IpcClient());

        var window = new MainWindow();
        _window = window;
        WindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(window);

        window.Activate();
        window.Start(); // create the tray icon and begin polling/streaming
    }
}
