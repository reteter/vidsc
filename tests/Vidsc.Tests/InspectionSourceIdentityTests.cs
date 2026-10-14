using Vidsc.Media.Inspection;
using Xunit;

namespace Vidsc.Tests;

public sealed class InspectionSourceIdentityTests
{
    [Fact]
    public void IdentityRejectsContentMutationEvenWithPreservedSizeAndTimestamp()
    {
        string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vidsc-identity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = System.IO.Path.Combine(directory, "fixture.bin");
        try
        {
            File.WriteAllBytes(path, new byte[200_000]);
            var identity = InspectionSourceIdentity.Capture(path);
            Assert.True(identity.MatchesCurrentFile());
            using (var file = File.OpenWrite(path)) { file.Position = 100_000; file.WriteByte(1); }
            File.SetLastWriteTimeUtc(path, new DateTime(identity.LastWriteUtcTicks, DateTimeKind.Utc));
            Assert.False(identity.MatchesCurrentFile());
        }
        finally { Directory.Delete(directory, true); }
    }
}
