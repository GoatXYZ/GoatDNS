using CommunityToolkit.Mvvm.ComponentModel;
using GoatDNS.Core.Config;
using GoatDNS.Core.Stamps;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace GoatDNS.App.ViewModels;

/// <summary>Result of the last connectivity Test for a server; drives the row colour (green/red).</summary>
public enum ServerHealth { Unknown, Ok, Failed }

/// <summary>
/// Observable mirror of <see cref="ServerDefinition"/>. Serves as both the list row and the
/// edit-dialog target (edited on a <see cref="Clone"/>, committed by replacing the list item),
/// which is why the protocol-dependent field visibility lives here as computed properties.
/// </summary>
public partial class ServerItemViewModel : ObservableObject
{
    private static readonly SolidColorBrush OkBrush = new(Colors.SeaGreen);
    private static readonly SolidColorBrush FailedBrush = new(Colors.IndianRed);

    /// <summary>Last Test result; untested servers stay <see cref="ServerHealth.Unknown"/> (default colour).</summary>
    [ObservableProperty][NotifyPropertyChangedFor(nameof(HealthBrush))] private ServerHealth _health;

    /// <summary>Row foreground: green when the last Test passed, red when it failed, theme default otherwise.</summary>
    public Brush HealthBrush => Health switch
    {
        ServerHealth.Ok => OkBrush,
        ServerHealth.Failed => FailedBrush,
        _ => (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"],
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private string _name = "New Server";

    // Changing the protocol re-shapes the visible fields, so notify every Show* flag.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    [NotifyPropertyChangedFor(nameof(ProtocolLabel))]
    [NotifyPropertyChangedFor(nameof(Endpoint))]
    [NotifyPropertyChangedFor(nameof(ShowAddress))]
    [NotifyPropertyChangedFor(nameof(ShowUrl))]
    [NotifyPropertyChangedFor(nameof(ShowHostname))]
    [NotifyPropertyChangedFor(nameof(ShowBootstrap))]
    [NotifyPropertyChangedFor(nameof(ShowTlsPins))]
    [NotifyPropertyChangedFor(nameof(ShowHttp3))]
    [NotifyPropertyChangedFor(nameof(ShowDnsCrypt))]
    private ServerProtocol _protocol;

    [ObservableProperty][NotifyPropertyChangedFor(nameof(Summary))][NotifyPropertyChangedFor(nameof(Endpoint))] private string? _address;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(Summary))][NotifyPropertyChangedFor(nameof(Endpoint))] private string? _url;
    [ObservableProperty] private string? _hostname;
    [ObservableProperty] private bool _useHttp3;
    [ObservableProperty] private string? _bootstrapAddress;
    [ObservableProperty] private string _tlsPinsText = "";
    [ObservableProperty] private string? _providerName;
    [ObservableProperty] private string? _publicKeyHex;
    [ObservableProperty] private string? _relayAddress;
    [ObservableProperty] private string? _bindInterface;

    public IReadOnlyList<ServerProtocol> ProtocolOptions { get; } = Enum.GetValues<ServerProtocol>();

    // Field visibility per protocol (see ServerDefinition XML docs for the field semantics).
    public bool ShowAddress => Protocol is ServerProtocol.Plain or ServerProtocol.DoT or ServerProtocol.DoQ or ServerProtocol.DnsCrypt;
    public bool ShowUrl => Protocol is ServerProtocol.DoH;
    public bool ShowHostname => Protocol is ServerProtocol.DoH or ServerProtocol.DoT or ServerProtocol.DoQ;
    public bool ShowBootstrap => Protocol is ServerProtocol.DoH or ServerProtocol.DoT or ServerProtocol.DoQ;
    public bool ShowTlsPins => Protocol is ServerProtocol.DoH or ServerProtocol.DoT or ServerProtocol.DoQ;
    public bool ShowHttp3 => Protocol is ServerProtocol.DoH;
    public bool ShowDnsCrypt => Protocol is ServerProtocol.DnsCrypt;

