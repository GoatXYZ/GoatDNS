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

    [ObservableProperty] private PoolItemViewModel? _selected;

    /// <summary>Every server name currently defined — the pool editor's membership choices.</summary>
    public IEnumerable<string> AllServerNames => _main.Servers.Items.Select(s => s.Name);

    public void Load(IEnumerable<PoolDefinition> pools)
    {
        Items.Clear();
        foreach (var p in pools) Items.Add(new PoolItemViewModel(p));
        Selected = Items.FirstOrDefault();
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
        }
    }
}
