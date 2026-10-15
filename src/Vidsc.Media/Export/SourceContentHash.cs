using System.Security.Cryptography;

namespace Vidsc.Media.Export;

internal static class SourceContentHash
{
    // Full-content identity must remain cancellable even when only a short range is retained.
    internal static async Task<string> ComputeAsync(Stream source, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        source.Position = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            hash.AppendData(buffer, 0, count);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
