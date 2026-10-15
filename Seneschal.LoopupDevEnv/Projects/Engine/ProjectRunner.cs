using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text.Json;
using DevEnvironment.Shared;

namespace DevEnvironment.Projects;

public enum RunStatus
{
    Stopped,
    Starting,
    Running,

    /// <summary>It was started by the runner and has since ended (crashed, or was closed by hand).</summary>
    Exited,

    /// <summary>Something else is listening on the project's port, for example the project running in Visual Studio.</summary>
    External,
}

public sealed record RunInfo(RunStatus Status, int? Pid, DateTime? StartedAt, string? Message)
{
    /// <summary>The state in words, as shown in a row and in the log window of a project.</summary>
    public string Text => Status switch
    {
        RunStatus.Starting => "Starting",
        RunStatus.Running => $"Running since {StartedAt!.Value.ToLocalTime():HH:mm}",
        RunStatus.Exited => Message ?? "Exited",
        RunStatus.External => Pid is { } pid ? $"Running elsewhere (id {pid})" : "Running elsewhere",
        _ => "Stopped",
    };
}

public sealed record RunResult(bool Ok, string Message);

/// <summary>
/// Starts and stops the projects. Each one is started with "dotnet run" in its own process that writes to a log file and is
/// not tied to this app, so it keeps running when Seneschal restarts; the process id is saved and picked up again at startup.
/// </summary>
public sealed class ProjectRunner : IDisposable
{
    private static readonly string StateFile = DevEnvironmentPaths.FilePath("runner-state.json");

    private sealed class Tracked
    {
        public int Pid;
        public long StartTicks;
        public DateTime StartedAt;
        public bool Ready;

        /// <summary>It was started with a build, so a copy of the same project must wait until that build is over.</summary>
        public bool Building;
        public bool Exited;
        public int? ExitCode;
    }

    private sealed record Saved(int Pid, long StartTicks, DateTime StartedAt);

    private readonly ProjectStore _store;
    private readonly ProjectCatalog _catalog;
    private readonly object _gate = new();
    private readonly Dictionary<string, Tracked> _tracked = [];

    // Processes outside the runner that have a project's own files open (so the project is running somewhere else)
    private Dictionary<string, List<int>> _external = [];
    private readonly Dictionary<string, (DateTime At, List<string> Files)> _outputFiles = [];
    private int _ticks;
    private HashSet<int> _listening = [];
    private string _signature = "";
    private readonly Timer _timer;

    public event Action? Changed;

