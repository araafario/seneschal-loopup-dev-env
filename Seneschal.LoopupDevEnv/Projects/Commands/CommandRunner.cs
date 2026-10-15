using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DevEnvironment.Shared;

namespace DevEnvironment.Projects.Commands;

/// <summary>
/// Starts and stops commands such as <c>npm start</c>. Each one runs in its own process that writes to a log file and is not tied
/// to this app, so it keeps running when Seneschal restarts; the process id is saved and picked up again at startup.
/// It is ready when its port listens, when its log says so, or after a short moment when it has neither.
/// </summary>
public sealed partial class CommandRunner : IDisposable
{
    private static readonly string StateFile = DevEnvironmentPaths.FilePath("commands-state.json");

    private sealed class Tracked
    {
        public int Pid;
        public long StartTicks;
        public DateTime StartedAt;
        public long LogStart;
        public bool Ready;
        public bool Exited;
        public int? ExitCode;
    }

    private sealed record Saved(int Pid, long StartTicks, DateTime StartedAt, long LogStart);

    private readonly CommandStore _store;
    private readonly object _gate = new();
    private readonly Dictionary<string, Tracked> _tracked = [];
    private Dictionary<int, int> _owners = [];   // listening port -> id of the process that holds it
    private HashSet<int> _listening = [];
    private int _ticks;
    private string _signature = "";
    private readonly Timer _timer;

    public event Action? Changed;

