using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GoatDNS.Core.Config;
using GoatDNS.Core.Ipc;

namespace GoatDNS.App.ViewModels;

/// <summary>A display row for the live log (pre-formatted time; kept immutable for cheap virtualization).</summary>
public sealed record LogRow(string Time, LogVerbosity Level, string Message);

/// <summary>
/// Backs the Log page: subscribes to the service's live log push and shows a filtered, bounded view.
/// A full buffer is kept so verbosity/text filter changes (and un-pausing) can re-project without
/// having to re-request history from the service.
/// </summary>
public partial class LogViewModel : ObservableObject
{
    // Matches the service ring so we never grow unbounded.
    private const int MaxRows = 2000;

    private readonly MainViewModel _main;
    private readonly List<LogRow> _buffer = [];
    private IDisposable? _subscription;

    public LogViewModel(MainViewModel main) => _main = main;

    /// <summary>The filtered rows shown by the (virtualized) ListView.</summary>
    public ObservableCollection<LogRow> Rows { get; } = [];

    [ObservableProperty] private bool _isPaused;

    /// <summary>Show entries at or below this verbosity (Core semantics: entry passes when Level &lt;= threshold).</summary>
    [ObservableProperty] private LogVerbosity _maxLevel = LogVerbosity.Debug;

    [ObservableProperty] private string _filterText = "";

    public IReadOnlyList<LogVerbosity> VerbosityOptions { get; } = Enum.GetValues<LogVerbosity>();

    /// <summary>Begins streaming. Idempotent-ish: only the first call wires the subscription.</summary>
    public void Start() => _subscription ??= _main.Ipc.SubscribeLog(OnPush);

    // Invoked on the UI thread (IpcClient marshals), so touching the collection is safe.
    private void OnPush(LogPush push)
    {
        var row = new LogRow(push.Time.ToLocalTime().ToString("HH:mm:ss.fff"), push.Level, push.Message);

        _buffer.Add(row);
        if (_buffer.Count > MaxRows) _buffer.RemoveAt(0);

        if (!IsPaused && Passes(row)) Append(row);
    }

    private void Append(LogRow row)
    {
        Rows.Add(row);
        if (Rows.Count > MaxRows) Rows.RemoveAt(0);
    }

    private bool Passes(LogRow row) =>
        row.Level <= MaxLevel &&
        (FilterText.Length == 0 || row.Message.Contains(FilterText, StringComparison.OrdinalIgnoreCase));

    partial void OnMaxLevelChanged(LogVerbosity value) => Rebuild();
    partial void OnFilterTextChanged(string value) => Rebuild();
    partial void OnIsPausedChanged(bool value)
    {
        // On un-pause, catch the view up with everything buffered while paused.
        if (!value) Rebuild();
    }

    private void Rebuild()
    {
        Rows.Clear();
        foreach (var r in _buffer.Where(Passes)) Rows.Add(r);
    }

    [RelayCommand]
    private void Clear()
    {
        _buffer.Clear();
        Rows.Clear();
    }
}
