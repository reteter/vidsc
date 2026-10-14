using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Vidsc.Media.Export;
using Vidsc.Media.Inspection;
using Xunit;

namespace Vidsc.Tests;

public sealed class OwnedOutputTransactionTests : IDisposable
{
    private readonly string root = Path.Combine(RepositoryRoot(), "artifacts", "test-temp", "transaction-" + Guid.NewGuid().ToString("N"));
    private string Source => Path.Combine(root, "source.bin");
    private string Destination => Path.Combine(root, "result.bin");
    private static readonly byte[] Output = [4, 8, 15, 16, 23, 42];

    public OwnedOutputTransactionTests() { Directory.CreateDirectory(root); File.WriteAllBytes(Source, [1, 2, 3]); }

    private OwnedOutputTransaction Prepare() => OwnedOutputTransaction.Prepare(Guid.NewGuid(), InspectionSourceIdentity.Capture(Source), Destination, root);
    private static ValidatedOutputReceipt Receipt(OwnedOutputTransaction transaction) => new(transaction.JobId,
        transaction.SourceIdentityKey, transaction.TemporaryPath, transaction.ComputeOutputSha256());
    private static void WriteOwnedTemporary(OwnedOutputTransaction transaction)
    {
        using var writer = new FileStream(transaction.TemporaryPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        writer.SetLength(0);
        writer.Write(Output);
        writer.Flush(flushToDisk: true);
    }
    private static void WriteAndProtect(OwnedOutputTransaction transaction)
    { WriteOwnedTemporary(transaction); transaction.ProtectForValidation(); }

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Vidsc.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("The repository root was not found.");
    }

