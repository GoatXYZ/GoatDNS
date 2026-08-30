using CommunityToolkit.Mvvm.ComponentModel;
using GoatDNS.App.Services;
using GoatDNS.Core.Config;

namespace GoatDNS.App.ViewModels;

/// <summary>
/// Backs the Options page: the non-list parts of the config (enabled, port, block mode, logging)
/// plus machine-level toggles (start-with-Windows) and config import/export. Everything here except
/// start-with-Windows is part of the working config and only reaches the service on Apply.
/// </summary>
public partial class OptionsViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    public OptionsViewModel(MainViewModel main)
    {
        _main = main;
        // Reflect the real registry state on load rather than defaulting to false.
        _startWithWindows = StartupRegistry.IsEnabled();
    }

    [ObservableProperty] private bool _enabled = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ListenPortValue))]
    private int _listenPort = 53535;

    /// <summary>double-typed bridge for NumberBox (x:Bind does no implicit int/double conversion).</summary>
    public double ListenPortValue
    {
        get => ListenPort;
        set => ListenPort = double.IsNaN(value) ? ListenPort : (int)value;
    }

    [ObservableProperty] private BlockResponseMode _blockResponse = BlockResponseMode.NxDomain;
    [ObservableProperty] private LogVerbosity _screenVerbosity = LogVerbosity.Normal;
    [ObservableProperty] private LogVerbosity _fileVerbosity = LogVerbosity.ErrorsOnly;
    [ObservableProperty] private string? _logFilePath;
    [ObservableProperty] private bool _startWithWindows;

    /// <summary>Transient feedback for import/export/startup surfaced in an InfoBar.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusShowError))]
    [NotifyPropertyChangedFor(nameof(StatusShowInfo))]
    private string? _statusMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusShowError))]
    [NotifyPropertyChangedFor(nameof(StatusShowInfo))]
    private bool _statusIsError;

    public bool StatusShowError => !string.IsNullOrEmpty(StatusMessage) && StatusIsError;
    public bool StatusShowInfo => !string.IsNullOrEmpty(StatusMessage) && !StatusIsError;

    public IReadOnlyList<BlockResponseMode> BlockResponseOptions { get; } = Enum.GetValues<BlockResponseMode>();
    public IReadOnlyList<LogVerbosity> VerbosityOptions { get; } = Enum.GetValues<LogVerbosity>();

    public void Load(GoatConfig c)
    {
        Enabled = c.Enabled;
        ListenPort = c.ListenPort;
        BlockResponse = c.BlockResponse;
        ScreenVerbosity = c.Logging.ScreenVerbosity;
        FileVerbosity = c.Logging.FileVerbosity;
        LogFilePath = c.Logging.FilePath;
    }

    public LoggingOptions ToLoggingOptions() => new()
    {
        ScreenVerbosity = ScreenVerbosity,
        FileVerbosity = FileVerbosity,
        FilePath = TextLists.Blank(LogFilePath),
    };

    /// <summary>Writing the Run key is a machine setting, applied immediately (not part of Apply).</summary>
    partial void OnStartWithWindowsChanged(bool value)
    {
        try
        {
            StartupRegistry.Set(value);
        }
        catch (Exception ex)
        {
            SetStatus($"Couldn't update startup setting: {ex.Message}", isError: true);
        }
    }

    /// <summary>Loads a config file into the working copy (does not push to the service until Apply).</summary>
    public void ImportFrom(string path)
    {
        try
        {
            _main.ImportConfigFromFile(path);
            SetStatus($"Imported '{path}'. Review, then Apply.", isError: false);
        }
        catch (Exception ex)
        {
            SetStatus($"Import failed: {ex.Message}", isError: true);
        }
    }

    /// <summary>Writes the current working copy to a config file.</summary>
    public void ExportTo(string path)
    {
        try
        {
            _main.ExportConfigToFile(path);
            SetStatus($"Exported to '{path}'.", isError: false);
        }
        catch (Exception ex)
        {
            SetStatus($"Export failed: {ex.Message}", isError: true);
        }
    }

    private void SetStatus(string message, bool isError)
    {
        StatusIsError = isError;
        StatusMessage = message;
    }
}
