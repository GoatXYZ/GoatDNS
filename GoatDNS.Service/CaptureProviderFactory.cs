using GoatDNS.Core.Capture;
using GoatDNS.Core.Logging;

namespace GoatDNS.Service;

/// <summary>
/// Selects the interception mechanism. eBPF is the intended path; if its runtime isn't present
/// (not installed, test-signing off), we fall back to NullCaptureProvider so the service still
/// runs as a plain loopback resolver instead of crash-looping.
/// </summary>
public static class CaptureProviderFactory
{
    public static ICaptureProvider Create(QueryLog log)
    {
        if (!OperatingSystem.IsWindows())
            return new NullCaptureProvider();

        try
        {
            return new GoatDNS.Ebpf.EbpfCaptureProvider();
        }
        catch (Exception ex)
        {
            log.Error($"eBPF provider unavailable ({ex.Message}); running without system-wide capture. " +
                      "Point a resolver at the listen port, or install the eBPF runtime with test-signing enabled.");
            return new NullCaptureProvider();
        }
    }
}
