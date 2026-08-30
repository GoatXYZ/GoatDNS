using CommunityToolkit.Mvvm.ComponentModel;
using GoatDNS.Core.Config;

namespace GoatDNS.App.ViewModels;

/// <summary>Observable mirror of <see cref="HostsFileDefinition"/> (edited inline on the Hosts page).</summary>
public partial class HostsItemViewModel : ObservableObject
{
    [ObservableProperty][NotifyPropertyChangedFor(nameof(Summary))] private string _name = "hosts";
    [ObservableProperty][NotifyPropertyChangedFor(nameof(Summary))] private string _path = "";
    [ObservableProperty][NotifyPropertyChangedFor(nameof(Summary))] private HostsFileMode _mode = HostsFileMode.StaticHosts;

    public IReadOnlyList<HostsFileMode> ModeOptions { get; } = Enum.GetValues<HostsFileMode>();

    public string Summary => $"{Mode} · {Path}";

    public HostsItemViewModel() { }

    public HostsItemViewModel(HostsFileDefinition h)
    {
        _name = h.Name;
        _path = h.Path;
        _mode = h.Mode;
    }

    public HostsFileDefinition ToModel() => new()
    {
        Name = Name.Trim(),
        Path = Path.Trim(),
        Mode = Mode,
    };
}
