namespace DevEnvironment.Shared;

/// <summary>
/// Where the extension keeps its files. The folder is given by the app (the extension's own data folder) when the
/// extension is set up, which happens before anything here is used.
/// </summary>
internal static class DevEnvironmentPaths
{
    private static string? _dataDir;
    private static bool _adopted;

    public static string DataDir =>
        _dataDir ?? throw new InvalidOperationException("The extension has not been set up yet.");

    public static void Initialize(string dataDir)
    {
        _dataDir = dataDir;
        Directory.CreateDirectory(dataDir);
        AdoptEarlierFiles(dataDir);
    }

    public static string FilePath(string name) => Path.Combine(DataDir, name);

    /// <summary>Log files of the projects the runner starts, one per project.</summary>
    public static string RunnerLogsDir => Path.Combine(DataDir, "logs");

    /// <summary>
    /// Earlier copies kept their files in two places: the app's own data folder (before this was an extension) and the folder of the
    /// bundled Dev Environment extension, next to this one (&lt;app data&gt;/extension-data/dev-environment). The project list, the log layout and
    /// the log history are copied over once. Nothing that already exists is overwritten, and the originals stay where they were.
    /// </summary>
    private static void AdoptEarlierFiles(string dataDir)
    {
        if (_adopted) return;
        _adopted = true;

        var appData = Path.GetFullPath(Path.Combine(dataDir, "..", ".."));
        var bundled = Path.GetFullPath(Path.Combine(dataDir, "..", "dev-environment"));
        foreach (var (folder, logs) in new[] { (bundled, Path.Combine(bundled, "logs")), (appData, Path.Combine(appData, "runner", "logs")) })
        {
            if (string.Equals(folder, Path.GetFullPath(dataDir), StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var name in new[] { "projects.json", "runner-state.json", "logviewer.json" })
                CopyIfMissing(Path.Combine(folder, name), Path.Combine(dataDir, name));

            if (!Directory.Exists(logs)) continue;
            foreach (var log in Directory.GetFiles(logs))
                CopyIfMissing(log, Path.Combine(RunnerLogsDir, Path.GetFileName(log)));
        }
    }

    private static void CopyIfMissing(string from, string to)
    {
        if (!File.Exists(from) || File.Exists(to)) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            // Share mode: a log of a running project is open for writing by that project
            using var source = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var destination = new FileStream(to, FileMode.CreateNew, FileAccess.Write);
            source.CopyTo(destination);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not copied: the extension starts without that file
        }
    }
}
