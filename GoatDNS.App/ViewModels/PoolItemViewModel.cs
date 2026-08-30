using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using GoatDNS.Core.Config;

namespace GoatDNS.App.ViewModels;

/// <summary>
/// Observable mirror of <see cref="PoolDefinition"/>. Membership is stored as a plain name list
/// (<see cref="Servers"/>); the edit dialog projects it onto <see cref="ServerChoices"/> (every
/// known server + a checkbox) via <see cref="BeginEdit"/>, then writes back in <see cref="EndEdit"/>.
/// </summary>
public partial class PoolItemViewModel : ObservableObject
{
    [ObservableProperty][NotifyPropertyChangedFor(nameof(Summary))] private string _name = "New Pool";
    [ObservableProperty][NotifyPropertyChangedFor(nameof(Summary))][NotifyPropertyChangedFor(nameof(StrategyLabel))] private PoolStrategy _strategy = PoolStrategy.Failover;

    /// <summary>Persisted server-name membership.</summary>
    public List<string> Servers { get; private set; } = [];

    /// <summary>Populated only while the edit dialog is open.</summary>
    public ObservableCollection<SelectableItem> ServerChoices { get; } = [];

    public IReadOnlyList<PoolStrategy> StrategyOptions { get; } = Enum.GetValues<PoolStrategy>();

    public string StrategyLabel => Strategy.ToString();

    /// <summary>Comma-joined member names for the Members column.</summary>
    public string MembersLabel => Servers.Count == 0 ? "(none)" : string.Join(", ", Servers);

    public string Summary => $"{Strategy} · {Servers.Count} server(s)";

    public PoolItemViewModel() { }

    public PoolItemViewModel(PoolDefinition p)
    {
        _name = p.Name;
        _strategy = p.Strategy;
        Servers = [.. p.Servers];
    }

    public PoolItemViewModel Clone() => new(ToModel());

    /// <summary>Fills <see cref="ServerChoices"/> from all known servers, pre-checking current members.</summary>
    public void BeginEdit(IEnumerable<string> allServerNames)
    {
        ServerChoices.Clear();
        foreach (var name in allServerNames)
            ServerChoices.Add(new SelectableItem(name, Servers.Contains(name, StringComparer.OrdinalIgnoreCase)));
    }

    /// <summary>Reads the checked choices back into <see cref="Servers"/>.</summary>
    public void EndEdit()
    {
        Servers = ServerChoices.Where(c => c.IsSelected).Select(c => c.Name).ToList();
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(MembersLabel));
    }

    public PoolDefinition ToModel() => new()
    {
        Name = Name.Trim(),
        Strategy = Strategy,
        Servers = [.. Servers],
    };
}
