#nullable enable
namespace FlyleafLib.MediaFramework.MediaRenderer;

public sealed record GpuMemorySegmentSnapshot(ulong Budget, ulong CurrentUsage,
    ulong CurrentReservation, ulong AvailableForReservation);
public sealed record GpuMemorySnapshot(long ReadQpcStart, long ReadQpcEnd, bool DevicePresent,
    long? AdapterLuid, string? AdapterName, GpuMemorySegmentSnapshot? Local,
    GpuMemorySegmentSnapshot? NonLocal, string? UnavailableReason);
