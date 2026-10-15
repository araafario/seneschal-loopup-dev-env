using System.Text.Json;
using DevEnvironment.Shared;

namespace DevEnvironment.Projects.Docker;

/// <summary>
/// The containers the user chose to manage here, by name. What a container is, and whether it runs, always comes from Docker;
/// this only remembers which ones to show, in the order they were added. Saved in the extension's data folder (docker.json).
/// </summary>
public sealed class DockerStore
{
    private static readonly string FilePath = DevEnvironmentPaths.FilePath("docker.json");

    private sealed record Saved(string[]? Added);

    private readonly object _gate = new();
    private readonly List<string> _added = [];

    public event Action? Changed;

    public DockerStore()
    {
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<Saved>(File.ReadAllText(FilePath)) is { Added: { } added })
                _added.AddRange(added.Distinct(StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Start with an empty list
        }
    }

    public IReadOnlyList<string> Added
    {
        get { lock (_gate) return [.. _added]; }
    }

    public bool Contains(string name)
    {
        lock (_gate) return _added.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    public void Add(IEnumerable<string> names)
    {
        lock (_gate)
        {
            foreach (var name in names)
                if (!_added.Contains(name, StringComparer.OrdinalIgnoreCase)) _added.Add(name);
        }
        SaveAndNotify();
    }

    /// <summary>Puts a container just before or after another one, which is how the user orders the list by hand.</summary>
    public void Move(string name, string targetName, bool after)
    {
        bool moved;
        lock (_gate) moved = ListMove.Move(_added, n => n.Equals(name, StringComparison.OrdinalIgnoreCase), n => n.Equals(targetName, StringComparison.OrdinalIgnoreCase), after);
        if (moved) SaveAndNotify();
    }

    public void Remove(string name)
    {
        lock (_gate) _added.RemoveAll(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
        SaveAndNotify();
    }

    private void SaveAndNotify()
    {
        try
        {
            Saved snapshot;
            lock (_gate) snapshot = new Saved([.. _added]);
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(snapshot));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The list still applies until the app restarts
        }
        Changed?.Invoke();
    }
}
