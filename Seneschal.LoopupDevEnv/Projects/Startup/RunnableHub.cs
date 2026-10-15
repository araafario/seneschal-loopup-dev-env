using DevEnvironment.Projects.Commands;
using DevEnvironment.Projects.Docker;

namespace DevEnvironment.Projects.Startup;

/// <summary>One thing the page can start: a container, a .NET project or a microfrontend.</summary>
public sealed record RunnableInfo(string Ref, string Name, string Kind);

public enum ItemState
{
    Stopped,
    Starting,

    /// <summary>Running and, for a container with a health check, healthy. Something running elsewhere on the port counts too.</summary>
    Ready,

    /// <summary>Ended on its own, or is gone.</summary>
    Failed,
}

/// <summary>
/// Treats the three kinds of things alike so a startup can mix them. An item is named by a reference: <c>docker:&lt;name&gt;</c>,
/// <c>project:&lt;id&gt;</c> or <c>command:&lt;id&gt;</c>, the same names the log sources use.
/// </summary>
public sealed class RunnableHub
{
    public const string DockerKind = "Docker";
    public const string DotNetKind = ".NET";
    public const string MicrofrontendKind = "Microfrontends";

    private readonly ProjectStore _projects;
    private readonly ProjectRunner _dotnet;
    private readonly CommandStore _commands;
    private readonly CommandRunner _commandRunner;
    private readonly DockerStore _dockerStore;
    private readonly DockerRunner _docker;

    public RunnableHub(ProjectStore projects, ProjectRunner dotnet, CommandStore commands, CommandRunner commandRunner, DockerStore dockerStore, DockerRunner docker)
    {
        _projects = projects;
        _dotnet = dotnet;
        _commands = commands;
        _commandRunner = commandRunner;
        _dockerStore = dockerStore;
        _docker = docker;
    }

    /// <summary>Everything that was added to the page, in the order the sections show it.</summary>
    public IReadOnlyList<RunnableInfo> All() =>
    [
        .. _dockerStore.Added.Select(n => new RunnableInfo($"docker:{n}", DockerDisplay(n), DockerKind)).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
        .. _projects.All.Select(p => new RunnableInfo($"project:{p.Id}", p.Name, DotNetKind)).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
        .. _commands.OfKind(CommandKinds.SingleSpa).Select(c => new RunnableInfo($"command:{c.Id}", c.Name, MicrofrontendKind)).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
    ];

    public RunnableInfo? Find(string reference)
    {
        var (kind, key) = Split(reference);
        return kind switch
        {
            "docker" when _dockerStore.Contains(key) => new RunnableInfo(reference, DockerDisplay(key), DockerKind),
            "project" when _projects.Get(key) is { } p => new RunnableInfo(reference, p.Name, DotNetKind),
            "command" when _commands.Get(key) is { } c => new RunnableInfo(reference, c.Name, MicrofrontendKind),
            _ => null,
        };
    }

    public ItemState State(string reference)
    {
        var (kind, key) = Split(reference);
        switch (kind)
        {
            case "project": return Map(_dotnet.Get(key).Status);
            case "command": return Map(_commandRunner.Get(key).Status);
            case "docker":
                if (_docker.Busy(key) is not null) return ItemState.Starting;
                var container = _docker.Get(key);
                if (container is null) return ItemState.Failed;
                if (container.Ready) return ItemState.Ready;
                return container.Running ? ItemState.Starting : ItemState.Stopped;
            default: return ItemState.Failed;
        }
    }

    public Task<RunResult> StartAsync(string reference)
    {
        var (kind, key) = Split(reference);
        return kind switch
        {
            "docker" => _docker.StartAsync(key),
            "project" => Task.Run(() => _dotnet.Start(key)),
            "command" => Task.Run(() => _commandRunner.Start(key)),
            _ => Task.FromResult(new RunResult(false, "Unknown item")),
        };
    }

    /// <summary>Stops the item. Something that runs elsewhere (not started here) is left alone.</summary>
    public Task<RunResult> StopAsync(string reference)
    {
        var (kind, key) = Split(reference);
        return kind switch
        {
            "docker" => _docker.StopAsync(key),
            "project" => Task.Run(() => _dotnet.Stop(key)),
            "command" => Task.Run(() => _commandRunner.Stop(key)),
            _ => Task.FromResult(new RunResult(false, "Unknown item")),
        };
    }

    private static ItemState Map(RunStatus status) => status switch
    {
        RunStatus.Running or RunStatus.External => ItemState.Ready,
        RunStatus.Starting => ItemState.Starting,
        RunStatus.Exited => ItemState.Failed,
        _ => ItemState.Stopped,
    };

    /// <summary>The compose service name when there is one, which is what people call the container.</summary>
    private string DockerDisplay(string name)
    {
        var c = _docker.Get(name);
        return c?.Project is not null && c.Service is not null && name.StartsWith(c.Project + "-" + c.Service + "-", StringComparison.OrdinalIgnoreCase)
            ? c.Service
            : name;
    }

    private static (string Kind, string Key) Split(string reference)
    {
        var colon = reference.IndexOf(':');
        return colon < 0 ? ("", reference) : (reference[..colon], reference[(colon + 1)..]);
    }
}
