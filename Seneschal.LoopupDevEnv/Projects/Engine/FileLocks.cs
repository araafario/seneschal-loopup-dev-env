using System.Runtime.InteropServices;

namespace DevEnvironment.Projects;

/// <summary>Asks Windows which processes have a file open or loaded, through the Restart Manager (the way MSBuild finds the culprit of "file is locked").</summary>
internal static class FileLocks
{
    private const int ErrorMoreData = 234;

    public static IReadOnlyList<int> ProcessesHolding(IReadOnlyCollection<string> files)
    {
        if (files.Count == 0 || !OperatingSystem.IsWindows()) return [];

        if (RmStartSession(out var session, 0, Guid.NewGuid().ToString("N")) != 0) return [];
        try
        {
            if (RmRegisterResources(session, (uint)files.Count, [.. files], 0, null, 0, null) != 0) return [];

            uint needed = 0, count = 0, reasons = 0;
            var result = RmGetList(session, out needed, ref count, null, ref reasons);
            if (result == 0) return [];
            if (result != ErrorMoreData) return [];

            var info = new RmProcessInfo[needed];
            count = needed;
            result = RmGetList(session, out needed, ref count, info, ref reasons);
            return result == 0 ? info.Take((int)count).Select(i => i.Process.ProcessId).Distinct().ToList() : [];
        }
        finally
        {
            RmEndSession(session);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RmUniqueProcess
    {
        public int ProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RmProcessInfo
    {
        public RmUniqueProcess Process;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string AppName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string ServiceShortName;

        public int ApplicationType;
        public uint AppStatus;
        public uint TsSessionId;

        [MarshalAs(UnmanagedType.Bool)]
        public bool Restartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint sessionHandle, int flags, string sessionKey);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint sessionHandle);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint sessionHandle, uint fileCount, string[]? files, uint applicationCount, RmUniqueProcess[]? applications, uint serviceCount, string[]? services);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint sessionHandle, out uint procInfoNeeded, ref uint procInfo, [In, Out] RmProcessInfo[]? affectedApps, ref uint rebootReasons);
}
