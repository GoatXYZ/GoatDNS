using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GoatDNS.Core.Config;
using GoatDNS.Core.Import;
using GoatDNS.Core.Stamps;

namespace GoatDNS.App.ViewModels;

/// <summary>Backs the Servers page: the working list of servers plus test / stamp-import actions.</summary>
public partial class ServersViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    public ServersViewModel(MainViewModel main) => _main = main;

    /// <summary>Working copy of the server list; only pushed to the service on Apply.</summary>
    public ObservableCollection<ServerItemViewModel> Items { get; } = [];

    /// <summary>Filtered + sorted projection of <see cref="Items"/> that the table actually binds to.</summary>
    public ObservableCollection<ServerItemViewModel> Visible { get; } = [];

    [ObservableProperty] private ServerItemViewModel? _selected;

    // Mirrors the table's SelectedItems (multi-select). Actions run over the whole selection;
    // Selected stays the single-row fallback for programmatic callers (Commit, Load, import).
    private readonly List<ServerItemViewModel> _selection = [];

    /// <summary>Live filter text (matches name / protocol / address).</summary>
    [ObservableProperty] private string _filter = "";

    /// <summary>Which column the table is sorted by, and the direction — driven by header clicks.</summary>
    [ObservableProperty] private string _sortKey = "Name";
    [ObservableProperty] private bool _sortDescending;

    // Column header captions carry the active sort arrow.
    public string NameHeader => "Name" + Arrow("Name");
    public string ProtocolHeader => "Protocol" + Arrow("Protocol");
    public string EndpointHeader => "Address / URL" + Arrow("Endpoint");
    public string CheckHeader => "Check" + Arrow("Check");
    private string Arrow(string key) => SortKey == key ? (SortDescending ? "  ▼" : "  ▲") : "";

    /// <summary>Text box holding an <c>sdns://</c> stamp to import.</summary>
    [ObservableProperty] private string _stampInput = "";

    // Test / import feedback, surfaced in an InfoBar. Two computed flags let the view pick a
    // severity without the view-model referencing WinUI's InfoBarSeverity enum.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResultShowError))]
    [NotifyPropertyChangedFor(nameof(ResultShowInfo))]
    private bool _resultOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResultShowError))]
    [NotifyPropertyChangedFor(nameof(ResultShowInfo))]
    private bool _resultIsError;

    [ObservableProperty] private string _resultMessage = "";
    [ObservableProperty] private bool _isTesting;

    public bool ResultShowError => ResultOpen && ResultIsError;
    public bool ResultShowInfo => ResultOpen && !ResultIsError;

    public void Load(IEnumerable<ServerDefinition> servers)
    {
        Items.Clear();
        foreach (var s in servers) Items.Add(new ServerItemViewModel(s));
        Selected = Items.FirstOrDefault();
        RefreshView();
    }

    // ---- Filter + sort projection (Items -> Visible) ----

    partial void OnFilterChanged(string value) => RefreshView();
    partial void OnSortKeyChanged(string value) { NotifyHeaders(); RefreshView(); }
    partial void OnSortDescendingChanged(bool value) { NotifyHeaders(); RefreshView(); }

    private void NotifyHeaders()
    {
        OnPropertyChanged(nameof(NameHeader));
        OnPropertyChanged(nameof(ProtocolHeader));
        OnPropertyChanged(nameof(EndpointHeader));
        OnPropertyChanged(nameof(CheckHeader));
    }

    /// <summary>Clicking a column header sorts by it; clicking the active column flips the direction.</summary>
    [RelayCommand]
    private void Sort(string key)
    {
        if (SortKey == key) SortDescending = !SortDescending;
        else { SortKey = key; SortDescending = false; }
    }

    private bool Matches(ServerItemViewModel s) =>
        Filter.Length == 0
        || s.Name.Contains(Filter, StringComparison.OrdinalIgnoreCase)
        || s.ProtocolLabel.Contains(Filter, StringComparison.OrdinalIgnoreCase)
        || s.Endpoint.Contains(Filter, StringComparison.OrdinalIgnoreCase);

    private void RefreshView()
    {
        var filtered = Items.Where(Matches);
        IEnumerable<ServerItemViewModel> ordered;
        if (SortKey == "Check")
        {
            // Latency sorts numerically, not as text; untested/failed rows sink to the bottom either way.
            ordered = SortDescending
                ? filtered.OrderByDescending(s => s.LatencyMs ?? -1)
                : filtered.OrderBy(s => s.LatencyMs ?? int.MaxValue);
        }
        else
        {
            Func<ServerItemViewModel, string> key = SortKey switch
            {
                "Protocol" => s => s.ProtocolLabel,
                "Endpoint" => s => s.Endpoint,
                _ => s => s.Name,
            };
            ordered = SortDescending
                ? filtered.OrderByDescending(key, StringComparer.OrdinalIgnoreCase)
                : filtered.OrderBy(key, StringComparer.OrdinalIgnoreCase);
        }
        ListProjection.Reproject(Visible, ordered, () => Selected, v => Selected = v);
    }

    /// <summary>Adds a new server (original null) or replaces an edited one in place.</summary>
    public void Commit(ServerItemViewModel edited, ServerItemViewModel? original)
    {
        if (original is null)
        {
            Items.Add(edited);
        }
        else
        {
            int i = Items.IndexOf(original);
            if (i >= 0) Items[i] = edited; else Items.Add(edited);
        }
        Selected = edited;
        RefreshView();
    }

    /// <summary>Called by the page whenever the table's selection changes.</summary>
    public void SetSelection(IEnumerable<ServerItemViewModel> items)
    {
        _selection.Clear();
        _selection.AddRange(items);
        DeleteCommand.NotifyCanExecuteChanged();
        TestSelectedCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Rows the Delete/Test actions apply to: the selection, or Selected when it is empty.</summary>
    private List<ServerItemViewModel> Targets =>
        _selection.Count > 0 ? [.. _selection] : Selected is { } s ? [s] : [];

    private bool HasSelection() => _selection.Count > 0 || Selected is not null;

    partial void OnSelectedChanged(ServerItemViewModel? value)
    {
        DeleteCommand.NotifyCanExecuteChanged();
        TestSelectedCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Delete()
    {
        var targets = Targets;
        if (targets.Count == 0) return;
        foreach (var s in targets) Items.Remove(s);
        Selected = Items.FirstOrDefault();
        RefreshView();
    }

    /// <summary>
    /// Probes every selected server via the service, colouring each green/red and filling its Check
    /// column. Note: the service resolves the name against its *applied* config, so a server that
    /// hasn't been Applied yet returns "No server named …" — which we treat as "unknown"
    /// (uncoloured), not a failure.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task TestSelectedAsync()
    {
        var targets = Targets;
        if (targets.Count == 0) return;
        IsTesting = true;
        ResultOpen = false;
        try
        {
            if (targets.Count == 1)
            {
                var (ok, message) = await ProbeAsync(targets[0]);
                ResultMessage = message;
                ResultIsError = !ok;
            }
            else
            {
                var (ok, failed) = await ProbeManyAsync(targets);
                ResultMessage = $"Tested {targets.Count} server(s): {ok} OK, {failed} failed.";
                ResultIsError = ok == 0 && failed > 0;
            }
        }
        finally
        {
            IsTesting = false;
            ResultOpen = true;
        }
    }

    /// <summary>Tests every server in turn, colouring each by the result (small lists only; sequential).</summary>
    [RelayCommand]
    private async Task TestAllAsync()
    {
        if (Items.Count == 0) return;
        IsTesting = true;
        ResultOpen = false;
        try
        {
            var (ok, failed) = await ProbeManyAsync(Items.ToList());
            ShowResult($"Tested {Items.Count} server(s): {ok} OK, {failed} failed.", isError: ok == 0 && failed > 0);
        }
        finally
        {
            IsTesting = false;
        }
    }

    // Sequential on purpose: probing in parallel would open every upstream at once.
    private async Task<(int Ok, int Failed)> ProbeManyAsync(IEnumerable<ServerItemViewModel> servers)
    {
        int ok = 0, failed = 0;
        foreach (var s in servers)
        {
            var (passed, _) = await ProbeAsync(s);
            if (passed) ok++;
            else if (s.Health == ServerHealth.Failed) failed++; // skip "not applied yet"
        }
        return (ok, failed);
    }

    // Tests one server and sets its Health. "No server named" means it isn't Applied yet (not a
    // failure), so we leave that row uncoloured. Never throws — returns the message to show.
    private async Task<(bool Ok, string Message)> ProbeAsync(ServerItemViewModel s)
    {
        s.IsChecking = true;
        try
        {
            var message = await _main.Backend.TestServerAsync(s.Name);
            s.Health = ServerHealth.Ok;
            s.LatencyMs = ParseLatency(message);
            return (true, message);
        }
        catch (Exception ex)
        {
            if (!ex.Message.Contains("No server named", StringComparison.OrdinalIgnoreCase))
                s.Health = ServerHealth.Failed;
            s.LatencyMs = null;
            return (false, ex.Message);
        }
        finally
        {
            s.IsChecking = false;
        }
    }

    // The probe already times the round trip service-side and formats it into its OK message
    // ("… in 42 ms"), so read the number back out of that rather than widening the IPC contract.
    // ponytail: format-coupled to GoatDnsHost.TestServerAsync; give the IPC a typed result if a
    // second caller ever needs the number.
    private static int? ParseLatency(string message)
    {
        int start = message.LastIndexOf(" in ", StringComparison.Ordinal);
        int end = message.LastIndexOf(" ms", StringComparison.Ordinal);
        return start >= 0 && end > start + 4 && int.TryParse(message.AsSpan(start + 4, end - start - 4), out int ms)
            ? ms
            : null;
    }

    [RelayCommand]
    private void ImportStamp()
    {
        if (!DnsStamp.TryParse(StampInput.Trim(), out var stamp) || stamp is null)
        {
            ShowResult("Not a valid sdns:// stamp.", isError: true);
            return;
        }
        try
        {
            var vm = ServerItemViewModel.FromStamp(stamp);
            Items.Add(vm);
            Selected = vm;
            StampInput = "";
            RefreshView();
            ShowResult($"Imported '{vm.Name}'. Review, then Apply.", isError: false);
        }
        catch (Exception ex)
        {
            ShowResult(ex.Message, isError: true);
        }
    }

    // ---- Bundled resolver-list import (vendored + rehosted by us; work fully offline) ----

    [RelayCommand] private Task ImportStarter() => ImportBundledAsync("starter.md", "starter list");
    [RelayCommand] private Task ImportPublicResolvers() => ImportBundledAsync("public-resolvers.md", "public resolvers list");

    private async Task ImportBundledAsync(string file, string label)
    {
        try
        {
            var result = await Task.Run(() => ServerImporter.ImportFromBundled(file));
            if (result.Servers.Count == 0)
            {
                ShowResult($"No entries found — is resolvers\\{file} present next to the app?", isError: true);
                return;
            }

            var existing = Items.Select(i => i.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            int added = 0;
            foreach (var def in result.Servers)
            {
                if (!existing.Add(def.Name)) continue; // skip names already in the working list
                Items.Add(new ServerItemViewModel(def));
                added++;
            }
            Selected ??= Items.FirstOrDefault();
            RefreshView();
            ShowResult($"Imported {added} server(s) from the {label}. Review, then Apply.", isError: false);
        }
        catch (Exception ex)
        {
            ShowResult(ex.Message, isError: true);
        }
    }

    private void ShowResult(string message, bool isError)
    {
        ResultMessage = message;
        ResultIsError = isError;
        ResultOpen = true;
    }
}
