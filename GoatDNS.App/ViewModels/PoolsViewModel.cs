using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GoatDNS.Core.Config;

namespace GoatDNS.App.ViewModels;

/// <summary>Backs the Pools page: working list of pools; the editor multi-selects server membership.</summary>
public partial class PoolsViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    public PoolsViewModel(MainViewModel main) => _main = main;

    public ObservableCollection<PoolItemViewModel> Items { get; } = [];

    /// <summary>Filtered + sorted projection of <see cref="Items"/> that the table binds to.</summary>
    public ObservableCollection<PoolItemViewModel> Visible { get; } = [];

    [ObservableProperty] private PoolItemViewModel? _selected;

    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private string _sortKey = "Name";
    [ObservableProperty] private bool _sortDescending;

    public string NameHeader => "Name" + Arrow("Name");
    public string StrategyHeader => "Strategy" + Arrow("Strategy");
    public string MembersHeader => "Members" + Arrow("Members");
    private string Arrow(string key) => SortKey == key ? (SortDescending ? "  ▼" : "  ▲") : "";

    /// <summary>Every server name currently defined — the pool editor's membership choices.</summary>
    public IEnumerable<string> AllServerNames => _main.Servers.Items.Select(s => s.Name);

    public void Load(IEnumerable<PoolDefinition> pools)
    {
        Items.Clear();
        foreach (var p in pools) Items.Add(new PoolItemViewModel(p));
        Selected = Items.FirstOrDefault();
        RefreshView();
    }

    public void Commit(PoolItemViewModel edited, PoolItemViewModel? original)
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

    partial void OnSelectedChanged(PoolItemViewModel? value) => DeleteCommand.NotifyCanExecuteChanged();

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

    // ---- Filter + sort projection (Items -> Visible) ----

    partial void OnFilterChanged(string value) => RefreshView();
    partial void OnSortKeyChanged(string value) { NotifyHeaders(); RefreshView(); }
    partial void OnSortDescendingChanged(bool value) { NotifyHeaders(); RefreshView(); }

    private void NotifyHeaders()
    {
        OnPropertyChanged(nameof(NameHeader));
        OnPropertyChanged(nameof(StrategyHeader));
        OnPropertyChanged(nameof(MembersHeader));
    }

    [RelayCommand]
    private void Sort(string key)
    {
        if (SortKey == key) SortDescending = !SortDescending;
        else { SortKey = key; SortDescending = false; }
    }

    private bool Matches(PoolItemViewModel p) =>
        Filter.Length == 0
        || p.Name.Contains(Filter, StringComparison.OrdinalIgnoreCase)
        || p.StrategyLabel.Contains(Filter, StringComparison.OrdinalIgnoreCase)
        || p.MembersLabel.Contains(Filter, StringComparison.OrdinalIgnoreCase);

    private void RefreshView()
    {
        Func<PoolItemViewModel, string> key = SortKey switch
        {
            "Strategy" => p => p.StrategyLabel,
            "Members" => p => p.MembersLabel,
            _ => p => p.Name,
        };
        var filtered = Items.Where(Matches);
        var ordered = SortDescending
            ? filtered.OrderByDescending(key, StringComparer.OrdinalIgnoreCase)
            : filtered.OrderBy(key, StringComparer.OrdinalIgnoreCase);
        ListProjection.Reproject(Visible, ordered, () => Selected, v => Selected = v);
    }
}
