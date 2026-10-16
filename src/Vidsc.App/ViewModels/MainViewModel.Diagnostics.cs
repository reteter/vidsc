using System.Diagnostics;

namespace Vidsc.App.ViewModels;

public sealed partial class MainViewModel
{
    private string ResourceDiagnosticText()
    {
        using var app = Process.GetCurrentProcess();
        long privateBytes = app.PrivateMemorySize64, workingSet = app.WorkingSet64;
        string child = "none observed";
        if (MediaJobProcess is { } identity)
        {
            try
            {
                using var process = Process.GetProcessById(identity.ProcessId);
                if (!process.HasExited && new DateTimeOffset(process.StartTime.ToUniversalTime()) == identity.StartedAtUtc)
                {
                    privateBytes += process.PrivateMemorySize64; workingSet += process.WorkingSet64;
                    child = $"private={process.PrivateMemorySize64}; working set={process.WorkingSet64}";
                }
                else child = "observed job exited";
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            { child = "exited or counters unavailable"; }
        }
        return $"app + observed job private bytes={privateBytes}; working set={workingSet}; child={child}; "
            + "OS file cache/shared allocations not inferred; descendants unavailable in this view";
    }
}
