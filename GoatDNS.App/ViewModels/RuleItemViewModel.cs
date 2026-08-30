using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using GoatDNS.Core.Config;

namespace GoatDNS.App.ViewModels;

/// <summary>
/// Observable mirror of <see cref="RuleDefinition"/>. The string-list fields (Hosts, Processes)
/// are edited as newline text; hosts-file references are edited as a checklist and the pool/server
/// target as a combo, both populated at dialog-open time via <see cref="BeginEdit"/>.
/// </summary>
public partial class RuleItemViewModel : ObservableObject
{
    [ObservableProperty][NotifyPropertyChangedFor(nameof(Summary))] private string _name = "New Rule";
    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty] private string _hostsText = "";
    [ObservableProperty] private string _processesText = "";
    [ObservableProperty] private string? _interfaceName;
    [ObservableProperty] private bool _ignoreWhenInterfaceDown;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    [NotifyPropertyChangedFor(nameof(ShowPool))]
    private RuleActionType _action = RuleActionType.Process;

    [ObservableProperty][NotifyPropertyChangedFor(nameof(Summary))] private string? _pool;
    [ObservableProperty] private DnssecMode _dnssec = DnssecMode.Off;

    /// <summary>Persisted hosts-file references (by name).</summary>
    public List<string> HostsFiles { get; private set; } = [];

    /// <summary>Populated only while the edit dialog is open.</summary>
    public ObservableCollection<SelectableItem> HostsFileChoices { get; } = [];

    /// <summary>Pool + server names offered by the target combo (edit-time).</summary>
    public ObservableCollection<string> PoolTargets { get; } = [];

    public IReadOnlyList<RuleActionType> ActionOptions { get; } = Enum.GetValues<RuleActionType>();
    public IReadOnlyList<DnssecMode> DnssecOptions { get; } = Enum.GetValues<DnssecMode>();

    /// <summary>The pool target only applies to the Process action.</summary>
    public bool ShowPool => Action == RuleActionType.Process;

    public string Summary => ShowPool && Pool is { Length: > 0 }
        ? $"{Action} → {Pool}"
        : Action.ToString();

    public RuleItemViewModel() { }

    public RuleItemViewModel(RuleDefinition r)
    {
        _name = r.Name;
        _enabled = r.Enabled;
        _hostsText = TextLists.Join(r.Hosts);
        _processesText = TextLists.Join(r.Processes);
        _interfaceName = r.InterfaceName;
        _ignoreWhenInterfaceDown = r.IgnoreWhenInterfaceDown;
        _action = r.Action;
        _pool = r.Pool;
        _dnssec = r.Dnssec;
        HostsFiles = [.. r.HostsFiles];
    }

    public RuleItemViewModel Clone() => new(ToModel());

    /// <summary>Populates the target combo and the hosts-file checklist for the dialog.</summary>
    public void BeginEdit(IEnumerable<string> poolTargets, IEnumerable<string> hostsFileNames)
    {
        PoolTargets.Clear();
        foreach (var t in poolTargets) PoolTargets.Add(t);

        HostsFileChoices.Clear();
        foreach (var h in hostsFileNames)
            HostsFileChoices.Add(new SelectableItem(h, HostsFiles.Contains(h, StringComparer.OrdinalIgnoreCase)));
    }

    /// <summary>Reads the checked hosts-file choices back into <see cref="HostsFiles"/>.</summary>
    public void EndEdit() =>
        HostsFiles = HostsFileChoices.Where(c => c.IsSelected).Select(c => c.Name).ToList();

    public RuleDefinition ToModel() => new()
    {
        Name = Name.Trim(),
        Enabled = Enabled,
        Hosts = TextLists.Split(HostsText),
        HostsFiles = [.. HostsFiles],
        Processes = TextLists.Split(ProcessesText),
        InterfaceName = TextLists.Blank(InterfaceName),
        IgnoreWhenInterfaceDown = IgnoreWhenInterfaceDown,
        Action = Action,
        // Pool is only meaningful for the Process action; drop it otherwise so Validate() stays happy.
        Pool = Action == RuleActionType.Process ? TextLists.Blank(Pool) : null,
        Dnssec = Dnssec,
    };
}
