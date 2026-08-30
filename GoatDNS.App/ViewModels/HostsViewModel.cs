using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GoatDNS.Core.Config;

namespace GoatDNS.App.ViewModels;

/// <summary>Backs the Hosts page: hosts-file entries edited inline, plus a reload action.</summary>
public partial class HostsViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    public HostsViewModel(MainViewModel main) => _main = main;

    public ObservableCollection<HostsItemViewModel> Items { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private HostsItemViewModel? _selected;

    /// <summary>Drives the inline editor's enabled state (nothing selected = nothing to edit).</summary>
    public bool HasSelection => Selected is not null;

    /// <summary>Static list for the mode combo so its items exist even with nothing selected.</summary>
    public IReadOnlyList<HostsFileMode> ModeOptions { get; } = Enum.GetValues<HostsFileMode>();

    public void Load(IEnumerable<HostsFileDefinition> hosts)
    {
        Items.Clear();
        foreach (var h in hosts) Items.Add(new HostsItemViewModel(h));
        Selected = Items.FirstOrDefault();
    }

    partial void OnSelectedChanged(HostsItemViewModel? value) => DeleteCommand.NotifyCanExecuteChanged();

    [RelayCommand]
    private void Add()
    {
        var h = new HostsItemViewModel();
        Items.Add(h);
        Selected = h;
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

    /// <summary>
    /// Re-applies the whole config so the service re-reads the hosts files from disk. There is no
    /// dedicated "reload hosts" IPC command, and ApplyConfig rebuilds the providers, which is the reload.
    /// </summary>
    [RelayCommand]
    private Task ReloadAsync() => _main.ApplyAsync();
}
