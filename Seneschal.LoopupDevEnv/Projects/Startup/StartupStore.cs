using System.Text.Json;
using DevEnvironment.Shared;

namespace DevEnvironment.Projects.Startup;

/// <summary>
/// One step of a startup. The items of a step always start together. <see cref="WaitForPrevious"/> says whether the step waits until
/// everything before it is ready (true) or starts at the same moment as the step before it (false).
/// </summary>
public sealed record StartupStep(string[] Items, bool WaitForPrevious, int TimeoutSeconds, bool ContinueOnFailure)
{
    public const int DefaultTimeoutSeconds = 120;
}

public sealed record StartupPlan(string Id, string Name, List<StartupStep> Steps, string? Description = null);

/// <summary>The startups the user made, saved in the extension's data folder (startup.json).</summary>
public sealed class StartupStore
{
    private static readonly string FilePath = DevEnvironmentPaths.FilePath("startup.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private sealed record Saved(List<StartupPlan>? Plans);

    private readonly object _gate = new();
    private readonly List<StartupPlan> _plans = [];

    public event Action? Changed;

    public StartupStore()
    {
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<Saved>(File.ReadAllText(FilePath)) is { Plans: { } plans })
                _plans.AddRange(plans);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Start with none
        }
    }

    public IReadOnlyList<StartupPlan> All
    {
        get { lock (_gate) return [.. _plans]; }
    }

    public StartupPlan? Get(string id)
    {
        lock (_gate) return _plans.FirstOrDefault(p => p.Id == id);
    }

    public static string NewId() => Guid.NewGuid().ToString("N")[..8];

    public void Save(StartupPlan plan)
    {
        lock (_gate)
        {
            var index = _plans.FindIndex(p => p.Id == plan.Id);
            if (index >= 0) _plans[index] = plan;
            else _plans.Add(plan);
        }
        SaveAndNotify();
    }

    /// <summary>Puts a startup just before or after another one, which is how the user orders the list by hand.</summary>
    public void Move(string id, string targetId, bool after)
    {
        bool moved;
        lock (_gate) moved = ListMove.Move(_plans, p => p.Id == id, p => p.Id == targetId, after);
        if (moved) SaveAndNotify();
    }

    public void Remove(string id)
    {
        lock (_gate) _plans.RemoveAll(p => p.Id == id);
        SaveAndNotify();
    }

    private void SaveAndNotify()
    {
        try
        {
            Saved snapshot;
            lock (_gate) snapshot = new Saved([.. _plans]);
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
