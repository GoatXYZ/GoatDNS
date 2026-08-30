using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GoatDNS.App.Services;
using GoatDNS.Core.Config;
using Microsoft.UI.Dispatching;

namespace GoatDNS.App.ViewModels;

/// <summary>
/// Root view-model. Owns the <see cref="IpcClient"/> and the child page view-models, which together
/// hold the *working copy* of the config. Nothing is sent to the service until <see cref="ApplyAsync"/>;
/// the status timer only refreshes read-only status, so it never clobbers in-progress edits.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private const int StatusPollSeconds = 3;

    private readonly DispatcherQueueTimer _statusTimer;

    // Load the config from the service exactly once (the first time it's reachable). After that the
    // working copy is the user's to edit; only Reload/Import overwrite it.
    private bool _configLoaded;

    /// <summary>The active backend — either the Windows service (over IPC) or an in-process DNS-mode host.</summary>
    public IBackend Backend { get; }

    /// <summary>True when interception runs in this app process (DNS mode) rather than the service.</summary>
    public bool IsDnsMode => Backend.IsLocal;

    public string ModeLabel => IsDnsMode ? "DNS mode (this app)" : "Windows service";

    public ServersViewModel Servers { get; }
    public PoolsViewModel Pools { get; }
    public RulesViewModel Rules { get; }
    public HostsViewModel Hosts { get; }
    public LogViewModel Log { get; }
    public OptionsViewModel Options { get; }

    /// <summary>False whenever the last IPC attempt failed; drives the "service not running" banner.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ServiceDown))]
    private bool _isServiceAvailable;

    /// <summary>One-line status shown in the status bar.</summary>
    [ObservableProperty] private string _statusSummary = "Connecting to service…";

    /// <summary>Mirrors ServiceStatus.Enabled; the toggle button and tray label key off it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InterceptionMenuLabel))]
    private bool _interceptionEnabled;

    [ObservableProperty] private bool _isBusy;

    /// <summary>Windows-service install/run state ("Not installed" / "Stopped" / "Running"), shown in Options.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ServiceInstalled))]
    [NotifyPropertyChangedFor(nameof(ServiceRunning))]
    private string _serviceStateText = "Checking…";

    /// <summary>True when this app is already admin, so service actions skip the UAC prompt.</summary>
    public bool IsElevated => ServiceControl.IsElevated;

    public bool ServiceInstalled => ServiceStateText is "Stopped" or "Running";
    public bool ServiceRunning => ServiceStateText == "Running";

    /// <summary>Last error from Apply/Reload/Toggle, surfaced in a closable InfoBar.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _lastError;

    public bool ServiceDown => !IsServiceAvailable;
    public bool HasError => !string.IsNullOrEmpty(LastError);
    public string InterceptionMenuLabel => InterceptionEnabled ? "Disable interception" : "Enable interception";

    public MainViewModel(IBackend backend)
    {
        Backend = backend;

        // Children hold back-references so they can read sibling data (e.g. Pools needs server names).
        Servers = new ServersViewModel(this);
        Pools = new PoolsViewModel(this);
        Rules = new RulesViewModel(this);
        Hosts = new HostsViewModel(this);
        Options = new OptionsViewModel(this);
        Log = new LogViewModel(this);

        // Constructed on the UI thread (App.OnLaunched), so the current-thread dispatcher is present.
        _statusTimer = DispatcherQueue.GetForCurrentThread()!.CreateTimer();
        _statusTimer.Interval = TimeSpan.FromSeconds(StatusPollSeconds);
        _statusTimer.Tick += (_, _) => _ = RefreshStatusAsync();
    }

    /// <summary>Called once by the window after it loads: start polling and the log stream.</summary>
    public void Start() => _ = InitializeAndStartAsync();

    private async Task InitializeAndStartAsync()
    {
        // In DNS mode this loads the shared config and starts interception in-process; over IPC it's a no-op.
        try { await Backend.InitializeAsync(); }
        catch (Exception ex) { LastError = ex.Message; }

        Log.Start();
        _statusTimer.Start();
        await RefreshStatusAsync();
    }

    private async Task RefreshStatusAsync()
    {
        // sc.exe query is blocking; keep it off the UI thread. Back on the UI context after await.
        ServiceStateText = await Task.Run(() => ServiceControl.Query()) switch
        {
            Services.ServiceState.NotInstalled => "Not installed",
            Services.ServiceState.Stopped => "Stopped",
            Services.ServiceState.Running => "Running",
            _ => "Unknown",
        };

        try
        {
            var status = await Backend.GetStatusAsync();
            IsServiceAvailable = true;
            InterceptionEnabled = status.Enabled;
            var mode = Backend.IsLocal ? "DNS mode (this app)" : "Service";
            StatusSummary = status.Enabled
                ? $"{mode} · {status.CaptureProvider} · {(status.CaptureActive ? "capturing" : "idle")} · port {status.ListenPort} · {status.QueriesHandled:N0} queries · v{status.Version}"
                : $"{mode} · interception disabled";
            if (status.LastError is { Length: > 0 } err) StatusSummary += $" · {err}";

            // First reachable moment: pull the config into the working copy (non-destructive thereafter).
            if (!_configLoaded) await LoadConfigFromServiceAsync();
        }
        catch (IpcUnavailableException)
        {
            IsServiceAvailable = false;
            StatusSummary = "Service not running";
        }
    }

    /// <summary>Re-reads the config from the service, replacing the working copy.</summary>
    public async Task LoadConfigFromServiceAsync()
    {
        var config = await Backend.GetConfigAsync();
        LoadConfig(config);
        _configLoaded = true;
    }

    /// <summary>Distributes a config across the child view-models.</summary>
    public void LoadConfig(GoatConfig config)
    {
        Servers.Load(config.Servers);
        Pools.Load(config.Pools);
        Rules.Load(config.Rules);
        Hosts.Load(config.HostsFiles);
        Options.Load(config);
    }

    /// <summary>Assembles a fresh <see cref="GoatConfig"/> from the working copy in the child view-models.</summary>
    public GoatConfig BuildConfig() => new()
    {
        Enabled = Options.Enabled,
        ListenPort = Options.ListenPort,
        BlockResponse = Options.BlockResponse,
        Servers = Servers.Items.Select(i => i.ToModel()).ToList(),
        Pools = Pools.Items.Select(i => i.ToModel()).ToList(),
        Rules = Rules.Items.Select(i => i.ToModel()).ToList(),
        HostsFiles = Hosts.Items.Select(i => i.ToModel()).ToList(),
        Logging = Options.ToLoggingOptions(),
    };

    public void ImportConfigFromFile(string path)
    {
        LoadConfig(GoatConfig.FromJson(File.ReadAllText(path)));
        _configLoaded = true; // treat as loaded so the status poll won't overwrite the import
    }

    public void ExportConfigToFile(string path) => File.WriteAllText(path, BuildConfig().ToJson());

    /// <summary>Validates the working copy and pushes it to the service. Exposed so children can trigger it.</summary>
    [RelayCommand]
    public async Task ApplyAsync()
    {
        LastError = null;
        IsBusy = true;
        try
        {
            var config = BuildConfig();
            config.Validate(); // fail fast with a readable message before the round-trip
            await Backend.ApplyConfigAsync(config);
            await RefreshStatusAsync();
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ReloadAsync()
    {
        LastError = null;
        IsBusy = true;
        try
        {
            await LoadConfigFromServiceAsync();
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ToggleInterceptionAsync()
    {
        LastError = null;
        bool target = !InterceptionEnabled;
        try
        {
            await Backend.SetEnabledAsync(target);
            Options.Enabled = target; // keep the Options page's persisted toggle in sync with the live state
            await RefreshStatusAsync();
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    [RelayCommand]
    private void DismissError() => LastError = null;

    /// <summary>Enter in-process DNS mode: relaunch elevated with --dnsmode (UAC) and close this instance.</summary>
    [RelayCommand]
    private void StartDnsMode()
    {
        if (ServiceControl.RelaunchElevated("--dnsmode"))
            Microsoft.UI.Xaml.Application.Current.Exit();
        else
            LastError = "Elevation was cancelled; DNS mode needs administrator rights.";
    }

    public bool CanStartDnsMode => !IsDnsMode;

    // ---- Windows service control (self-elevates via UAC when the app isn't already admin) ----

    [RelayCommand] private Task StartService() => RunServiceActionAsync("start");
    [RelayCommand] private Task StopService() => RunServiceActionAsync("stop");
    [RelayCommand] private Task RestartService() => RunServiceActionAsync("restart");
    [RelayCommand] private Task InstallService() => RunServiceActionAsync("install");
    [RelayCommand] private Task UninstallService() => RunServiceActionAsync("uninstall");

    private async Task RunServiceActionAsync(string action)
    {
        LastError = null;
        IsBusy = true;
        try
        {
            // EnsureAction blocks (it may spawn an elevated child and wait for the UAC flow).
            var (ok, message) = await Task.Run(() => ServiceControl.EnsureAction(action));
            if (!ok) LastError = message;
            await Task.Delay(500); // give the SCM a moment to settle before we re-query
            await RefreshStatusAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }
}
