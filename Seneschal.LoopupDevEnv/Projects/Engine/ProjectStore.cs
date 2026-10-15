using System.Text.Json;
using DevEnvironment.Shared;

namespace DevEnvironment.Projects;

/// <summary>A project the user added to the runner. Everything else (ports, profiles, solutions) is read from disk.</summary>
public sealed record ProjectEntry(
    string Id,
    string Name,
    string ProjectPath,
    string? SolutionPath,
    string? Profile,
    Dictionary<string, string> Env,
    string Args,
    string? InstanceOf = null,
    int PortOffset = 0);

/// <summary>The projects in the runner, saved in the extension's data folder (projects.json).</summary>
public sealed class ProjectStore
{
    private static readonly string FilePath = DevEnvironmentPaths.FilePath("projects.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly object _gate = new();
    private readonly List<ProjectEntry> _items = [];

    public event Action? Changed;

    public ProjectStore()
    {
        try
        {
            if (File.Exists(FilePath))
                _items.AddRange(JsonSerializer.Deserialize<List<ProjectEntry>>(File.ReadAllText(FilePath)) ?? []);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A broken file must never stop the app; start with an empty list.
        }
    }

    public IReadOnlyList<ProjectEntry> All
    {
        get { lock (_gate) return [.. _items]; }
    }

    public ProjectEntry? Get(string id)
    {
        lock (_gate) return _items.FirstOrDefault(i => i.Id == id);
    }

    public bool Contains(string projectPath)
    {
        lock (_gate) return _items.Any(i => i.ProjectPath.Equals(projectPath, StringComparison.OrdinalIgnoreCase));
    }

    public static string NewId() => Guid.NewGuid().ToString("N")[..8];

    public void Add(IEnumerable<ProjectEntry> entries)
    {
        lock (_gate)
        {
            foreach (var entry in entries)
            {
                if (!_items.Any(i => i.ProjectPath.Equals(entry.ProjectPath, StringComparison.OrdinalIgnoreCase)))
                    _items.Add(entry);
            }
        }
        SaveAndNotify();
    }

    /// <summary>
    /// Adds more copies of a project right below it (and below its other copies). Copies share the project file on purpose, so
    /// unlike <see cref="Add"/> nothing is skipped for being "already added".
    /// </summary>
    public void AddAfter(string afterId, IEnumerable<ProjectEntry> entries)
    {
        lock (_gate)
        {
            var index = _items.FindIndex(i => i.Id == afterId);
            if (index < 0) index = _items.Count - 1;
            while (index + 1 < _items.Count && _items[index + 1].InstanceOf == afterId) index++;
            _items.InsertRange(index + 1, entries);
        }
        SaveAndNotify();
    }

    /// <summary>Puts a project just before or after another one, which is how the user orders the list by hand.</summary>
    public void Move(string id, string targetId, bool after)
    {
        bool moved;
        lock (_gate) moved = ListMove.Move(_items, i => i.Id == id, i => i.Id == targetId, after);
        if (moved) SaveAndNotify();
    }

    public void Update(ProjectEntry entry)
    {
        lock (_gate)
        {
            var index = _items.FindIndex(i => i.Id == entry.Id);
            if (index >= 0) _items[index] = entry;
        }
        SaveAndNotify();
    }

    public void Remove(string id)
    {
        lock (_gate) _items.RemoveAll(i => i.Id == id);
        SaveAndNotify();
    }

    private void SaveAndNotify()
    {
        try
        {
            List<ProjectEntry> snapshot;
            lock (_gate) snapshot = [.. _items];
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(snapshot, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The list still works until the app restarts
        }
        Changed?.Invoke();
    }
}

/// <summary>Reads each project file once and again only when the .csproj or launchSettings.json changes.</summary>
public sealed class ProjectCatalog
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (long Stamp, ProjectInfo Info)> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The project's info, or null when the file is missing or unreadable.</summary>
    public ProjectInfo? Info(string projectPath)
    {
        if (!File.Exists(projectPath)) return null;
        var stamp = Stamp(projectPath);
        lock (_gate)
        {
            if (_cache.TryGetValue(projectPath, out var cached) && cached.Stamp == stamp)
                return cached.Info;
        }
        try
        {
            var info = ProjectReader.Read(projectPath);
            lock (_gate) _cache[projectPath] = (stamp, info);
            return info;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private readonly Dictionary<string, (long Stamp, IReadOnlyList<CodePort> Ports)> _codePorts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The ports a project fixes in its Program.cs. Read again only when that file changes.</summary>
    public IReadOnlyList<CodePort> CodePorts(ProjectInfo info)
    {
        var file = Path.Combine(info.Directory, "Program.cs");
        var stamp = File.Exists(file) ? File.GetLastWriteTimeUtc(file).Ticks : 0;
        lock (_gate)
        {
            if (_codePorts.TryGetValue(info.Path, out var cached) && cached.Stamp == stamp) return cached.Ports;
        }
        var ports = ProjectReader.CodePorts(info.Directory);
        lock (_gate) _codePorts[info.Path] = (stamp, ports);
        return ports;
    }

    /// <summary>Forget what was read (for example after a new .sln was added).</summary>
    public void Refresh(string projectPath)
    {
        lock (_gate) _cache.Remove(projectPath);
    }

    private static long Stamp(string projectPath)
    {
        var settings = Path.Combine(Path.GetDirectoryName(projectPath)!, "Properties", "launchSettings.json");
        return File.GetLastWriteTimeUtc(projectPath).Ticks ^ (File.Exists(settings) ? File.GetLastWriteTimeUtc(settings).Ticks : 0);
    }
}
