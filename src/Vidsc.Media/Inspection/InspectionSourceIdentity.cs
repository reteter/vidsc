using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Vidsc.Media.Inspection;

/// <summary>A bounded cache identity, not a substitute for guarding a source during reads.</summary>
public sealed record InspectionSourceIdentity(string Path, long Size, long LastWriteUtcTicks, string IdentityKey)
{
    public static InspectionSourceIdentity Capture(string path)
    {
        string fullPath = System.IO.Path.GetFullPath(path);
        using var source = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        long size = source.Length;
        long modified = File.GetLastWriteTimeUtc(fullPath).Ticks;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(fullPath.ToUpperInvariant() + "\n" +
            size.ToString(CultureInfo.InvariantCulture) + "\n" + modified.ToString(CultureInfo.InvariantCulture)));
        byte[] buffer = new byte[64 * 1024];
        foreach (long offset in new[] { 0L, Math.Max(0, size / 2 - buffer.Length / 2), Math.Max(0, size - buffer.Length) }.Distinct())
        {
            source.Position = offset;
            int remaining = (int)Math.Min(buffer.Length, size - offset);
            int read = 0;
            while (read < remaining)
            {
                int count = source.Read(buffer, read, remaining - read);
                if (count == 0) throw new IOException("Source changed while capturing inspection identity.");
                read += count;
            }
            hash.AppendData(buffer.AsSpan(0, read));
        }
        if (source.Length != size || File.GetLastWriteTimeUtc(fullPath).Ticks != modified)
            throw new IOException("Source changed while capturing inspection identity.");
        return new(fullPath, size, modified, Convert.ToHexString(hash.GetHashAndReset()));
    }

    public bool MatchesCurrentFile() => Capture(Path).IdentityKey == IdentityKey;
}
