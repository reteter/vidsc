namespace Vidsc.App.Services;

/// <summary>
/// Filesystem locations the application owns.
/// </summary>
/// <remarks>
/// The application directory is treated as read-only (it is the delivered bundle). Everything the
/// application writes at runtime — logs, caches and temporary export files — goes to the per-user
/// application data directory unless the operator overrides it with <c>VIDSC_WORK_DIR</c>.
/// Source media is never written to.
/// </remarks>
public sealed record AppPaths(string ApplicationDirectory, string WorkDirectory, string LogDirectory, string CacheDirectory)
{
    public const string WorkDirectoryOverrideVariable = "VIDSC_WORK_DIR";

    public static AppPaths Discover()
    {
        string applicationDirectory = Path.GetFullPath(AppContext.BaseDirectory);

        string? overridden = Environment.GetEnvironmentVariable(WorkDirectoryOverrideVariable);
        string workDirectory = string.IsNullOrWhiteSpace(overridden)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vidsc")
            : Path.GetFullPath(overridden);

        string logDirectory = Path.Combine(workDirectory, "logs");
        string cacheDirectory = Path.Combine(workDirectory, "cache");

        return new AppPaths(applicationDirectory, workDirectory, logDirectory, cacheDirectory);
    }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(WorkDirectory);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(CacheDirectory);
    }
}
