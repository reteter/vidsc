using System.Text.Json;
using Vidsc.Core.Time;
using Vidsc.Media.Inspection;
using Vidsc.Media.Tooling;

namespace Vidsc.Media.Export;

public sealed record VerifiedAccurateExportResult(Guid JobId, bool Success, bool Cancelled,
    bool ExitConfirmed, string? Destination, string? JournalPath, string? Error,
    Rational? PresentationEndSeconds = null, string? Assurance = null,
    IReadOnlyList<QualifiedAudioTiming>? AudioTimings = null,
    IReadOnlyList<AccurateAudioDetails>? AudioDetails = null,
    double? MinimumPsnr = null, double? MinimumBlockSsim = null);

/// <summary>Qualified accurate rendering with guarded validation before no-clobber promotion.</summary>
public sealed class VerifiedAccurateExporter(AccurateCutVerifier verifier)
{
    public async Task<VerifiedAccurateExportResult> ExportAsync(QualifiedAccurateCut plan,
        string destination, string journalDirectory, CancellationToken cancellationToken,
        Action<ToolProcessIdentity>? processStarted = null)
    {
        Guid jobId = Guid.NewGuid();
        using var lease = await CopyJobAdmission.EnterAsync(jobId, cancellationToken).ConfigureAwait(false);
        OwnedOutputTransaction? transaction = null;
        ToolProcessIdentity? active = null;
        bool retained = false, committed = false;
        void Started(ToolProcessIdentity identity) { active = identity; processStarted?.Invoke(identity); }
        try
        {
            if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetExtension(destination), ".mp4"))
                throw new ArgumentException("The qualified accurate route requires an explicit MP4 destination.");
            var source = InspectionSourceIdentity.Capture(plan.SourcePath);
            if (source.IdentityKey != plan.Request.Source.SourceId)
                throw new InvalidOperationException("The qualified accurate source identity is stale.");
            transaction = OwnedOutputTransaction.Prepare(jobId, source, destination, journalDirectory);
            await verifier.RunUnderLeaseAsync(plan, jobId, "qualify", null, lease, cancellationToken, Started).ConfigureAwait(false);
            await verifier.RunUnderLeaseAsync(plan, jobId, "write", transaction.TemporaryPath, lease,
                cancellationToken, Started).ConfigureAwait(false);
            transaction.ProtectForValidation();
            var report = await verifier.RunUnderLeaseAsync(plan, jobId, "validate", transaction.TemporaryPath,
                lease, cancellationToken, Started).ConfigureAwait(false);
            var output = report.Output ?? throw new InvalidOperationException("Accurate output validation is missing.");
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit(new(jobId, source.IdentityKey, transaction.TemporaryPath, output.OutputSha256));
            committed = true;
            return new(jobId, true, false, true, Path.GetFullPath(destination), transaction.JournalPath, null,
                output.PresentationEndSeconds, output.Assurance, plan.AudioTimings, plan.AudioDetails,
                output.MinimumPsnr, output.MinimumBlockSsim);
        }
        catch (NavigationProcessStuckException failure)
        {
            retained = true;
            lease.Retain(active, failure.ProcessId, () =>
            {
                if (!verifier.ReleaseStuckGuards(jobId)) throw new InvalidOperationException("Accurate process remains retained.");
                transaction?.Dispose();
            });
            return new(jobId, false, false, false, null, transaction?.JournalPath,
                "Accurate worker exit is unconfirmed; source/output guards and shared admission remain reserved.");
        }
        catch (OperationCanceledException)
        {
            return new(jobId, false, true, true, null, transaction?.JournalPath, "Accurate export cancelled before promotion.");
        }
        catch (Exception failure) when (failure is IOException or InvalidOperationException or ArgumentException or
            UnauthorizedAccessException or OverflowException or JsonException or KeyNotFoundException or FormatException)
        {
            return new(jobId, false, false, true, null, transaction?.JournalPath, failure.Message);
        }
        finally
        {
            if (!retained)
            {
                try { if (!committed) transaction?.DiscardPreparedOutput(); }
                catch (Exception failure) when (failure is IOException or InvalidOperationException or UnauthorizedAccessException) { }
                finally { transaction?.Dispose(); }
            }
        }
    }

    public static bool TryReconcileStuckJob(Guid jobId) => CopyJobAdmission.TryReconcile(jobId);
}
