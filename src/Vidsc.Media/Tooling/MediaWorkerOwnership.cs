using Vidsc.Media.Export;

namespace Vidsc.Media.Tooling;

/// <summary>Application entry point to reconcile retained shared-worker ownership; never polls or kills by PID.</summary>
public static class MediaWorkerOwnership
{
    public static bool TryReconcile(Guid jobId) => CopyJobAdmission.TryReconcile(jobId);
}
