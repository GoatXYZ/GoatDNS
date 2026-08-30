using GoatDNS.Core.Capture;
using GoatDNS.Core.Engine;
using GoatDNS.Core.Logging;

namespace GoatDNS.Service;

/// <summary>
/// Selects the interception mechanism. WinDivert (a signed WFP driver — no test signing) is the
/// provider; on non-Windows or if the driver can't be opened, the service still runs as a plain
/// loopback resolver via <see cref="NullCaptureProvider"/> instead of failing to start.
/// </summary>
public static class CaptureProviderFactory
{
    public static ICaptureProvider Create(QueryLog log, DnsEngine engine)
    {
        if (!OperatingSystem.IsWindows())
            return new NullCaptureProvider();

        // Construction does no native work; a missing driver/dll surfaces at StartAsync, which the
        // worker catches and reports (leaving the service running as a local resolver).
        return new GoatDNS.WinDivert.WinDivertCaptureProvider(engine, log);
    }
}
