using Seneschal.Sdk;
using DevEnvironment.Logging;
using DevEnvironment.Projects.Commands;
using DevEnvironment.Projects.Startup;
using DevEnvironment.Projects.Docker;
using DevEnvironment.Shared;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;

namespace DevEnvironment.Projects;

// Metadata, plus the tool's own services (IToolLifecycle) so the app's startup code knows nothing about them.
public sealed class ProjectsTool : IToolModule, IToolLifecycle, IToolMcp
{
    public ToolMetadata Metadata { get; } = new(
        Id: "projects",
        Title: "Projects",
        Description: "Run and watch Docker, .NET and microfrontends",
        Group: "LoopUp",
        Icon: Icons.Material.Filled.PlayCircle,
        ComponentType: typeof(ProjectsView),
        SearchTerms: ["run", "start", "dotnet", "service", "microservice", "csproj", "docker", "container", "npm", "microfrontend", "single-spa"])
    {
        // Keeps the search box and the selection while you look at the logs
        Persistent = true,
        Prompts = ["What is running right now?", "Stop everything that is running", "Start my first Smart Startup"],
    };

    public void ConfigureServices(IServiceCollection services, IExtensionContext context)
    {
        DevEnvironmentPaths.Initialize(context.DataDirectory);

        services.AddSingleton<ShellLauncher>();
        services.AddSingleton<ProjectStore>();
        services.AddSingleton<ProjectCatalog>();
        services.AddSingleton<ProjectRunner>();
        services.AddSingleton<ILogSourceProvider, ProjectLogSources>();

        services.AddSingleton<CommandStore>();
        services.AddSingleton<CommandRunner>();
        services.AddSingleton<ILogSourceProvider, CommandLogSources>();

        services.AddSingleton<StartupStore>();
        services.AddSingleton<RunnableHub>();
        services.AddSingleton<StartupRunner>();

        services.AddSingleton<DockerStore>();
        services.AddSingleton<DockerRunner>();
        services.AddSingleton<DockerLogSources>();
        services.AddSingleton<ILogSourceProvider>(sp => sp.GetRequiredService<DockerLogSources>());
    }

    public IEnumerable<McpTool> McpTools(IServiceProvider services) => new ProjectsMcp(services).Tools();

    // Projects that kept running while the app was restarted are picked up again right away, and Docker is watched from the start
    // so the logs of its containers are followed even before the Projects page is opened
    public void Started(IServiceProvider services, IExtensionContext context)
    {
        services.GetRequiredService<ProjectRunner>();
        services.GetRequiredService<CommandRunner>();
        services.GetRequiredService<DockerLogSources>();
    }
}