    public ProjectRunner(ProjectStore store, ProjectCatalog catalog)
    {
        _store = store;
        _catalog = catalog;
        LoadState();
        RefreshListening();
        _timer = new Timer(_ => Poll(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public static string LogPath(string id) => Path.Combine(DevEnvironmentPaths.RunnerLogsDir, $"{id}.log");

    public RunInfo Get(string id)
    {
        lock (_gate)
        {
            if (_tracked.TryGetValue(id, out var t))
            {
                if (t.Exited)
                    return new RunInfo(RunStatus.Exited, t.Pid, t.StartedAt, t.ExitCode is { } code ? $"Exited with code {code}" : "Exited");
                return new RunInfo(t.Ready ? RunStatus.Running : RunStatus.Starting, t.Pid, t.StartedAt, null);
            }
            if (_external.TryGetValue(id, out var holders) && holders.Count > 0)
                return new RunInfo(RunStatus.External, holders[0], null, $"Running in another process (id {holders[0]}), not started here");
            var busy = PortsOf(id).FirstOrDefault(_listening.Contains);
            return busy != 0
                ? new RunInfo(RunStatus.External, null, null, $"Something else is listening on port {busy}")
                : new RunInfo(RunStatus.Stopped, null, null, null);
        }
    }

    public RunResult Start(string id)
    {
        var entry = _store.Get(id);
        if (entry is null) return new RunResult(false, "That project is no longer in the list.");
        var info = _catalog.Info(entry.ProjectPath);
        if (info is null) return new RunResult(false, $"The project file was not found: {entry.ProjectPath}");

        lock (_gate)
        {
            if (_tracked.TryGetValue(id, out var existing) && !existing.Exited)
                return new RunResult(false, $"{entry.Name} is already running.");
        }

        // Already running somewhere else (Visual Studio, a terminal)? Starting a second copy would fail on its locked files.
        var holders = FindExternal(id, info);
        if (holders.Count > 0)
        {
            lock (_gate) _external[id] = holders;
            Notify();
            return new RunResult(false, $"{entry.Name} is already running in another process ({Describe(holders)}). Stop it there first, or use Stop here.");
        }

        RefreshListening();
        var busy = PortsOf(id).Where(_listening.Contains).ToList();
        if (busy.Count > 0)
            return new RunResult(false, $"Port {busy[0]} is already in use (another program, or {entry.Name} running in Visual Studio).");

        var log = LogPath(id);
        var launch = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            WorkingDirectory = info.Directory,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        ProcessTools.CleanEnvironment(launch);
        // An instance is also told its offset as the setting PortOffset, for services that fix their port in code and read it
        if (entry.PortOffset != 0) launch.Environment["PortOffset"] = entry.PortOffset.ToString();
        foreach (var (key, value) in entry.Env)
            launch.Environment[key] = value;

        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? $"\"{host}\"" : "dotnet";
        var profile = info.Profile(entry.Profile) is not null ? $"--launch-profile \"{entry.Profile}\"" : "--no-launch-profile";
        // A copy of a project listens on the project's addresses moved by its port offset
        var urls = entry.PortOffset != 0 && info.Profile(entry.Profile) is { Urls.Length: > 0 } copyProfile
            ? string.Join(';', copyProfile.Urls.Select(u => ProjectReader.ShiftUrl(u, entry.PortOffset)))
            : null;
        var appArgs = ((urls is null ? "" : $"--urls \"{urls}\" ") + entry.Args.Trim()).Trim();
        var args = appArgs.Length == 0 ? "" : $" -- {appArgs}";
        // cmd redirects the output into the log file, so nothing depends on this app staying alive to read a pipe
        string RunLine(bool noBuild) => $"/d /s /c \"{dotnet} run --project \"{info.Path}\"{(noBuild ? " --no-build" : "")} {profile}{args} >> \"{log}\" 2>&1\"";

        // Copies of one project share its bin folder: one builds, the others wait for that build and then run what it made
        var gate = FamilyGate(info.Path);
        gate.Wait();
        try
        {
            WaitForBuild(id, info.Path);
            var noBuild = SiblingRunning(id, info.Path);
            launch.Arguments = RunLine(noBuild);
            // Helpers an earlier run left behind (a dev server the service started) still hold the log and would make this start fail
            ProcessTools.KillLogHolders(log);
            PrepareLog(log, $"=== {entry.Name} started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
            var process = Process.Start(launch) ?? throw new InvalidOperationException("The process did not start.");
            var tracked = new Tracked
            {
                Pid = process.Id,
                StartTicks = process.StartTime.ToUniversalTime().Ticks,
                StartedAt = DateTime.UtcNow,
                Building = !noBuild,
            };
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) =>
            {
                try { lock (_gate) tracked.ExitCode = process.ExitCode; } catch (InvalidOperationException) { }
            };
            lock (_gate) _tracked[id] = tracked;
            SaveState();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return new RunResult(false, $"Could not start {entry.Name}: {ex.Message}");
        }
        finally
        {
            gate.Release();
        }

        Notify();
        return new RunResult(true, $"Starting {entry.Name}");
    }

    public RunResult Stop(string id)
    {
        var name = _store.Get(id)?.Name ?? id;
        Tracked? tracked;
        lock (_gate) _tracked.TryGetValue(id, out tracked);

        if (tracked is null)
        {
            return Get(id).Status == RunStatus.External
                ? new RunResult(false, $"{name} was not started here (for example it runs in Visual Studio), so stop it where it was started.")
                : new RunResult(true, $"{name} is not running.");
        }

        try
        {
            Kill(tracked);
            ProcessTools.KillLogHolders(LogPath(id));
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return new RunResult(false, $"Could not stop {name}: {ex.Message}");
        }

        lock (_gate) _tracked.Remove(id);
        SaveState();
        try { File.AppendAllText(LogPath(id), $"=== {name} stopped {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==={Environment.NewLine}"); }
        catch (IOException) { }
        RefreshListening();
        Notify();
        return new RunResult(true, $"Stopped {name}.");
    }

    /// <summary>Stops the processes that have this project's files open although they were not started here. Ask the user first.</summary>
    public RunResult StopExternal(string id)
    {
        var entry = _store.Get(id);
        var info = entry is null ? null : _catalog.Info(entry.ProjectPath);
        if (entry is null || info is null) return new RunResult(false, "That project is no longer available.");

        var holders = FindExternal(id, info);
        if (holders.Count == 0) return new RunResult(true, $"{entry.Name} is not running in another process.");

        foreach (var pid in holders)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
            catch (ArgumentException)
            {
                // Already gone
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return new RunResult(false, $"Could not stop process {pid}: {ex.Message}");
            }
        }

        lock (_gate) _external[id] = [];
        RefreshListening();
        Notify();
        return new RunResult(true, $"Stopped {entry.Name} ({Describe(holders)}).");
    }

    public RunResult Restart(string id)
    {
        var stopped = Stop(id);
        if (!stopped.Ok) return stopped;

        // Give the old process a moment to release its port
        for (var i = 0; i < 20; i++)
        {
            RefreshListening();
            if (!PortsOf(id).Any(_listening.Contains)) break;
            Thread.Sleep(250);
        }
        return Start(id);
    }

    public void Dispose() => _timer.Dispose();

    private void Poll()
    {
        try
        {
            RefreshListening();
            if (_ticks++ % 3 == 0) RefreshExternal();
            lock (_gate)
            {
                foreach (var (id, t) in _tracked)
                {
                    if (t.Exited) continue;
                    if (!IsAlive(t.Pid, t.StartTicks))
                    {
                        t.Exited = true;
                        continue;
                    }
                    if (!t.Ready)
                    {
                        var ports = PortsOf(id);
                        t.Ready = ports.Count > 0 ? ports.Any(_listening.Contains) : DateTime.UtcNow - t.StartedAt > TimeSpan.FromSeconds(2);
                    }
                }
            }
            Notify();
        }
        catch (Exception)
        {
            // A failed check must never stop the timer; the next tick tries again
        }
    }

    private void Notify()
    {
        string signature;
        lock (_gate)
            signature = string.Join('|', _store.All.Select(e => $"{e.Id}:{Get(e.Id).Status}:{Get(e.Id).Pid}"));
        if (signature == _signature) return;
        _signature = signature;
        Changed?.Invoke();
    }

    /// <summary>Looks, for every project that is not running here, whether another process has its output files open.</summary>
    private void RefreshExternal()
    {
        var found = new Dictionary<string, List<int>>();
        foreach (var entry in _store.All)
        {
            lock (_gate)
            {
                if (_tracked.TryGetValue(entry.Id, out var t) && !t.Exited) continue;
            }
            var info = _catalog.Info(entry.ProjectPath);
            if (info is null) continue;
            var holders = FindExternal(entry.Id, info);
            if (holders.Count > 0) found[entry.Id] = holders;
        }
        lock (_gate) _external = found;
    }

    /// <summary>The processes, other than this app and anything the runner started, that have the project's .exe or .dll in bin open.</summary>
    private List<int> FindExternal(string id, ProjectInfo info)
    {
        var files = OutputFiles(info);
        if (files.Count == 0) return [];

        var own = new HashSet<int> { Environment.ProcessId };
        lock (_gate)
        {
            // Every project this runner started, not only this one: another copy of the same project holds the same files
            foreach (var running in _tracked.Values) own.Add(running.Pid);
        }

        var result = new List<int>();
        foreach (var pid in FileLocks.ProcessesHolding(files))
        {
            if (own.Contains(pid) || ProcessTools.IsDescendantOf(pid, own)) continue;
            try
            {
                using var process = Process.GetProcessById(pid);
                // The app itself (its own apphost) or "dotnet" running its dll; build servers and editors also open files but are not the app
                var name = process.ProcessName;
                if (name.Equals(info.Name, StringComparison.OrdinalIgnoreCase) || name.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                    result.Add(pid);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                // Gone already
            }
        }
        return result;
    }

    /// <summary>The project's own exe and dll under bin (every configuration and framework). The list is cached for a while.</summary>
    private List<string> OutputFiles(ProjectInfo info)
    {
        lock (_gate)
        {
            if (_outputFiles.TryGetValue(info.Path, out var cached) && DateTime.UtcNow - cached.At < TimeSpan.FromSeconds(20))
                return cached.Files;
        }

        var files = new List<string>();
        var bin = Path.Combine(info.Directory, "bin");
        if (Directory.Exists(bin))
        {
            try
            {
                foreach (var extension in new[] { ".exe", ".dll" })
                    files.AddRange(Directory.EnumerateFiles(bin, info.Name + extension, SearchOption.AllDirectories));
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // Check what we found
            }
        }
        lock (_gate) _outputFiles[info.Path] = (DateTime.UtcNow, files);
        return files;
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);

    private SemaphoreSlim FamilyGate(string projectPath) => _gates.GetOrAdd(projectPath, _ => new SemaphoreSlim(1, 1));

    private IEnumerable<string> Family(string projectPath) =>
        _store.All.Where(e => e.ProjectPath.Equals(projectPath, StringComparison.OrdinalIgnoreCase)).Select(e => e.Id);

    /// <summary>True while another copy of the project is running, which means its files are in use and cannot be rebuilt.</summary>
    private bool SiblingRunning(string id, string projectPath)
    {
        lock (_gate) return Family(projectPath).Any(f => f != id && _tracked.TryGetValue(f, out var t) && !t.Exited);
    }

    /// <summary>Waits (a few minutes at most) while another copy of the project is still building, so two builds never run at once.</summary>
    private void WaitForBuild(string id, string projectPath)
    {
        var until = DateTime.UtcNow.AddMinutes(3);
        while (DateTime.UtcNow < until)
        {
            bool building;
            lock (_gate)
                building = Family(projectPath).Any(f => f != id && _tracked.TryGetValue(f, out var t) && !t.Exited && !t.Ready && t.Building);
            if (!building) return;
            Thread.Sleep(500);
        }
    }

    private static string Describe(IReadOnlyList<int> pids) =>
        string.Join(", ", pids.Select(pid =>
        {
            try { using var p = Process.GetProcessById(pid); return $"{p.ProcessName} {pid}"; }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return pid.ToString(); }
        }));

    private List<int> PortsOf(string id)
    {
        var entry = _store.Get(id);
        var info = entry is null ? null : _catalog.Info(entry.ProjectPath);
        var profile = info?.Profile(entry!.Profile);
        var env = entry?.Env.TryGetValue("ASPNETCORE_URLS", out var urls) == true ? urls.Split(';').Select(ProjectReader.PortOf).OfType<int>() : [];
        var shift = entry?.PortOffset ?? 0;
        var ports = (profile?.Ports ?? []).Concat(env).ToList();
        // Some projects fix their port in code (ListenAnyIP(5010)): that is the port to wait for and to check
        if (ports.Count == 0 && info is not null) ports = [.. _catalog.CodePorts(info).Select(c => c.Port)];
        return ports.Select(port => port + shift).Distinct().ToList();
    }

    private void RefreshListening()
    {
        try
        {
            _listening = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Select(e => e.Port).ToHashSet();
        }
        catch (NetworkInformationException)
        {
            // Keep the last known set
        }
    }

    private static bool IsAlive(int pid, long startTicks) => ProcessTools.IsAlive(pid, startTicks);

    private static void Kill(Tracked tracked)
    {
        if (!tracked.Exited) ProcessTools.KillTree(tracked.Pid, tracked.StartTicks);
    }

    private static void PrepareLog(string log, string banner) => ProcessTools.PrepareLog(log, banner);

    private void LoadState()
    {
        try
        {
            if (!File.Exists(StateFile)) return;
            var saved = JsonSerializer.Deserialize<Dictionary<string, Saved>>(File.ReadAllText(StateFile)) ?? [];
            foreach (var (id, s) in saved)
            {
                if (IsAlive(s.Pid, s.StartTicks))
                    _tracked[id] = new Tracked { Pid = s.Pid, StartTicks = s.StartTicks, StartedAt = s.StartedAt };
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Nothing to pick up again
        }
    }

    private void SaveState()
    {
        try
        {
            Dictionary<string, Saved> snapshot;
            lock (_gate)
                snapshot = _tracked.Where(p => !p.Value.Exited).ToDictionary(p => p.Key, p => new Saved(p.Value.Pid, p.Value.StartTicks, p.Value.StartedAt));
            Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
            File.WriteAllText(StateFile, JsonSerializer.Serialize(snapshot));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Without the file a restart of this app cannot find the running projects again
        }
    }
}
