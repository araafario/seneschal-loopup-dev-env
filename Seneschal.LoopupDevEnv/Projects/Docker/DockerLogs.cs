using System.Diagnostics;
using System.Text;
using DevEnvironment.Logging;
using DevEnvironment.Shared;

namespace DevEnvironment.Projects.Docker;

/// <summary>
/// The containers of the list as log sources. The log viewer follows files, so each listed container that is running gets a
/// "docker logs --follow" process that appends to a file in the extension's data folder. The followers belong to this app and
/// end with it (they are in a job object, so even a killed app takes them along); the files stay, and a follower started again later continues from where the last one ended.
/// </summary>
public sealed class DockerLogSources : ILogSourceProvider, IDisposable
{
    private const int TailLines = 300;

    private sealed class Follower
    {
        public required Process Process { get; init; }
        public required StreamWriter Writer { get; init; }
    }

    private readonly DockerRunner _runner;
    private readonly DockerStore _store;
    private readonly object _gate = new();
    private readonly Dictionary<string, Follower> _followers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _lastEnd = new(StringComparer.OrdinalIgnoreCase);
    private readonly ChildProcessJob _job = new();
    private bool _disposed;

    public event Action? Changed;

    public DockerLogSources(DockerRunner runner, DockerStore store)
    {
        _runner = runner;
        _store = store;
        _runner.Changed += OnChanged;
        _store.Changed += OnChanged;
    }

    public static string LogPath(string name) => Path.Combine(DevEnvironmentPaths.RunnerLogsDir, $"docker-{name}.log");

    public IReadOnlyList<LogSource> Sources() =>
        _runner.Containers
            .Where(c => _store.Contains(c.Name))
            .Select(c => new LogSource($"docker:{c.Name}", c.Name, "Docker", LogPath(c.Name)))
            .ToList();

    public void Dispose()
    {
        _runner.Changed -= OnChanged;
        _store.Changed -= OnChanged;
        lock (_gate)
        {
            _disposed = true;
            foreach (var follower in _followers.Values) Stop(follower);
            _followers.Clear();
        }
        _job.Dispose();
    }

    private void OnChanged()
    {
        Sync();
        Changed?.Invoke();
    }

    /// <summary>Starts a follower for every listed running container that has none, and ends the ones that are no longer needed.</summary>
    private void Sync()
    {
        var wanted = _runner.Containers.Where(c => c.Running && _store.Contains(c.Name)).Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            if (_disposed) return;
            foreach (var name in _followers.Keys.Where(n => !wanted.Contains(n)).ToList())
            {
                Stop(_followers[name]);
                _followers.Remove(name);
            }
            foreach (var name in wanted.Where(n => !_followers.ContainsKey(n)))
            {
                try
                {
                    _followers[name] = Start(name);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // No log for this container; the next change tries again
                }
            }
        }
    }

    private Follower Start(string name)
    {
        var path = LogPath(name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // First time since this app started: a fresh file with the last lines. Afterwards: add what came since the last one ended.
        var continuing = _lastEnd.TryGetValue(name, out var since);
        var stream = new FileStream(path, continuing ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        writer.WriteLine($"=== {name} logs followed {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");

        var start = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        start.ArgumentList.Add("logs");
        start.ArgumentList.Add("--follow");
        if (continuing)
        {
            start.ArgumentList.Add("--since");
            start.ArgumentList.Add(since.ToString("yyyy-MM-ddTHH:mm:ssZ"));
        }
        else
        {
            start.ArgumentList.Add("--tail");
            start.ArgumentList.Add(TailLines.ToString());
        }
        start.ArgumentList.Add(name);

        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        void Write(object _, DataReceivedEventArgs e)
        {
            if (e.Data is null) return;
            lock (writer)
            {
                try { writer.WriteLine(e.Data); }
                catch (ObjectDisposedException) { }
                catch (IOException) { }
            }
        }
        process.OutputDataReceived += Write;
        process.ErrorDataReceived += Write;
        process.Exited += (_, _) =>
        {
            lock (_gate)
            {
                _lastEnd[name] = DateTime.UtcNow;
                if (_followers.TryGetValue(name, out var current) && current.Process == process) _followers.Remove(name);
            }
            lock (writer)
            {
                try { writer.Dispose(); } catch (IOException) { }
            }
        };

        process.Start();
        _job.Add(process); // ends with the app even when it is killed or restarted without a clean shutdown
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return new Follower { Process = process, Writer = writer };
    }

    private static void Stop(Follower follower)
    {
        try
        {
            if (!follower.Process.HasExited) follower.Process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone
        }
    }
}
