using System.Runtime.InteropServices;

namespace Vidsc.App.Services;

internal static class CachePressure
{
    internal static bool ShouldReclaim(ulong available, ulong total, ulong gpuUsage = 0, ulong gpuBudget = 0)
        => (total > 0 && (available < 512UL * 1024 * 1024 || available / (double)total < .1))
            || (gpuBudget > 0 && gpuUsage / (double)gpuBudget >= .9);

    internal static (ulong Available, ulong Total) ReadMemory()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        return GlobalMemoryStatusEx(ref status) ? (status.AvailablePhysical, status.TotalPhysical) : (0, 0);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile,
            TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
}
