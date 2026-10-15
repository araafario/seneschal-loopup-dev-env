using System.Runtime.InteropServices;
using System.Text;

namespace DevEnvironment.Shared;

/// <summary>
/// Finds the processes that have a file open, with the Windows Restart Manager. A service's log is opened by its own output
/// redirect and handed down to everything it starts, so whatever still holds the log belongs to that service, even after the
/// process that started it is gone and the process tree can no longer be followed.
/// </summary>
internal static class FileLockers
{
    private const int ErrorMoreData = 234;

    public static IReadOnlyList<int> Find(string path)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(path)) return [];

        var key = new StringBuilder(33);
        if (RmStartSession(out var session, 0, key) != 0) return [];
        try
        {
            if (RmRegisterResources(session, 1, [path], 0, null, 0, null) != 0) return [];

            uint needed = 0, count = 0, reasons = 0;
            var result = RmGetList(session, out needed, ref count, null, ref reasons);
            for (var attempt = 0; result == ErrorMoreData && attempt < 3; attempt++)
            {
                var info = new ProcessInfo[needed];
                count = needed;
                result = RmGetList(session, out needed, ref count, info, ref reasons);
                if (result == 0)
                    return info.Take((int)count).Select(i => i.Process.ProcessId).Where(id => id != Environment.ProcessId).Distinct().ToList();
            }
            return [];
        }
        finally
        {
            RmEndSession(session);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UniqueProcess
    {
        public int ProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessInfo
    {
        public UniqueProcess Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string AppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string ServiceShortName;
        public int ApplicationType;
        public uint AppStatus;
        public uint SessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool Restartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint session, int flags, StringBuilder key);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint session, uint files, string[]? fileNames, uint apps, UniqueProcess[]? processes, uint services, string[]? serviceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint session, out uint needed, ref uint count, [In, Out] ProcessInfo[]? info, ref uint reasons);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint session);
}
