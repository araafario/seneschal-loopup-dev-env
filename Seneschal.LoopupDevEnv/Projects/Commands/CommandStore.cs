using System.Text.Json;
using DevEnvironment.Shared;

namespace DevEnvironment.Projects.Commands;

public static class CommandKinds
{
    /// <summary>A single-spa root config, application or utility module, started with the dev server of its package.</summary>
    public const string SingleSpa = "single-spa";
}

public static class SingleSpaRoles
{
    public const string Root = "root";
    public const string Application = "application";
    public const string Utility = "utility";

    public static string Label(string? role) => role switch
    {
        Root => "Root config",
        Application => "Application",
        Utility => "Utility module",
        _ => "Microfrontend",
    };
}

/// <summary>
/// Something started with a command line in a folder, such as <c>npm start</c>. The engine does not care what it is: a
/// <see cref="Kind"/> says which section of the page lists it, and <see cref="Role"/> is extra information for that kind.
/// </summary>
public sealed record CommandEntry(
    string Id,
    string Name,
    string Directory,
    string CommandLine,
    string Shell,
    Dictionary<string, string> Env,
    int[] Ports,
    string? ReadyText,
    string? Group,
    string Kind,
    string? Role)
{
    public const string Cmd = "cmd";
    public const string PowerShell = "powershell";
}

/// <summary>The commands the user added, saved in the extension's data folder (commands.json) together with the last folder that was scanned.</summary>
public sealed class CommandStore
{
    private static readonly string FilePath = DevEnvironmentPaths.FilePath("commands.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private sealed record Saved(List<CommandEntry> Commands, string? LastScanPath);

    private readonly object _gate = new();
    private readonly List<CommandEntry> _items = [];
    private string? _lastScanPath;

    public event Action? Changed;

    public CommandStore()
    {
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<Saved>(File.ReadAllText(FilePath)) is { } saved)
            {
                _items.AddRange(saved.Commands ?? []);
                _lastScanPath = saved.LastScanPath;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A broken file must never stop the app; start with an empty list.
        }
    }

    public IReadOnlyList<CommandEntry> All
    {
        get { lock (_gate) return [.. _items]; }
    }

    public IReadOnlyList<CommandEntry> OfKind(string kind)
    {
        lock (_gate) return _items.Where(i => i.Kind == kind).ToList();
    }

    public CommandEntry? Get(string id)
    {
        lock (_gate) return _items.FirstOrDefault(i => i.Id == id);
    }

    public bool Contains(string directory, string commandLine)
    {
        lock (_gate)
            return _items.Any(i => i.Directory.Equals(directory, StringComparison.OrdinalIgnoreCase) && i.CommandLine == commandLine);
    }

    public string? LastScanPath
    {
        get { lock (_gate) return _lastScanPath; }
    }

    public void RememberScanPath(string path)
    {
        lock (_gate) _lastScanPath = path;
        SaveAndNotify();
    }

    public static string NewId() => Guid.NewGuid().ToString("N")[..8];

    public void Add(IEnumerable<CommandEntry> entries)
    {
        lock (_gate)
        {
            foreach (var entry in entries)
            {
                if (!_items.Any(i => i.Directory.Equals(entry.Directory, StringComparison.OrdinalIgnoreCase) && i.CommandLine == entry.CommandLine))
                    _items.Add(entry);
            }
        }
        SaveAndNotify();
    }

    /// <summary>Puts an item just before or after another one, which is how the user orders the list by hand.</summary>
    public void Move(string id, string targetId, bool after)
    {
        bool moved;
        lock (_gate) moved = ListMove.Move(_items, i => i.Id == id, i => i.Id == targetId, after);
        if (moved) SaveAndNotify();
    }

    public void Update(CommandEntry entry)
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
            Saved snapshot;
            lock (_gate) snapshot = new Saved([.. _items], _lastScanPath);
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
