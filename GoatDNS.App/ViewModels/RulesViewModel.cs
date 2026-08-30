using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GoatDNS.Core.Config;

namespace GoatDNS.App.ViewModels;

/// <summary>
/// Backs the Rules page: an ordered, reorderable rule list. Rules are evaluated top-to-bottom, so
/// the last rule is treated as the pinned catch-all/default and is never moved out of last place.
/// </summary>
public partial class RulesViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    public RulesViewModel(MainViewModel main) => _main = main;

    public ObservableCollection<RuleItemViewModel> Items { get; } = [];

    [ObservableProperty] private RuleItemViewModel? _selected;

    /// <summary>Pool names then server names — the rule editor's target choices.</summary>
    public IEnumerable<string> PoolAndServerTargets =>
        _main.Pools.Items.Select(p => p.Name).Concat(_main.Servers.Items.Select(s => s.Name));

    /// <summary>Hosts-file names — the rule editor's hosts-file checklist.</summary>
    public IEnumerable<string> HostsFileNames => _main.Hosts.Items.Select(h => h.Name);

    public void Load(IEnumerable<RuleDefinition> rules)
    {
        Items.Clear();
        foreach (var r in rules) Items.Add(new RuleItemViewModel(r));
        Selected = Items.FirstOrDefault();
    }

    /// <summary>New rules are inserted just above the pinned default; edits replace in place.</summary>
    public void Commit(RuleItemViewModel edited, RuleItemViewModel? original)
    {
        if (original is null)
        {
            int at = Items.Count > 0 ? Items.Count - 1 : 0; // keep the last (default) rule last
            Items.Insert(at, edited);
        }
        else
        {
            int i = Items.IndexOf(original);
            if (i >= 0) Items[i] = edited; else Items.Add(edited);
        }
        Selected = edited;
    }

    private int SelectedIndex => Selected is null ? -1 : Items.IndexOf(Selected);

    private bool HasSelection() => Selected is not null;

    // The last item is the pinned default: it can't move, and nothing can move below it.
    private bool CanMoveUp() => SelectedIndex >= 1 && SelectedIndex != Items.Count - 1;
    private bool CanMoveDown() => SelectedIndex >= 0 && SelectedIndex < Items.Count - 2;

    partial void OnSelectedChanged(RuleItemViewModel? value) => RefreshCommands();

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void MoveUp()
    {
        int i = SelectedIndex;
        if (i >= 1) { Items.Move(i, i - 1); RefreshCommands(); }
    }

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private void MoveDown()
    {
        int i = SelectedIndex;
        if (i >= 0 && i < Items.Count - 1) { Items.Move(i, i + 1); RefreshCommands(); }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Delete()
    {
        if (Selected is { } s)
        {
            Items.Remove(s);
            Selected = Items.FirstOrDefault();
        }
    }

    private void RefreshCommands()
    {
        DeleteCommand.NotifyCanExecuteChanged();
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
    }
}