    /// <summary>Short protocol label for the Protocol column.</summary>
    public string ProtocolLabel => Protocol switch
    {
        ServerProtocol.DoH => "DoH",
        ServerProtocol.DoT => "DoT",
        ServerProtocol.DoQ => "DoQ",
        ServerProtocol.DnsCrypt => "DNSCrypt",
        _ => "Plain",
    };

    /// <summary>The address-or-URL shown in the last column (URL for DoH, else the IP/host).</summary>
    public string Endpoint => Protocol == ServerProtocol.DoH ? (Url ?? "") : (Address ?? Hostname ?? "");

    /// <summary>Secondary text for the list row.</summary>
    public string Summary => Protocol switch
    {
        ServerProtocol.DoH => $"DoH · {Url}",
        ServerProtocol.DnsCrypt => $"DNSCrypt · {Address}",
        _ => $"{Protocol} · {Address}",
    };

    public ServerItemViewModel() { }

    public ServerItemViewModel(ServerDefinition s)
    {
        _name = s.Name;
        _protocol = s.Protocol;
        _address = s.Address;
        _url = s.Url;
        _hostname = s.Hostname;
        _useHttp3 = s.UseHttp3;
        _bootstrapAddress = s.BootstrapAddress;
        _tlsPinsText = TextLists.Join(s.TlsPins);
        _providerName = s.ProviderName;
        _publicKeyHex = s.PublicKeyHex;
        _relayAddress = s.RelayAddress;
        _bindInterface = s.BindInterface;
    }

    /// <summary>An independent copy for the edit dialog (so Cancel discards changes).</summary>
    public ServerItemViewModel Clone() => new(ToModel());

    public ServerDefinition ToModel() => new()
    {
        Name = Name.Trim(),
        Protocol = Protocol,
        Address = TextLists.Blank(Address),
        Url = TextLists.Blank(Url),
        Hostname = TextLists.Blank(Hostname),
        UseHttp3 = UseHttp3,
        BootstrapAddress = TextLists.Blank(BootstrapAddress),
        TlsPins = TextLists.Split(TlsPinsText),
        ProviderName = TextLists.Blank(ProviderName),
        PublicKeyHex = TextLists.Blank(PublicKeyHex),
        RelayAddress = TextLists.Blank(RelayAddress),
        BindInterface = TextLists.Blank(BindInterface),
    };

    /// <summary>Builds a server from a parsed <c>sdns://</c> stamp (the Servers view's import feature).</summary>
    public static ServerItemViewModel FromStamp(DnsStamp s)
    {
        var vm = new ServerItemViewModel();
        switch (s.Protocol)
        {
            case StampProtocol.Plain:
                vm.Protocol = ServerProtocol.Plain;
                vm.Address = s.Address;
                vm.Name = $"Plain {s.Address}";
                break;

            case StampProtocol.DoH:
                vm.Protocol = ServerProtocol.DoH;
                vm.Hostname = TextLists.Blank(s.Hostname);
                vm.Url = $"https://{s.Hostname}{s.Path}";
                vm.BootstrapAddress = TextLists.Blank(s.Address); // the stamp's Address is the resolver IP
                vm.TlsPinsText = TextLists.Join(s.Hashes.Select(Convert.ToBase64String));
                vm.Name = $"DoH {s.Hostname}";
                break;

            case StampProtocol.DoT:
            case StampProtocol.DoQ:
                vm.Protocol = s.Protocol == StampProtocol.DoQ ? ServerProtocol.DoQ : ServerProtocol.DoT;
                vm.Address = s.Address;
                vm.Hostname = TextLists.Blank(s.Hostname);
                vm.TlsPinsText = TextLists.Join(s.Hashes.Select(Convert.ToBase64String));
                vm.Name = $"{vm.Protocol} {(string.IsNullOrEmpty(s.Hostname) ? s.Address : s.Hostname)}";
                break;

            case StampProtocol.DnsCrypt:
                vm.Protocol = ServerProtocol.DnsCrypt;
                vm.Address = s.Address;
                vm.ProviderName = s.ProviderName;
                vm.PublicKeyHex = Convert.ToHexString(s.PublicKey);
                vm.Name = $"DNSCrypt {s.ProviderName}";
                break;

            default:
                throw new FormatException($"Unsupported stamp protocol {s.Protocol}.");
        }
        return vm;
    }
}
