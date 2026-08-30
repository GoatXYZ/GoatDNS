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

    [ObservableProperty] private ServerItemViewModel? _selected;

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
        }
    }

    /// <summary>
    /// Probes the selected server via the service. Note: the service resolves the name against its
    /// *applied* config, so a server that hasn't been Applied yet returns "No server named …".
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task TestSelectedAsync()
    {
        if (Selected is null) return;
        IsTesting = true;
        ResultOpen = false;
        try
        {
            ResultMessage = await _main.Ipc.TestServerAsync(Selected.Name);
            ResultIsError = false;
        }
        catch (Exception ex)
        {
            ResultMessage = ex.Message;
            ResultIsError = true;
        }
        finally
        {
            IsTesting = false;
            ResultOpen = true;
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