    public CommandRunner(CommandStore store)
    {
        _store = store;
        LoadState();
        RefreshListening();
        _timer = new Timer(_ => Poll(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public static string LogPath(string id) => Path.Combine(DevEnvironmentPaths.RunnerLogsDir, $"command-{id}.log");

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

            var busy = PortsOf(id).FirstOrDefault(_listening.Contains);
            if (busy == 0) return new RunInfo(RunStatus.Stopped, null, null, null);
            return _owners.TryGetValue(busy, out var owner)
                ? new RunInfo(RunStatus.External, owner, null, $"Port {busy} is held by process {owner}, which was not started here")
                : new RunInfo(RunStatus.External, null, null, $"Something else is listening on port {busy}");
        }
    }

    public RunResult Start(string id)
    {
        var entry = _store.Get(id);
        if (entry is null) return new RunResult(false, "That item is no longer in the list");
        if (!Directory.Exists(entry.Directory)) return new RunResult(false, $"The folder was not found: {entry.Directory}");
        if (string.IsNullOrWhiteSpace(entry.CommandLine)) return new RunResult(false, $"{entry.Name} has no command to run");

        lock (_gate)
        {
            if (_tracked.TryGetValue(id, out var existing) && !existing.Exited)
                return new RunResult(false, $"{entry.Name} is already running");
        }

        RefreshListening();
        var taken = entry.Ports.Where(_listening.Contains).ToList();
        if (taken.Count > 0)
            return new RunResult(false, $"Port {taken[0]} is already in use ({Describe(taken[0])})");

        var log = LogPath(id);
        try
        {
            // Helpers an earlier run left behind still hold the log and would make this start fail
            ProcessTools.KillLogHolders(log);
            ProcessTools.PrepareLog(log, $"=== {entry.Name} started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
            var logStart = new FileInfo(log).Length;
            var process = Process.Start(Launch(entry, log)) ?? throw new InvalidOperationException("The process did not start.");
            var tracked = new Tracked
            {
                Pid = process.Id,
                StartTicks = process.StartTime.ToUniversalTime().Ticks,
                StartedAt = DateTime.UtcNow,
                LogStart = logStart,
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
                ? new RunResult(false, $"{name} was not started here (for example it runs in a terminal), so stop it where it was started")
                : new RunResult(true, $"{name} is not running");
        }

        if (!tracked.Exited) ProcessTools.KillTree(tracked.Pid, tracked.StartTicks);
        ProcessTools.KillLogHolders(LogPath(id));
        lock (_gate) _tracked.Remove(id);
        SaveState();
        try { File.AppendAllText(LogPath(id), $"=== {name} stopped {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==={Environment.NewLine}"); }
        catch (IOException) { }
        RefreshListening();
        Notify();
        return new RunResult(true, $"Stopped {name}");
    }

    /// <summary>Stops the process that holds the command's port although it was not started here, for example a dev server left running in a terminal. Ask the user first.</summary>
    public RunResult StopExternal(string id)
    {
        var entry = _store.Get(id);
        if (entry is null) return new RunResult(false, "That item is no longer available");

        RefreshListening();
        var holders = entry.Ports.Where(_owners.ContainsKey).Select(p => _owners[p]).Distinct().ToList();
        if (holders.Count == 0) return new RunResult(true, $"{entry.Name} is not running in another process");

        foreach (var pid in holders)
        {
            ProcessTools.KillTreeById(pid);
        }

        RefreshListening();
        Notify();
        return new RunResult(true, $"Stopped {entry.Name} (process {string.Join(", ", holders)})");
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

    private ProcessStartInfo Launch(CommandEntry entry, string log)
    {
        var start = new ProcessStartInfo
        {
            WorkingDirectory = entry.Directory,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // The shell writes the output into the log file, so nothing depends on this app staying alive to read a pipe
        if (entry.Shell == CommandEntry.PowerShell)
        {
            var script = $"& {{ {entry.CommandLine} }} *>&1 | Out-File -LiteralPath '{log.Replace("'", "''")}' -Append -Encoding utf8";
            start.FileName = "powershell.exe";
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
                start.ArgumentList.Add(arg);
        }
        else
        {
            start.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            start.Arguments = $"/d /s /c \"{entry.CommandLine} >> \"{log}\" 2>&1\"";
        }

        ProcessTools.CleanEnvironment(start);
        // Colors and progress bars only make a log harder to read
        start.Environment["NO_COLOR"] = "1";
        start.Environment["FORCE_COLOR"] = "0";
        foreach (var (key, value) in entry.Env)
            start.Environment[key] = value;
        return start;
    }

    private void Poll()
    {
        try
        {
            RefreshListening();
            if (_ticks++ % 3 == 0) RefreshOwners();
            lock (_gate)
            {
                foreach (var (id, t) in _tracked)
                {
                    if (t.Exited) continue;
                    if (!ProcessTools.IsAlive(t.Pid, t.StartTicks))
                    {
                        t.Exited = true;
                        continue;
                    }
                    if (!t.Ready) t.Ready = IsReady(id, t);
                }
            }
            Notify();
        }
        catch (Exception)
        {
            // A failed check must never stop the timer; the next tick tries again
        }
    }

    private bool IsReady(string id, Tracked t)
    {
        var entry = _store.Get(id);
        if (entry is null) return true;
        if (!string.IsNullOrWhiteSpace(entry.ReadyText))
            return LogHas(id, t.LogStart, entry.ReadyText);
        if (entry.Ports.Length > 0)
            return entry.Ports.Any(_listening.Contains);
        return DateTime.UtcNow - t.StartedAt > TimeSpan.FromSeconds(2);
    }

    /// <summary>True when the text appears in what the command wrote to its log since it was started.</summary>
    private static bool LogHas(string id, long from, string text)
    {
        try
        {
            using var stream = new FileStream(LogPath(id), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length <= from) return false;
            stream.Seek(from, SeekOrigin.Begin);
            var buffer = new byte[Math.Min(stream.Length - from, 512 * 1024)];
            var read = stream.Read(buffer, 0, buffer.Length);
            return Encoding.UTF8.GetString(buffer, 0, read).Contains(text, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
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

    private string Describe(int port)
    {
        lock (_gate)
        {
            if (_owners.TryGetValue(port, out var pid))
            {
                try { using var p = Process.GetProcessById(pid); return $"{p.ProcessName} {pid}"; }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return $"process {pid}"; }
            }
        }
        return "another program";
    }

    private List<int> PortsOf(string id) => [.. _store.Get(id)?.Ports ?? []];

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

    /// <summary>
    /// Which process holds each listening port. Windows has no cheap call for it, so this reads "netstat -ano", and only for ports that
    /// belong to a command that is not running here.
    /// </summary>
    private void RefreshOwners()
    {
        var wanted = _store.All.Where(e => !_tracked.ContainsKey(e.Id)).SelectMany(e => e.Ports).Where(_listening.Contains).ToHashSet();
        if (wanted.Count == 0)
        {
            lock (_gate) _owners = [];
            return;
        }

        var owners = new Dictionary<int, int>();
        try
        {
            using var netstat = Process.Start(new ProcessStartInfo("netstat.exe", "-ano -p TCP")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (netstat is null) return;
            var output = netstat.StandardOutput.ReadToEnd();
            netstat.WaitForExit(3000);
            foreach (Match m in NetstatLine().Matches(output))
            {
                if (int.Parse(m.Groups[1].Value) is var port && wanted.Contains(port))
                    owners[port] = int.Parse(m.Groups[2].Value);
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return;
        }
        lock (_gate) _owners = owners;
    }

    private void LoadState()
    {
        try
        {
            if (!File.Exists(StateFile)) return;
            var saved = JsonSerializer.Deserialize<Dictionary<string, Saved>>(File.ReadAllText(StateFile)) ?? [];
            foreach (var (id, s) in saved)
            {
                if (ProcessTools.IsAlive(s.Pid, s.StartTicks))
                    _tracked[id] = new Tracked { Pid = s.Pid, StartTicks = s.StartTicks, StartedAt = s.StartedAt, LogStart = s.LogStart };
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
                snapshot = _tracked.Where(p => !p.Value.Exited).ToDictionary(p => p.Key, p => new Saved(p.Value.Pid, p.Value.StartTicks, p.Value.StartedAt, p.Value.LogStart));
            Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
            File.WriteAllText(StateFile, JsonSerializer.Serialize(snapshot));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Without the file a restart of this app cannot find the running commands again
        }
    }

    [GeneratedRegex(@"^\s*TCP\s+\S+:(\d+)\s+\S+\s+LISTENING\s+(\d+)", RegexOptions.Multiline)]
    private static partial Regex NetstatLine();
}
