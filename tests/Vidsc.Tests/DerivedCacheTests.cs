using Vidsc.Media.Inspection;
using Vidsc.App.Services;
using Xunit;

namespace Vidsc.Tests;

public sealed class DerivedCacheTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "vidsc-cache-" + Guid.NewGuid().ToString("N"));
    private string Category(string category) => Path.Combine(root, category);
    private static string Key(char value) => new(value, 64);

    [Fact]
    public void ReadRefreshesLruAcrossCategoriesAndPreservesForeignFiles()
    {
        DerivedCache.Write(Category("scenes"), Key('A'), "1111", 12);
        DerivedCache.Write(Category("waveform"), Key('B'), "2222", 12);
        File.SetLastWriteTimeUtc(Path.Combine(Category("scenes"), Key('A') + ".json"), DateTime.UtcNow.AddDays(-2));
        File.SetLastWriteTimeUtc(Path.Combine(Category("waveform"), Key('B') + ".json"), DateTime.UtcNow.AddDays(-1));
        Assert.Equal("1111", DerivedCache.Read(Category("scenes"), Key('A'), 16));
        File.WriteAllText(Path.Combine(Category("scenes"), "project.json"), "preserve");
        DerivedCache.Write(Category("visible-thumbnails"), Key('C'), "33333333", 12);
        Assert.Null(DerivedCache.Read(Category("waveform"), Key('B'), 16));
        Assert.Equal("1111", DerivedCache.Read(Category("scenes"), Key('A'), 16));
        Assert.Equal("preserve", File.ReadAllText(Path.Combine(Category("scenes"), "project.json")));
    }

    [Fact]
    public void LockedEntryCannotCauseBudgetOverflowOrReplacePreviousResult()
    {
        DerivedCache.Write(Category("scenes"), Key('A'), "1111", 8);
        DerivedCache.Write(Category("waveform"), Key('B'), "2222", 8);
        using (File.Open(Path.Combine(Category("scenes"), Key('A') + ".json"), FileMode.Open, FileAccess.Read, FileShare.Read))
            DerivedCache.Write(Category("waveform"), Key('B'), "33333333", 8);
        Assert.Equal("2222", DerivedCache.Read(Category("waveform"), Key('B'), 16));
        Assert.Empty(Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void OptionalCacheIoFailureIsAMissAndDoesNotThrow()
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Category("scenes"), "blocked");
        Assert.Null(DerivedCache.Read(Category("scenes"), Key('A'), 8));
        DerivedCache.Write(Category("scenes"), Key('A'), "1111", 8);
        Assert.Equal("blocked", File.ReadAllText(Category("scenes")));
    }

    [Theory]
    [InlineData(256UL, 8192UL, true)]
    [InlineData(1024UL, 8192UL, false)]
    [InlineData(0UL, 0UL, false)]
    public void PressurePolicyUsesAvailableMemoryAndGpuBudget(ulong availableMiB, ulong totalMiB, bool expected)
    {
        Assert.Equal(expected, CachePressure.ShouldReclaim(availableMiB * 1048576, totalMiB * 1048576));
        Assert.True(CachePressure.ShouldReclaim(0, 0, 90, 100));
        Assert.False(CachePressure.ShouldReclaim(0, 0, 89, 100));
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
