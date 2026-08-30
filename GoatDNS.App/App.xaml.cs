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
        _main = new MainViewModel(SelectBackend());

        var window = new MainWindow();
        _window = window;
        WindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(window);

        window.Activate();
        window.Start(); // create the tray icon and begin polling/streaming
    }

    /// <summary>
    /// Chooses how interception runs: the background service if it's running, otherwise in-process
    /// "DNS mode" when we're elevated. Unelevated with no service falls back to the IPC client, whose
    /// "service not running" state lets the user install the service or relaunch in DNS mode.
    /// <c>--dnsmode</c> forces in-process (the elevated relaunch passes it).
    /// </summary>
    private static IBackend SelectBackend()
    {
        bool forceDnsMode = Environment.GetCommandLineArgs().Contains("--dnsmode");
        if (!forceDnsMode && ServiceControl.Query() == ServiceState.Running)
            return new IpcClient();
        if (ServiceControl.IsElevated)
            return new InProcessBackend();
        return new IpcClient();
    }
}