    [Fact]
    public void ConfirmedPreparedDiscardRemovesOnlyRegisteredOutput()
    {
        using var transaction = Prepare();
        WriteOwnedTemporary(transaction);
        File.WriteAllBytes(Destination, [99]);
        transaction.DiscardPreparedOutput();
        Assert.False(File.Exists(transaction.TemporaryPath));
        Assert.Equal(new byte[] { 99 }, File.ReadAllBytes(Destination));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Source));
        Assert.Equal(OwnedOutputRecoveryStatus.RetainedUnvalidated, OwnedOutputTransaction.Recover(transaction.JournalPath));
    }

    [Fact]
    public void PreparedDiscardRejectsForeignReplacementIdentity()
    {
        using var transaction = Prepare();
        // Simulate substitution in the handle transition window; normal callers cannot release this guard.
        string temporary = transaction.TemporaryPath;
        var field = typeof(OwnedOutputTransaction).GetField("temporaryGuard", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        ((IDisposable)field.GetValue(transaction)!).Dispose();
        File.Move(temporary, temporary + ".retained");
        File.WriteAllBytes(temporary, [77]);
        Assert.Equal(OwnedOutputRecoveryStatus.Conflict, OwnedOutputTransaction.Recover(transaction.JournalPath));
        Assert.Throws<IOException>(() => transaction.DiscardPreparedOutput());
        Assert.Equal(new byte[] { 77 }, File.ReadAllBytes(temporary));
    }

    [Fact]
    public void PreparedTransactionProtectsSourceAndOwnsRegisteredTemporary()
    {
        using var transaction = Prepare();
        Assert.True(File.Exists(transaction.JournalPath));
        Assert.Throws<IOException>(() => File.WriteAllBytes(Source, [9]));
        Assert.Throws<IOException>(() => File.Delete(Source));
        Assert.Throws<IOException>(() => File.Delete(transaction.TemporaryPath));
        Assert.Equal(OwnedOutputRecoveryStatus.RetainedUnvalidated, OwnedOutputTransaction.Recover(transaction.JournalPath));
    }

    [Fact]
    public void ExistingDestinationAndSourceAliasAreRejectedWithoutChangingBytes()
    {
        File.WriteAllBytes(Destination, Output);
        Assert.Throws<IOException>(() => Prepare());
        Assert.Throws<IOException>(() => OwnedOutputTransaction.Prepare(Guid.NewGuid(), InspectionSourceIdentity.Capture(Source), Source, root));
        Assert.Equal(Output, File.ReadAllBytes(Destination));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Source));
    }

    [Fact]
    public void ReceiptMustMatchJobSourcePathAndFullContents()
    {
        using var transaction = Prepare();
        WriteAndProtect(transaction);
        string hash = transaction.ComputeOutputSha256();
        foreach (var receipt in new[]
        {
            new ValidatedOutputReceipt(Guid.NewGuid(), transaction.SourceIdentityKey, transaction.TemporaryPath, hash),
            new ValidatedOutputReceipt(transaction.JobId, "foreign", transaction.TemporaryPath, hash),
            new ValidatedOutputReceipt(transaction.JobId, transaction.SourceIdentityKey, Source, hash),
            new ValidatedOutputReceipt(transaction.JobId, transaction.SourceIdentityKey, transaction.TemporaryPath, new string('0', 64))
        }) Assert.Throws<InvalidDataException>(() => transaction.Commit(receipt));
        Assert.False(File.Exists(Destination));
        Assert.Throws<IOException>(() => File.WriteAllBytes(transaction.TemporaryPath, [9]));
        Assert.Throws<IOException>(() => File.Delete(transaction.TemporaryPath));
    }

    [Fact]
    public void ValidatedOutputPromotesAndRecoveryReconcilesRecordedResult()
    {
        string journal;
        using (var transaction = Prepare())
        {
            journal = transaction.JournalPath;
            WriteAndProtect(transaction);
            transaction.Commit(Receipt(transaction));
            Assert.False(File.Exists(transaction.TemporaryPath));
            Assert.Equal(Output, File.ReadAllBytes(Destination));
            Assert.Equal(OwnedOutputRecoveryStatus.Recorded, OwnedOutputTransaction.Recover(journal));
        }
        Assert.Equal(OwnedOutputRecoveryStatus.Recorded, OwnedOutputTransaction.Recover(journal));
    }

    [Fact]
    public void LateConflictPreservesBothFilesAndRetainsValidatedTemporary()
    {
        string journal;
        string temporary;
        using (var transaction = Prepare())
        {
            journal = transaction.JournalPath;
            temporary = transaction.TemporaryPath;
            WriteAndProtect(transaction);
            File.WriteAllBytes(Destination, [99]);
            Assert.Throws<IOException>(() => transaction.Commit(Receipt(transaction)));
            Assert.Throws<InvalidOperationException>(() => transaction.DiscardPreparedOutput());
        }
        Assert.Equal(new byte[] { 99 }, File.ReadAllBytes(Destination));
        Assert.Equal(Output, File.ReadAllBytes(temporary));
        Assert.Equal(OwnedOutputRecoveryStatus.RetainedValidated, OwnedOutputTransaction.Recover(journal));
    }

    [Theory]
    [InlineData("validated")]
    [InlineData("promoted")]
    public void RecoveryReconcilesPromotionJournalGapByIdentityAndHash(string crashState)
    {
        string journal;
        using (var transaction = Prepare())
        {
            WriteAndProtect(transaction);
            transaction.Commit(Receipt(transaction));
            journal = transaction.JournalPath;
        }
        var record = JsonNode.Parse(File.ReadAllText(journal))!;
        record["State"] = crashState;
        File.WriteAllText(journal, record.ToJsonString());
        Assert.Equal(OwnedOutputRecoveryStatus.Recorded, OwnedOutputTransaction.Recover(journal));
        Assert.Equal("recorded", JsonNode.Parse(File.ReadAllText(journal))!["State"]!.GetValue<string>());
    }

    [Fact]
    public void PreparedJournalNeverCertifiesAnOutputEvenWithMatchingHash()
    {
        string journal;
        using (var transaction = Prepare()) { journal = transaction.JournalPath; WriteOwnedTemporary(transaction); }
        var record = JsonNode.Parse(File.ReadAllText(journal))!;
        record["OutputSha256"] = Convert.ToHexString(SHA256.HashData(Output));
        File.WriteAllText(journal, record.ToJsonString());
        Assert.Equal(OwnedOutputRecoveryStatus.RetainedUnvalidated, OwnedOutputTransaction.Recover(journal));
        Assert.False(File.Exists(Destination));
    }

    [Fact]
    public void PreparedRecoveryRejectsReplacedTemporaryIdentity()
    {
        string journal;
        string temporary;
        using (var transaction = Prepare()) { journal = transaction.JournalPath; temporary = transaction.TemporaryPath; }
        // Keep the old file allocated so the filesystem cannot reuse its identifier.
        File.Move(temporary, temporary + ".retained");
        File.WriteAllBytes(temporary, [77]);
        Assert.Equal(OwnedOutputRecoveryStatus.Conflict, OwnedOutputTransaction.Recover(journal));
        Assert.Equal(new byte[] { 77 }, File.ReadAllBytes(temporary));
        Assert.False(File.Exists(Destination));
    }

    [Fact]
    public void ForeignTemporaryPathIsRejectedWithoutMutatingEitherFile()
    {
        string journal;
        using (var transaction = Prepare()) journal = transaction.JournalPath;
        var record = JsonNode.Parse(File.ReadAllText(journal))!;
        record["TemporaryPath"] = Source;
        File.WriteAllText(journal, record.ToJsonString());
        string before = File.ReadAllText(journal);
        Assert.Throws<InvalidDataException>(() => OwnedOutputTransaction.Recover(journal));
        Assert.Equal(before, File.ReadAllText(journal));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Source));
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
