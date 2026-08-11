namespace BrowserSync.Host;

public static class AppPaths
{
    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BrowserSync");

    public static string DatabasePath => Path.Combine(DataDirectory, "browsersync.db");

    public static string LogsDirectory => Path.Combine(DataDirectory, "logs");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }
}
