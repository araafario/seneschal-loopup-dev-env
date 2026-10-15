using System.Diagnostics;
using System.Text.RegularExpressions;

namespace DevEnvironment.Projects.Docker;

public enum EngineState
{
    /// <summary>Not asked yet.</summary>
    Unknown,
    Up,

    /// <summary>The docker command exists but the engine does not answer, usually because Docker Desktop is not running.</summary>
    Down,

    /// <summary>There is no docker command on this machine.</summary>
    Missing,
}

public enum Health
{
    None,
    Starting,
    Healthy,
    Unhealthy,
}

/// <summary>One container as Docker reports it.</summary>
public sealed record ContainerInfo(
    string Id,
    string Name,
    string Image,
    string State,
    string Status,
    Health Health,
    IReadOnlyList<int> Ports,
    string? Project,
    string? Service)
{
    public bool Running => State is "running" or "restarting" or "paused";

    /// <summary>Running, and when the image has a health check, healthy as well.</summary>
    public bool Ready => State == "running" && Health is Health.None or Health.Healthy;

    /// <summary>The row class that colors the dot: starting, running, stopped, or exited badly (also an unhealthy container).</summary>
    public string RunClass(string? busy)
    {
        if (busy is not null || State == "restarting" || Health == Health.Starting) return "run-starting";
        if (Health == Health.Unhealthy) return "run-exited";
        if (Running) return "run-running";
        return State == "exited" && !Status.StartsWith("Exited (0)", StringComparison.Ordinal) ? "run-exited" : "run-stopped";
    }
}

/// <summary>Reads the containers from Docker and starts, stops and restarts them. Containers are never created or removed here.</summary>
public sealed partial class DockerRunner : IDisposable
{
    private const string ListFormat =
        "{{.ID}}\t{{.Names}}\t{{.Image}}\t{{.State}}\t{{.Status}}\t{{.Ports}}\t{{.Label \"com.docker.compose.project\"}}\t{{.Label \"com.docker.compose.service\"}}";

    private readonly object _gate = new();
    private readonly Dictionary<string, string> _busy = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _timer;
    private List<ContainerInfo> _containers = [];
    private EngineState _engine = EngineState.Unknown;
    private string? _engineMessage;
    private string _signature = "";
    private int _polling;

    public event Action? Changed;

    public DockerRunner()
    {
        _timer = new Timer(_ => _ = PollAsync(), null, TimeSpan.Zero, TimeSpan.FromSeconds(3));
    }

    public EngineState Engine
    {
        get { lock (_gate) return _engine; }
    }

    public string? EngineMessage
    {
        get { lock (_gate) return _engineMessage; }
    }

    public IReadOnlyList<ContainerInfo> Containers
    {
        get { lock (_gate) return [.. _containers]; }
    }

    public ContainerInfo? Get(string name)
    {
        lock (_gate) return _containers.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>What is being done to the container right now ("Starting", "Stopping", "Restarting"), or null.</summary>
    public string? Busy(string name)
    {
        lock (_gate) return _busy.GetValueOrDefault(name);
    }

    public Task<RunResult> StartAsync(string name) => RunAsync(name, "Starting", ["start", name], $"Started {name}");

    public Task<RunResult> StopAsync(string name) => RunAsync(name, "Stopping", ["stop", "--time", "10", name], $"Stopped {name}");

    public Task<RunResult> RestartAsync(string name) => RunAsync(name, "Restarting", ["restart", "--time", "10", name], $"Restarted {name}");

    /// <summary>Opens Docker Desktop. The list fills in by itself once the engine answers.</summary>
    public RunResult StartDockerDesktop()
    {
        var program = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Docker", "Docker", "Docker Desktop.exe");
        if (!File.Exists(program))
            return new RunResult(false, "Docker Desktop was not found in its usual folder, start it yourself");
        try
        {
            Process.Start(new ProcessStartInfo(program) { UseShellExecute = true });
            return new RunResult(true, "Starting Docker Desktop");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new RunResult(false, $"Could not start Docker Desktop: {ex.Message}");
        }
    }

    public void Dispose() => _timer.Dispose();

    private async Task<RunResult> RunAsync(string name, string doing, string[] args, string done)
    {
        lock (_gate)
        {
            if (_busy.ContainsKey(name)) return new RunResult(false, $"{name} is busy");
            _busy[name] = doing;
        }
        Changed?.Invoke();

        try
        {
            var result = await DockerCli.RunAsync(args, TimeSpan.FromSeconds(45));
            return result.Ok ? new RunResult(true, done) : new RunResult(false, $"{name}: {result.Reason}");
        }
        finally
        {
            lock (_gate) _busy.Remove(name);
            await PollAsync();
        }
    }

    private async Task PollAsync()
    {
        if (Interlocked.Exchange(ref _polling, 1) == 1) return;
        try
        {
            var result = await DockerCli.RunAsync(["ps", "-a", "--format", ListFormat], TimeSpan.FromSeconds(8));
            lock (_gate)
            {
                if (result.Ok)
                {
                    _engine = EngineState.Up;
                    _engineMessage = null;
                    _containers = [.. result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(Parse).OfType<ContainerInfo>().OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)];
                }
                else
                {
                    _engine = result.ExitCode == DockerCli.NotFound ? EngineState.Missing : EngineState.Down;
                    _engineMessage = _engine == EngineState.Missing ? "Docker is not installed or not on the PATH" : "Docker is not running";
                    _containers = [];
                }
            }
        }
        catch (Exception)
        {
            // A failed check must never stop the timer; the next tick tries again
        }
        finally
        {
            Volatile.Write(ref _polling, 0);
        }
        Notify();
    }

    private void Notify()
    {
        string signature;
        lock (_gate)
            signature = _engine + "|" + string.Join('|', _containers.Select(c => $"{c.Id}:{c.State}:{c.Status}:{c.Health}")) + "|" + string.Join('|', _busy);
        if (signature == _signature) return;
        _signature = signature;
        Changed?.Invoke();
    }

    private static ContainerInfo? Parse(string line)
    {
        var parts = line.TrimEnd('\r').Split('\t');
        if (parts.Length < 8) return null;

        var status = parts[4];
        var health = status.Contains("(unhealthy)", StringComparison.Ordinal) ? Health.Unhealthy
            : status.Contains("(healthy)", StringComparison.Ordinal) ? Health.Healthy
            : status.Contains("(health: starting)", StringComparison.Ordinal) ? Health.Starting
            : Health.None;

        return new ContainerInfo(parts[0], parts[1], parts[2], parts[3], status, health, HostPorts(parts[5]),
            parts[6].Length > 0 ? parts[6] : null, parts[7].Length > 0 ? parts[7] : null);
    }

    /// <summary>The ports a container publishes on this machine, from text such as "0.0.0.0:6543->5432/tcp, [::]:6543->5432/tcp".</summary>
    internal static List<int> HostPorts(string text)
    {
        var ports = new List<int>();
        foreach (Match m in PublishedPort().Matches(text))
        {
            var from = int.Parse(m.Groups[1].Value);
            var to = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : from;
            for (var port = from; port <= Math.Min(to, from + 20); port++)
                if (!ports.Contains(port)) ports.Add(port);
        }
        return ports;
    }

    [GeneratedRegex(@":(\d+)(?:-(\d+))?->")]
    private static partial Regex PublishedPort();
}
