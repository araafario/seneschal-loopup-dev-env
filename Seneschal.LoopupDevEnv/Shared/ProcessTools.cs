using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DevEnvironment.Shared;

/// <summary>
/// What every runner needs to start, find again and stop a detached process: a clean environment, a log that does not grow
/// without end, a check that a saved process id is still the process that was started, and a kill that takes the children along.
/// </summary>
internal static class ProcessTools
{
    private const long MaxLogBytes = 5 * 1024 * 1024;

    // Settings of this app that a started process must not inherit: they would change what port it listens on or which
    // environment it runs in, and the debugger and hot reload hooks of the process that runs Seneschal itself.
    private static readonly string[] ScrubbedEnvironment =
    [
        "ASPNETCORE_URLS", "ASPNETCORE_HTTP_PORTS", "ASPNETCORE_HTTPS_PORTS", "ASPNETCORE_HTTPS_PORT", "ASPNETCORE_ENVIRONMENT",
        "DOTNET_ENVIRONMENT", "DOTNET_URLS", "DOTNET_LAUNCH_PROFILE", "DOTNET_STARTUP_HOOKS", "DOTNET_MODIFIABLE_ASSEMBLIES",
        "DOTNET_WATCH", "DOTNET_HOTRELOAD_NAMEDPIPE_NAME", "DOTNET_USE_POLLING_FILE_WATCHER", "ASPNETCORE_HOSTINGSTARTUPASSEMBLIES",
    ];

    /// <summary>Removes the settings of this app (and of Seneschal itself) from what the new process will inherit.</summary>
    public static void CleanEnvironment(ProcessStartInfo start)
    {
        foreach (var name in ScrubbedEnvironment)
            start.Environment.Remove(name);
        foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("SENESCHAL_", StringComparison.OrdinalIgnoreCase)).ToList())
            start.Environment.Remove(key);
    }

    /// <summary>True when the process with this id exists and is the one that was started (not a reused id).</summary>
    public static bool IsAlive(int pid, long startTicks)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited && Math.Abs(process.StartTime.ToUniversalTime().Ticks - startTicks) < TimeSpan.FromSeconds(2).Ticks;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Ends the process and everything it started, and waits a moment for it. Does nothing when it is already gone.</summary>
    public static void KillTree(int pid, long startTicks)
    {
        if (!IsAlive(pid, startTicks)) return;
        KillTreeById(pid);
    }

    /// <summary>
    /// Ends whatever still has a service's log open, with its children. A service can leave a helper behind (a dev server it started)
    /// once its own process is gone, and that helper keeps the log locked so the service cannot start again. Returns how many it ended.
    /// </summary>
    public static int KillLogHolders(string log)
    {
        var holders = FileLockers.Find(log);
        foreach (var pid in holders)
            KillTreeById(pid);
        return holders.Count;
    }

    /// <summary>
    /// Ends a process and all of its descendants. "npm start" runs cmd, node, cmd and node again, and Process.Kill(entireProcessTree)
    /// leaves the last one running when its parent dies first, so the work is left to taskkill, which collects the whole tree before it
    /// kills anything. Process.Kill is only the fallback.
    /// </summary>
    public static void KillTreeById(int pid)
    {
        try
        {
            using var taskkill = Process.Start(new ProcessStartInfo("taskkill.exe")
            {
                ArgumentList = { "/PID", pid.ToString(), "/T", "/F" },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (taskkill is not null)
            {
                taskkill.StandardOutput.ReadToEnd();
                taskkill.StandardError.ReadToEnd();
                taskkill.WaitForExit(8000);
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Fall back to the built-in kill below
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone
        }
    }

    /// <summary>
    /// True when the process runs below one of <paramref name="ancestors"/> (its parent, the parent of that, and so on). The program a
    /// runner started is a few processes down from the one it launched, and a second copy must not mistake it for a stranger.
    /// </summary>
    public static bool IsDescendantOf(int pid, ISet<int> ancestors, int maxDepth = 12)
    {
        var current = pid;
        for (var i = 0; i < maxDepth; i++)
        {
            if (ParentOf(current) is not { } parent || parent <= 0) return false;
            if (ancestors.Contains(parent)) return true;
            current = parent;
        }
        return false;
    }

    private static int? ParentOf(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            var info = new BasicInformation();
            return NtQueryInformationProcess(process.Handle, 0, ref info, Marshal.SizeOf<BasicInformation>(), out _) == 0
                ? (int)info.InheritedFromUniqueProcessId
                : null;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicInformation
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2a;
        public IntPtr Reserved2b;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int informationClass, ref BasicInformation info, int size, out int returned);

    /// <summary>Makes sure the log's folder exists, keeps the last log aside when it got big, and writes the banner line.</summary>
    public static void PrepareLog(string log, string banner)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        // Another process may still hold the file (a log window reading it, a copy that has not exited yet), so none of this may fail the start
        try
        {
            if (File.Exists(log) && new FileInfo(log).Length > MaxLogBytes)
                File.Move(log, log + ".old", overwrite: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        try
        {
            using var stream = new FileStream(log, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            using var writer = new StreamWriter(stream);
            writer.WriteLine(banner);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
