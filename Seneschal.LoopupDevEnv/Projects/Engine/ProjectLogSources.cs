using DevEnvironment.Logging;

namespace DevEnvironment.Projects;

/// <summary>The projects of the runner as log sources.</summary>
public sealed class ProjectLogSources : ILogSourceProvider
{
    private readonly ProjectStore _store;

    public ProjectLogSources(ProjectStore store)
    {
        _store = store;
        _store.Changed += () => Changed?.Invoke();
    }

    public event Action? Changed;

    public IReadOnlyList<LogSource> Sources() =>
        _store.All.Select(e => new LogSource($"project:{e.Id}", e.Name, ".NET", ProjectRunner.LogPath(e.Id))).ToList();
}
