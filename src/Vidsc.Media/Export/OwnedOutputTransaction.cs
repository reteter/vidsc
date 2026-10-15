using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Vidsc.Media.Inspection;

namespace Vidsc.Media.Export;

/// <summary>Evidence issued by the production media validator, bound to one protected output.</summary>
public sealed class ValidatedOutputReceipt
{
    internal ValidatedOutputReceipt(Guid jobId, string sourceIdentityKey, string temporaryPath, string outputSha256)
        => (JobId, SourceIdentityKey, TemporaryPath, OutputSha256) = (jobId, sourceIdentityKey, temporaryPath, outputSha256);
    public Guid JobId { get; }
    public string SourceIdentityKey { get; }
    public string TemporaryPath { get; }
    public string OutputSha256 { get; }
}

public enum OwnedOutputRecoveryStatus { RetainedUnvalidated, RetainedValidated, Recorded, Conflict }

/// <summary>A single output transaction. Disposal retains registered artifacts for explicit recovery.</summary>
public sealed class OwnedOutputTransaction : IDisposable
{
    private const int MaximumJournalBytes = 32 * 1024;
    private readonly FileStream sourceGuard;
    private FileStream? temporaryGuard;
    private Journal journal;
    private bool protectedForValidation;
    private bool disposed;

    private OwnedOutputTransaction(FileStream sourceGuard, FileStream temporaryGuard, Journal journal, string journalPath)
        => (this.sourceGuard, this.temporaryGuard, this.journal, JournalPath) = (sourceGuard, temporaryGuard, journal, journalPath);

    public Guid JobId => journal.JobId;
    public string SourceIdentityKey => journal.Source.IdentityKey;
    public string TemporaryPath => journal.TemporaryPath;
    public string JournalPath { get; }

