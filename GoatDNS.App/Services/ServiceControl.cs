using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;

namespace GoatDNS.App.Services;

public enum ServiceState { NotInstalled, Stopped, Running, Unknown }

/// <summary>
/// Controls the GoatDNS Windows service from the (normally unelevated) app. Query needs no rights;
/// start/stop/install do, so those self-elevate: if we aren't admin, we relaunch our own exe with
/// <c>--svc &lt;action&gt;</c> under the "runas" verb (a UAC prompt), wait for it, and read its exit code.
/// The elevated instance runs the action headless via <see cref="RunActionElevated"/> and exits.
/// </summary>
public static class ServiceControl
{
    public const string ServiceName = "GoatDNS";

    public static bool IsElevated
    {
        get
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public static ServiceState Query()
    {
        var (code, output) = Run("sc.exe", $"query \"{ServiceName}\"");
        if (code == 1060 || output.Contains("does not exist", StringComparison.OrdinalIgnoreCase))
            return ServiceState.NotInstalled;
        if (output.Contains("RUNNING", StringComparison.Ordinal)) return ServiceState.Running;
        if (output.Contains("STOPPED", StringComparison.Ordinal) || output.Contains("STOP_PENDING", StringComparison.Ordinal))
            return ServiceState.Stopped;
        return ServiceState.Unknown;
    }

    /// <summary>Runs a control action, elevating via UAC first if we aren't already admin.</summary>
    public static (bool Ok, string Message) EnsureAction(string action)
    {
        if (IsElevated) return RunActionElevated(action);

        try
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!, $"--svc {action}")
            {
                UseShellExecute = true,
                Verb = "runas",
            };
            using var p = Process.Start(psi)!;
            p.WaitForExit();
            return p.ExitCode == 0
                ? (true, $"Service {action} succeeded.")
                : (false, $"Service {action} failed (exit {p.ExitCode}). Try running from an elevated prompt.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return (false, "Elevation was cancelled.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>Performs the action directly; must be called elevated. Invoked by the `--svc` relaunch.</summary>
    public static (bool Ok, string Message) RunActionElevated(string action)
    {
        try
        {
            return action.ToLowerInvariant() switch
            {
                "start" => Sc($"start \"{ServiceName}\"", "started"),
                "stop" => Sc($"stop \"{ServiceName}\"", "stopped"),
                "restart" => Restart(),
                "install" => Install(),
                "uninstall" => Uninstall(),
                _ => (false, $"Unknown action '{action}'"),
            };
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static (bool, string) Restart()
    {
        Run("sc.exe", $"stop \"{ServiceName}\"");
        WaitForState(ServiceState.Stopped, TimeSpan.FromSeconds(15));
        return Sc($"start \"{ServiceName}\"", "restarted");
    }

    private static (bool, string) Install()
    {
        var exe = LocateServiceExe();
        if (exe is null)
            return (false, "GoatDNS.Service.exe not found next to the app or in Program Files. Run scripts\\setup.ps1 instead.");

        var (code, output) = Run("sc.exe",
            $"create \"{ServiceName}\" binPath= \"{exe}\" start= auto obj= LocalSystem DisplayName= \"GoatDNS\"");
        if (code != 0) return (false, $"sc create failed: {output.Trim()}");
        Run("sc.exe", $"description \"{ServiceName}\" \"GoatDNS system-wide encrypted DNS client\"");
        return Sc($"start \"{ServiceName}\"", "installed and started");
    }

    private static (bool, string) Uninstall()
    {
        Run("sc.exe", $"stop \"{ServiceName}\"");
        WaitForState(ServiceState.Stopped, TimeSpan.FromSeconds(15));
        var (code, output) = Run("sc.exe", $"delete \"{ServiceName}\"");
        return code == 0 ? (true, "Service uninstalled.") : (false, $"sc delete failed: {output.Trim()}");
    }

    /// <summary>Looks for the published service binary next to the app, in a sibling folder, or in Program Files.</summary>
    private static string? LocateServiceExe()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "GoatDNS.Service.exe"),
            Path.Combine(AppContext.BaseDirectory, "..", "service", "GoatDNS.Service.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "GoatDNS", "service", "GoatDNS.Service.exe"),
        };
        return candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
    }

    private static (bool, string) Sc(string args, string pastTense)
    {
        var (code, output) = Run("sc.exe", args);
        return code == 0 ? (true, $"Service {pastTense}.") : (false, output.Trim().Length > 0 ? output.Trim() : $"sc failed (exit {code})");
    }

    private static void WaitForState(ServiceState target, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && Query() != target)
            Thread.Sleep(300);
    }

    private static (int Code, string Output) Run(string exe, string args)
    {
        using var p = new Process
        {
            StartInfo = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        p.Start();
        string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output);
    }
}
