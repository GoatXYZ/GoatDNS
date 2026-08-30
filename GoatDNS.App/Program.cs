using GoatDNS.App.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace GoatDNS.App;

/// <summary>
/// Custom entry point (replaces the generated XAML Main). Its one extra job over the boilerplate is
/// the elevated helper mode: when relaunched as <c>GoatDNS.App.exe --svc &lt;action&gt;</c> under UAC,
/// it performs a single Windows-service action headless (no window) and returns an exit code, which
/// is how the unelevated UI gets admin power over the service. See <see cref="ServiceControl"/>.
/// </summary>
public static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--svc")
        {
            var (ok, _) = ServiceControl.RunActionElevated(args[1]);
            return ok ? 0 : 1;
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
        return 0;
    }
}
