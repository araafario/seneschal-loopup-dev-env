using DevEnvironment.Logging;

namespace DevEnvironment.Projects.Commands;

/// <summary>The commands of the runner as log sources, grouped by what they are.</summary>
public sealed class CommandLogSources : ILogSourceProvider
{
    private readonly CommandStore _store;

    public CommandLogSources(CommandStore store)
    {
        _store = store;
        _store.Changed += () => Changed?.Invoke();
    }

    public event Action? Changed;

    public IReadOnlyList<LogSource> Sources() =>
        _store.All.Select(e => new LogSource($"command:{e.Id}", e.Name, e.Kind == CommandKinds.SingleSpa ? "Microfrontends" : "Commands", CommandRunner.LogPath(e.Id))).ToList();
}
