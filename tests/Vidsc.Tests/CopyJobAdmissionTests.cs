using System.Diagnostics;
using System.Reflection;
using Vidsc.Media.Export;
using Vidsc.Media.Tooling;
using Xunit;

namespace Vidsc.Tests;

[CollectionDefinition("Copy job admission", DisableParallelization = true)]
public sealed class CopyJobAdmissionCollection { }

[Collection("Copy job admission")]
public sealed class CopyJobAdmissionTests
{
    [Fact]
    public async Task IndependentCallersSerializeAndCancelledWaiterDoesNotConsumeSlot()
    {
        using var first = await CopyJobAdmission.EnterAsync(Guid.NewGuid(), default);
        using var cancellation = new CancellationTokenSource();
        var cancelled = CopyJobAdmission.EnterAsync(Guid.NewGuid(), cancellation.Token);
        var next = CopyJobAdmission.EnterAsync(Guid.NewGuid(), default);
        Assert.False(next.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        first.Dispose();
        first.Dispose();
        using var second = await next.WaitAsync(TimeSpan.FromSeconds(5));
        second.EnsureActive();
        Assert.Throws<ObjectDisposedException>(first.EnsureActive);
        var third = CopyJobAdmission.EnterAsync(Guid.NewGuid(), default);
        Assert.False(third.IsCompleted);
        second.Dispose();
        using var last = await third.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task EmptyJobIdentityIsRejected()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => CopyJobAdmission.EnterAsync(Guid.Empty, default));
    }

    [Theory]
    [InlineData("live")]
    [InlineData("null")]
    [InlineData("wrong-pid")]
    [InlineData("invalid-pid")]
    public async Task UnconfirmedExitKeepsAdmissionClosed(string kind)
    {
        var lease = await CopyJobAdmission.EnterAsync(Guid.NewGuid(), default);
        using var cancellation = new CancellationTokenSource();
        ToolProcessIdentity? identity = CurrentProcess();
        int processId = identity.ProcessId;
        if (kind == "null") identity = null;
        if (kind == "wrong-pid") identity = identity! with { ProcessId = int.MaxValue };
        if (kind == "invalid-pid") processId = 0;
        bool cleaned = false;
        try
        {
            lease.Retain(identity, processId, () => cleaned = true);
            lease.Dispose();
            Assert.Throws<InvalidOperationException>(lease.EnsureActive);
            Assert.False(CopyJobAdmission.TryReconcile(lease.JobId));
            Assert.False(CopyJobAdmission.TryReconcile(Guid.NewGuid()));
            var waiter = CopyJobAdmission.EnterAsync(Guid.NewGuid(), cancellation.Token);
            Assert.False(waiter.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
            Assert.False(cleaned);
        }
        finally { RestoreRetainedOwner(lease); }
    }

    [Fact]
    public async Task ReusedProcessIdCleansBeforeReleaseAndCleanupFailureCanRetry()
    {
        var lease = await CopyJobAdmission.EnterAsync(Guid.NewGuid(), default);
        var identity = CurrentProcess() with { StartedAtUtc = DateTimeOffset.MinValue };
        using var cancellation = new CancellationTokenSource();
        Task<CopyJobAdmission.Lease>? waiter = null;
        int attempts = 0;
        try
        {
            lease.Retain(identity, identity.ProcessId, () =>
            {
                Assert.False(waiter!.IsCompleted);
                if (++attempts == 1) throw new IOException("Cleanup is temporarily unavailable.");
            });
            waiter = CopyJobAdmission.EnterAsync(Guid.NewGuid(), cancellation.Token);
            Assert.False(CopyJobAdmission.TryReconcile(lease.JobId));
            Assert.False(waiter.IsCompleted);
            Assert.True(CopyJobAdmission.TryReconcile(lease.JobId));
            Assert.False(CopyJobAdmission.TryReconcile(lease.JobId));
            using var next = await waiter.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, attempts);
            Assert.Throws<ObjectDisposedException>(lease.EnsureActive);
        }
        finally
        {
            cancellation.Cancel();
            RestoreRetainedOwner(lease);
            if (waiter is not null)
            {
                try { (await waiter).Dispose(); }
                catch (OperationCanceledException) { }
            }
        }
    }

    [Fact]
    public async Task AbsentOriginalProcessReconcilesExactlyOnce()
    {
        // A positive PID with matching recorded identity is required even when the process is gone.
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(int.MaxValue));
        var lease = await CopyJobAdmission.EnterAsync(Guid.NewGuid(), default);
        int cleanups = 0;
        try
        {
            lease.Retain(new(int.MaxValue, "exited-test-process", DateTimeOffset.MinValue),
                int.MaxValue, () => cleanups++);
            Assert.True(CopyJobAdmission.TryReconcile(lease.JobId));
            Assert.False(CopyJobAdmission.TryReconcile(lease.JobId));
            Assert.Equal(1, cleanups);
            using var next = await CopyJobAdmission.EnterAsync(Guid.NewGuid(), default).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { RestoreRetainedOwner(lease); lease.Dispose(); }
    }

    private static ToolProcessIdentity CurrentProcess()
    {
        using var process = Process.GetCurrentProcess();
        return new(process.Id, "test-process", process.StartTime.ToUniversalTime());
    }

    private static void RestoreRetainedOwner(CopyJobAdmission.Lease lease)
    {
        // Unknown identities deliberately cannot reconcile; restore only this test's owner.
        var owner = typeof(CopyJobAdmission).GetField("retainedOwner", BindingFlags.Static | BindingFlags.NonPublic)!;
        if (!ReferenceEquals(owner.GetValue(null), lease)) return;
        owner.SetValue(null, null);
        var slot = (SemaphoreSlim)typeof(CopyJobAdmission).GetField("Slot", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        slot.Release();
    }
}
