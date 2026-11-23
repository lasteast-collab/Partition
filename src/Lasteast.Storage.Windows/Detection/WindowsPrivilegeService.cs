using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using Lasteast.Core.Abstractions;

namespace Lasteast.Storage.Windows.Detection;

[SupportedOSPlatform("windows")]
public sealed class WindowsPrivilegeService : IPrivilegeService
{
    private const int ERROR_CANCELLED = 1223;

    public bool IsElevated { get; } = Detect();

    private static bool Detect()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public bool TryRestartElevated(string[] args)
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return false;
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" };
            foreach (var a in args) psi.ArgumentList.Add(a);
            Process.Start(psi);
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ERROR_CANCELLED)
        {
            return false; // user declined the UAC prompt
        }
    }
}
