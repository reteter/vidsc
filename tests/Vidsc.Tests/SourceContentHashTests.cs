using System.Security.Cryptography;
using Vidsc.Media.Export;
using Xunit;

namespace Vidsc.Tests;

public sealed class SourceContentHashTests
{
    [Fact]
    public async Task HashesCompleteSourceOnEachPassRegardlessOfCurrentPosition()
    {
        byte[] data = Enumerable.Range(0, 150_001).Select(i => (byte)(i % 251)).ToArray();
        using var stream = new MemoryStream(data);
        stream.Position = data.Length - 1;
        string expected = Convert.ToHexString(SHA256.HashData(data));
        Assert.Equal(expected, await SourceContentHash.ComputeAsync(stream, default));
        Assert.Equal(expected, await SourceContentHash.ComputeAsync(stream, default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingReadObservesUserCancellationOrInvocationDeadline(bool deadline)
    {
        using var requested = new CancellationTokenSource();
        using var invocation = CancellationTokenSource.CreateLinkedTokenSource(requested.Token);
        using var stream = new PendingReadStream();
        Task<string> hashing = SourceContentHash.ComputeAsync(stream, invocation.Token);
        await stream.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (deadline) invocation.CancelAfter(TimeSpan.FromMilliseconds(25));
        else requested.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => hashing.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(!deadline, requested.IsCancellationRequested);
    }

    private sealed class PendingReadStream : MemoryStream
    {
        internal TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