    public static OwnedOutputTransaction Prepare(Guid jobId, InspectionSourceIdentity source, string destination, string journalDirectory)
    {
        if (jobId == Guid.Empty) throw new ArgumentException("A job ID is required.", nameof(jobId));
        string target = Path.GetFullPath(destination);
        string directory = Path.GetDirectoryName(target)!;
        RejectReparsePath(source.Path);
        RejectReparsePath(directory);
        RejectReparsePath(journalDirectory);
        if (StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(source.Path), target) || Path.Exists(target))
            throw new IOException("The destination already exists or aliases the source.");
        var sourceGuard = new FileStream(source.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        FileStream? temporary = null;
        try
        {
            if (!source.MatchesCurrentFile()) throw new IOException("The export source identity is stale.");
            Directory.CreateDirectory(journalDirectory);
            string token = Guid.NewGuid().ToString("N");
            string temp = Path.Combine(directory, $".vidsc-{jobId:N}-{token}.tmp");
            string journalPath = Path.Combine(Path.GetFullPath(journalDirectory), $"vidsc-{jobId:N}-{token}.json");
            var record = new Journal(jobId, token, source, target, temp, "prepared", null, null);
            // Register intent before creating the output; CreateNew establishes exclusive ownership.
            Persist(journalPath, record, create: true);
            temporary = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite);
            record = record with { FileIdentity = Identity(temporary) };
            Persist(journalPath, record);
            return new(sourceGuard, temporary, record, journalPath);
        }
        catch { temporary?.Dispose(); sourceGuard.Dispose(); throw; }
    }

    /// <summary>The caller must first confirm that its owned writer process has exited.</summary>
    public void ProtectForValidation()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (protectedForValidation) return;
        temporaryGuard!.Dispose();
        temporaryGuard = null;
        var guard = new FileStream(TemporaryPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            RejectReparsePath(TemporaryPath);
            if (Identity(guard) != journal.FileIdentity) throw new IOException("The owned temporary output was replaced.");
            temporaryGuard = guard;
            protectedForValidation = true;
        }
        catch { guard.Dispose(); throw; }
    }

    public string ComputeOutputSha256()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!protectedForValidation) throw new InvalidOperationException("Protect the output before validation.");
        temporaryGuard!.Position = 0;
        return Convert.ToHexString(SHA256.HashData(temporaryGuard));
    }

    /// <summary>Caller must confirm all owned writers exited before discarding a failed, unvalidated output.</summary>
    internal void DiscardPreparedOutput()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (journal.State != "prepared") throw new InvalidOperationException("Only unvalidated outputs can be discarded.");
        temporaryGuard?.Dispose();
        temporaryGuard = null;
        RejectReparsePath(TemporaryPath);
        var handle = CreateFile(TemporaryPath, 0x80000000u | 0x00010000u, 1, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle.IsInvalid) { handle.Dispose(); throw new IOException("Cannot protect the failed output for discard."); }
        using var output = new FileStream(handle, FileAccess.Read);
        if (Identity(output) != journal.FileIdentity) throw new IOException("The registered failed output was replaced.");
        IntPtr disposition = Marshal.AllocHGlobal(1);
        try
        {
            Marshal.WriteByte(disposition, 1);
            if (!SetFileInformationByHandle(output.SafeFileHandle, 4, disposition, 1))
                throw new IOException("The owned failed output could not be discarded.", Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
        }
        finally { Marshal.FreeHGlobal(disposition); }
    }

    public void Commit(ValidatedOutputReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (journal.State != "prepared") throw new InvalidOperationException("This transaction was already committed.");
        if (receipt.JobId != JobId || receipt.SourceIdentityKey != SourceIdentityKey ||
            !StringComparer.OrdinalIgnoreCase.Equals(receipt.TemporaryPath, TemporaryPath) ||
            receipt.OutputSha256 != ComputeOutputSha256())
            throw new InvalidDataException("The validation receipt does not match this output.");
        if (!journal.Source.MatchesCurrentFile()) throw new IOException("The source identity changed.");
        // Reacquire with DELETE access only after external validation readers have exited.
        // Recheck both identity and full hash after this handle transition.
        temporaryGuard!.Dispose();
        temporaryGuard = null;
        var handle = CreateFile(TemporaryPath, 0x80000000u | 0x00010000u, 1, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle.IsInvalid) { handle.Dispose(); throw new IOException("Cannot protect the output for promotion."); }
        temporaryGuard = new FileStream(handle, FileAccess.Read);
        RejectReparsePath(TemporaryPath);
        RejectReparsePath(Path.GetDirectoryName(journal.Destination)!);
        if (Identity(temporaryGuard!) != journal.FileIdentity || ComputeOutputSha256() != receipt.OutputSha256)
            throw new IOException("The owned output identity or contents changed.");
        journal = journal with { State = "validated", OutputSha256 = receipt.OutputSha256 };
        Persist(JournalPath, journal);
        RenameWithoutReplacement(temporaryGuard!, journal.Destination);
        journal = journal with { State = "promoted" };
        Persist(JournalPath, journal);
        journal = journal with { State = "recorded" };
        Persist(JournalPath, journal);
        // The recorded result belongs to the caller; release DELETE access for ordinary readers.
        temporaryGuard.Dispose();
        temporaryGuard = null;
    }

    /// <summary>Reconciles only an explicit journal. Recovery never promotes an abandoned temporary file.</summary>
    public static OwnedOutputRecoveryStatus Recover(string journalPath)
    {
        string fullPath = Path.GetFullPath(journalPath);
        RejectReparsePath(fullPath);
        using var input = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > MaximumJournalBytes) throw new InvalidDataException("The output journal is too large.");
        var record = JsonSerializer.Deserialize<Journal>(input) ?? throw new InvalidDataException("The output journal is empty.");
        input.Dispose();
        ValidateJournal(fullPath, record);
        if (record.State == "prepared")
        {
            if (Path.Exists(record.TemporaryPath))
            {
                RejectReparsePath(record.TemporaryPath);
                if (record.FileIdentity is null) return OwnedOutputRecoveryStatus.Conflict;
                using var retained = new FileStream(record.TemporaryPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (Identity(retained) != record.FileIdentity) return OwnedOutputRecoveryStatus.Conflict;
            }
            return OwnedOutputRecoveryStatus.RetainedUnvalidated;
        }
        if (File.Exists(record.TemporaryPath))
        {
            RejectReparsePath(record.TemporaryPath);
            using var retained = new FileStream(record.TemporaryPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Identity(retained) == record.FileIdentity && Convert.ToHexString(SHA256.HashData(retained)) == record.OutputSha256
                ? OwnedOutputRecoveryStatus.RetainedValidated : OwnedOutputRecoveryStatus.Conflict;
        }
        if (!File.Exists(record.Destination)) return OwnedOutputRecoveryStatus.Conflict;
        RejectReparsePath(record.Destination);
        using var final = new FileStream(record.Destination, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (Identity(final) != record.FileIdentity || Convert.ToHexString(SHA256.HashData(final)) != record.OutputSha256)
            return OwnedOutputRecoveryStatus.Conflict;
        Persist(fullPath, record with { State = "recorded" });
        return OwnedOutputRecoveryStatus.Recorded;
    }

    private static void ValidateJournal(string path, Journal record)
    {
        if (record.JobId == Guid.Empty || record.Token?.Length != 32 || !Guid.TryParseExact(record.Token, "N", out _) ||
            Path.GetFileName(path) != $"vidsc-{record.JobId:N}-{record.Token}.json" ||
            string.IsNullOrWhiteSpace(record.Destination) || string.IsNullOrWhiteSpace(record.TemporaryPath) ||
            record.Source is null || string.IsNullOrWhiteSpace(record.Source.Path) ||
            !Path.IsPathFullyQualified(record.Destination) || !Path.IsPathFullyQualified(record.TemporaryPath) ||
            record.Destination != Path.GetFullPath(record.Destination) || record.TemporaryPath != Path.GetFullPath(record.TemporaryPath) ||
            StringComparer.OrdinalIgnoreCase.Equals(record.Destination, Path.GetFullPath(record.Source.Path)) ||
            !StringComparer.OrdinalIgnoreCase.Equals(record.TemporaryPath, Path.Combine(Path.GetDirectoryName(record.Destination)!, $".vidsc-{record.JobId:N}-{record.Token}.tmp")) ||
            record.State is not ("prepared" or "validated" or "promoted" or "recorded") ||
            (record.State != "prepared" && (record.FileIdentity is null || record.OutputSha256?.Length != 64)))
            throw new InvalidDataException("The output journal does not register a valid owned output.");
    }

    private static void Persist(string path, Journal record, bool create = false)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(record);
        if (bytes.Length > MaximumJournalBytes) throw new InvalidDataException("The output journal is too large.");
        string staging = path + "." + Guid.NewGuid().ToString("N") + ".update";
        using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { output.Write(bytes); output.Flush(flushToDisk: true); }
        File.Move(staging, path, overwrite: !create);
    }

    private static void RejectReparsePath(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if (Path.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Reparse paths are not supported by strict output transactions.");
    }

    private static string Identity(FileStream file)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Strict export requires Windows file sharing.");
        if (!GetFileInformationByHandle(file.SafeFileHandle, out var info))
            throw new IOException("Cannot establish the output file identity.", Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
        if (info.NumberOfLinks != 1) throw new IOException("The owned output must have exactly one link.");
        return $"{info.VolumeSerialNumber:X8}:{info.FileIndexHigh:X8}{info.FileIndexLow:X8}";
    }

    private static void RenameWithoutReplacement(FileStream file, string destination)
    {
        byte[] name = System.Text.Encoding.Unicode.GetBytes(destination);
        int rootOffset = IntPtr.Size == 8 ? 8 : 4;
        int lengthOffset = rootOffset + IntPtr.Size;
        int nameOffset = lengthOffset + sizeof(uint);
        // Win32's path conversion consumes a terminated WCHAR string, even though FileNameLength excludes it.
        int bufferSize = nameOffset + name.Length + sizeof(char);
        IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            // FILE_RENAME_INFO.ReplaceIfExists is false; rename the guarded identity, not a reopenable path.
            for (int i = 0; i < nameOffset; i++) Marshal.WriteByte(buffer, i, 0);
            Marshal.WriteInt32(buffer, lengthOffset, name.Length);
            Marshal.Copy(name, 0, buffer + nameOffset, name.Length);
            Marshal.WriteInt16(buffer, nameOffset + name.Length, 0);
            if (!SetFileInformationByHandle(file.SafeFileHandle, 3, buffer, (uint)bufferSize))
                throw new IOException("The output could not be promoted without replacement.", Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass, IntPtr information, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, AccessTime, WriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    private sealed record Journal(Guid JobId, string Token, InspectionSourceIdentity Source, string Destination,
        string TemporaryPath, string State, string? OutputSha256, string? FileIdentity);

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        temporaryGuard?.Dispose();
        sourceGuard.Dispose();
    }
}
