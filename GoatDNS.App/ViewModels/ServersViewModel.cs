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

    /// <summary>Live filter text (matches name / protocol / address).</summary>
    [ObservableProperty] private string _filter = "";

    /// <summary>Which column the table is sorted by, and the direction — driven by header clicks.</summary>
    [ObservableProperty] private string _sortKey = "Name";
    [ObservableProperty] private bool _sortDescending;

    // Column header captions carry the active sort arrow.
    public string NameHeader => "Name" + Arrow("Name");
    public string ProtocolHeader => "Protocol" + Arrow("Protocol");
    public string EndpointHeader => "Address / URL" + Arrow("Endpoint");
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
        Func<ServerItemViewModel, string> key = SortKey switch
        {
            "Protocol" => s => s.ProtocolLabel,
            "Endpoint" => s => s.Endpoint,
            _ => s => s.Name,
        };
        var filtered = Items.Where(Matches);
        var ordered = SortDescending
            ? filtered.OrderByDescending(key, StringComparer.OrdinalIgnoreCase)
            : filtered.OrderBy(key, StringComparer.OrdinalIgnoreCase);
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

    private bool HasSelection() => Selected is not null;

    partial void OnSelectedChanged(ServerItemViewModel? value)
    {
        DeleteCommand.NotifyCanExecuteChanged();
        TestSelectedCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Delete()
    {
        if (Selected is { } s)
        {
            Items.Remove(s);
            Selected = Items.FirstOrDefault();
            RefreshView();
        }
    }

    /// <summary>
    /// Probes the selected server via the service, colouring it green/red. Note: the service resolves
    /// the name against its *applied* config, so a server that hasn't been Applied yet returns
    /// "No server named …" — which we treat as "unknown" (uncoloured), not a failure.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task TestSelectedAsync()
    {
        if (Selected is null) return;
        IsTesting = true;
        ResultOpen = false;
        try
        {
            var (ok, message) = await ProbeAsync(Selected);
            ResultMessage = message;
            ResultIsError = !ok;
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
        int ok = 0, failed = 0;
        try
        {
            foreach (var s in Items.ToList())
            {
                var (passed, _) = await ProbeAsync(s);
                if (passed) ok++;
                else if (s.Health == ServerHealth.Failed) failed++; // skip "not applied yet"
            }
            ShowResult($"Tested {Items.Count} server(s): {ok} OK, {failed} failed.", isError: ok == 0 && failed > 0);
        }
        finally
        {
            IsTesting = false;
        }
    }

    // Tests one server and sets its Health. "No server named" means it isn't Applied yet (not a
    // failure), so we leave that row uncoloured. Never throws — returns the message to show.
    private async Task<(bool Ok, string Message)> ProbeAsync(ServerItemViewModel s)
    {
        try
        {
            var message = await _main.Backend.TestServerAsync(s.Name);
            s.Health = ServerHealth.Ok;
            return (true, message);
        }
        catch (Exception ex)
        {
            if (!ex.Message.Contains("No server named", StringComparison.OrdinalIgnoreCase))
                s.Health = ServerHealth.Failed;
            return (false, ex.Message);
        }
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
