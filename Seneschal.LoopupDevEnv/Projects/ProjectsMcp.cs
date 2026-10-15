using System.Text.Json.Nodes;
using DevEnvironment.Projects.Commands;
using DevEnvironment.Projects.Docker;
using DevEnvironment.Projects.Startup;
using Microsoft.Extensions.DependencyInjection;
using Seneschal.Sdk;

namespace DevEnvironment.Projects;

/// <summary>
/// What the AI assistant can do with the Projects tool: everything the page does. Containers, .NET projects and microfrontends are
/// called "items" and are named the way the page names them (or as docker:name, project:id, command:id when two share a name).
/// </summary>
internal sealed class ProjectsMcp(IServiceProvider services)
{
    private RunnableHub Hub => services.GetRequiredService<RunnableHub>();
    private ProjectStore Projects => services.GetRequiredService<ProjectStore>();
    private ProjectCatalog Catalog => services.GetRequiredService<ProjectCatalog>();
    private CommandStore Commands => services.GetRequiredService<CommandStore>();
    private DockerStore DockerList => services.GetRequiredService<DockerStore>();
    private DockerRunner Docker => services.GetRequiredService<DockerRunner>();
    private StartupStore Startups => services.GetRequiredService<StartupStore>();
    private StartupRunner Runner => services.GetRequiredService<StartupRunner>();

    private static readonly JsonObject StepsSchema = new()
    {
        ["type"] = "array",
        ["description"] = "The steps in the order they run. Each step starts its items together",
        ["items"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["items"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "Names of the items in this step" },
                ["waitForPrevious"] = new JsonObject { ["type"] = "boolean", ["description"] = "True (default) waits until the step before is ready. False starts this step together with it" },
                ["timeoutSeconds"] = new JsonObject { ["type"] = "integer", ["description"] = "How long to wait for the step to be ready. Default 120" },
                ["ignoreFailures"] = new JsonObject { ["type"] = "boolean", ["description"] = "True carries on when an item of the step fails" },
            },
            ["required"] = new JsonArray("items"),
        },
    };

    public IEnumerable<McpTool> Tools()
    {
        // Looking
        yield return new McpTool("projects_overview", "Show everything in Projects: Docker containers, .NET projects, microfrontends and the Smart Startups, each with its state",
            McpEffect.Read, McpSchema.None, _ => Overview());

        yield return new McpTool("projects_status", "Show the state of the named items only",
            McpEffect.Read, McpSchema.Object().List("items", "Item names", required: true),
            args => args.List("items").Select(Resolve).Select(Describe));

        // Starting and stopping
        yield return new McpTool("projects_start", "Start items (containers, .NET projects, microfrontends). They start together, so use a startup when the order matters",
            McpEffect.Change, McpSchema.Object().List("items", "Item names, such as postgres or Gateway", required: true),
            async (args, _) => await Run(args.List("items"), start: true));

        yield return new McpTool("projects_stop", "Stop items",
            McpEffect.Change, McpSchema.Object().List("items", "Item names", required: true),
            async (args, _) => await Run(args.List("items"), start: false));

        // Ordering
        yield return new McpTool("projects_move", "Put an item before or after another one of the same kind, which is how the list is ordered",
            McpEffect.Change, McpSchema.Object().Text("item", "The item to move", required: true).Text("relativeTo", "The item it goes next to", required: true)
                .Choice("position", "Before or after it (default after)", ["before", "after"]),
            args => Move(Resolve(args.Text("item")), Resolve(args.Text("relativeTo")), args.Optional("position") != "before"));

        // Docker
        yield return new McpTool("docker_available", "List the containers that exist in Docker, and whether they are already in Projects",
            McpEffect.Read, McpSchema.None, _ => Docker.Containers.Select(c => new { c.Name, c.Image, c.State, c.Status, Added = DockerList.Contains(c.Name) }));

        yield return new McpTool("docker_add", "Add Docker containers to Projects so they can be started and stopped there",
            McpEffect.Change, McpSchema.Object().List("names", "Container names from docker_available", required: true),
            args =>
            {
                var names = args.List("names");
                var missing = names.Where(n => Docker.Get(n) is null).ToList();
                if (missing.Count > 0) throw new McpException($"Docker has no container named {string.Join(", ", missing)}");
                DockerList.Add(names);
                return $"Added {string.Join(", ", names)}";
            });

        yield return new McpTool("docker_remove", "Take containers off the Projects list. The containers themselves stay in Docker",
            McpEffect.Destructive, McpSchema.Object().List("names", "Container names", required: true),
            args =>
            {
                var names = args.List("names");
                foreach (var name in names) DockerList.Remove(name);
                return $"Removed {string.Join(", ", names)} from the list";
            });

        // .NET
        yield return new McpTool("dotnet_find", "Find the .NET projects that can run under a folder",
            McpEffect.Read, McpSchema.Object().Text("folder", "The folder to look in", required: true),
            args => ProjectReader.Scan(Folder(args.Text("folder")), includeNonRunnable: false)
                .Select(p => new { p.Name, p.Path, Added = Projects.Contains(p.Path), Profiles = p.Profiles.Select(x => x.Name) }));

        yield return new McpTool("dotnet_add", "Add a .NET project to Projects. Give the .csproj path, or a folder that holds one project",
            McpEffect.Change, McpSchema.Object().Text("path", "The .csproj file or its folder", required: true).Text("name", "A name for the list (default: the project name)"),
            args => AddProject(args.Text("path"), args.Optional("name")));

        yield return new McpTool("dotnet_add_instances", "Add copies of a .NET project that listen on their own ports, to run more than one at a time",
            McpEffect.Change, McpSchema.Object().Text("item", "The project", required: true).Number("count", "How many to add (1 to 9, default 1)").Number("portStep", "Ports between copies (default 100)"),
            args =>
            {
                if (Resolve(args.Text("item")) is not { Kind: "project" } found) throw new McpException("Only a .NET project can have instances");
                var entry = Projects.Get(found.Key)!;
                var instances = ProjectInstances.Create(Projects, entry, Math.Clamp(args.Number("count", 1), 1, 9), Math.Clamp(args.Number("portStep", 100), 1, 5000));
                Projects.AddAfter(entry.Id, instances);
                return new { Added = instances.Select(i => new { i.Name, i.PortOffset }) };
            });

        yield return new McpTool("dotnet_remove", "Take a .NET project off the Projects list. The project's files are not touched",
            McpEffect.Destructive, McpSchema.Object().Text("item", "The project", required: true),
            args => Remove(Resolve(args.Text("item")), "project"));

        // Microfrontends
        yield return new McpTool("microfrontend_find", "Find the single-spa microfrontends under a folder",
            McpEffect.Read, McpSchema.Object().Text("folder", "The folder to look in, such as the folder that holds your frontends", required: true),
            args => SingleSpaScanner.Scan(Folder(args.Text("folder"))).Select(f => new { f.Name, f.Directory, f.Role, f.Port, f.CommandLine, Added = Commands.Contains(f.Directory, f.CommandLine) }));

        yield return new McpTool("microfrontend_add", "Add the microfrontends found under a folder to Projects (all of them, or only the named ones)",
            McpEffect.Change, McpSchema.Object().Text("folder", "The folder to look in", required: true).List("names", "Only these (default: all that were found)"),
            args =>
            {
                var folder = Folder(args.Text("folder"));
                var only = args.List("names");
                var group = Path.GetFileName(folder.TrimEnd('\\', '/'));
                var entries = SingleSpaScanner.Scan(folder)
                    .Where(f => only.Count == 0 || only.Contains(f.Name, StringComparer.OrdinalIgnoreCase))
                    .Where(f => !Commands.Contains(f.Directory, f.CommandLine))
                    .Select(f => new CommandEntry(CommandStore.NewId(), f.Name, f.Directory, f.CommandLine, CommandEntry.Cmd, [], f.Port is { } port ? [port] : [], null,
                        group.Length > 0 ? group : null, CommandKinds.SingleSpa, f.Role))
                    .ToList();
                Commands.Add(entries);
                Commands.RememberScanPath(folder);
                return new { Added = entries.Select(e => e.Name) };
            });

        yield return new McpTool("microfrontend_remove", "Take a microfrontend off the Projects list. Its files are not touched",
            McpEffect.Destructive, McpSchema.Object().Text("item", "The microfrontend", required: true),
            args => Remove(Resolve(args.Text("item")), "command"));

        // Smart Startup
        yield return new McpTool("startup_list", "List the Smart Startups with their steps and whether they are running",
            McpEffect.Read, McpSchema.None, _ => Startups.All.Select(DescribePlan));

        yield return new McpTool("startup_run", "Run a Smart Startup: its steps start in order, each waiting until the one before is ready",
            McpEffect.Change, McpSchema.Object().Text("name", "The startup", required: true),
            args =>
            {
                var plan = Plan(args.Text("name"));
                Runner.Start(plan.Id);
                return $"Started {plan.Name}. Use startup_status to follow it";
            });

        yield return new McpTool("startup_status", "Show how a Smart Startup is going, and which items failed and why",
            McpEffect.Read, McpSchema.Object().Text("name", "The startup", required: true),
            args =>
            {
                var plan = Plan(args.Text("name"));
                var status = Runner.Get(plan.Id);
                return new
                {
                    plan.Name,
                    State = (status?.State ?? PlanState.Idle).ToString(),
                    Message = status?.Message,
                    Items = status?.Items.Select(i => new { Item = Hub.Find(i.Key)?.Name ?? i.Key, Phase = i.Value.Phase.ToString(), i.Value.Message }),
                };
            });

        yield return new McpTool("startup_stop", "Stop what a Smart Startup started, in reverse order (or cancel it while it is still starting)",
            McpEffect.Change, McpSchema.Object().Text("name", "The startup", required: true),
            args =>
            {
                var plan = Plan(args.Text("name"));
                if (Runner.Get(plan.Id)?.State == PlanState.Running) Runner.Cancel(plan.Id);
                else Runner.Stop(plan.Id);
                return $"Stopping {plan.Name}";
            });

        yield return new McpTool("startup_create", "Create a Smart Startup. Items in one step start together, and each step waits for the one before",
            McpEffect.Change, McpSchema.Object().Text("name", "Name of the startup", required: true).Text("description", "A short description").Raw("steps", StepsSchema, required: true),
            args =>
            {
                var name = args.Text("name");
                if (Startups.All.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new McpException($"A startup called {name} exists. Use startup_update to change it");
                var plan = new StartupPlan(StartupStore.NewId(), name, ReadSteps(args.Values["steps"]), args.Optional("description"));
                Startups.Save(plan);
                return DescribePlan(plan);
            });

        yield return new McpTool("startup_update", "Change a Smart Startup: its name, description or steps. Steps replace all the old ones",
            McpEffect.Change, McpSchema.Object().Text("name", "The startup to change", required: true).Text("newName", "A new name").Text("description", "A new description").Raw("steps", StepsSchema),
            args =>
            {
                var plan = Plan(args.Text("name"));
                var updated = plan with
                {
                    Name = args.Optional("newName") ?? plan.Name,
                    Description = args.Optional("description") ?? plan.Description,
                    Steps = args.Has("steps") ? ReadSteps(args.Values["steps"]) : plan.Steps,
                };
                Startups.Save(updated);
                return DescribePlan(updated);
            });

        yield return new McpTool("startup_move", "Put a Smart Startup before or after another one in the list",
            McpEffect.Change, McpSchema.Object().Text("name", "The startup to move", required: true).Text("relativeTo", "The startup it goes next to", required: true)
                .Choice("position", "Before or after it (default after)", ["before", "after"]),
            args =>
            {
                Startups.Move(Plan(args.Text("name")).Id, Plan(args.Text("relativeTo")).Id, args.Optional("position") != "before");
                return "Moved";
            });

        yield return new McpTool("startup_delete", "Delete a Smart Startup",
            McpEffect.Destructive, McpSchema.Object().Text("name", "The startup", required: true),
            args =>
            {
                var plan = Plan(args.Text("name"));
                Startups.Remove(plan.Id);
                return $"Deleted {plan.Name}";
            });
    }

    // ---- helpers ----

    private sealed record Found(string Ref, string Kind, string Key, string Name);

    /// <summary>Finds the item by its name (or reference). An unknown or ambiguous name says what is available.</summary>
    private Found Resolve(string name)
    {
        var all = Hub.All();
        var matches = all.Where(r => r.Ref.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0) matches = all.Where(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0) matches = all.Where(r => r.Name.Contains(name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 1)
        {
            var split = matches[0].Ref.Split(':', 2);
            return new Found(matches[0].Ref, split[0], split[1], matches[0].Name);
        }
        throw new McpException(matches.Count == 0
            ? $"No item is called '{name}'. Items: {string.Join(", ", all.Select(r => r.Name))}"
            : $"'{name}' matches several items: {string.Join(", ", matches.Select(r => $"{r.Name} ({r.Ref})"))}. Use the reference");
    }

    private object Describe(Found item) => new { item.Name, Kind = KindName(item.Kind), State = Hub.State(item.Ref).ToString() };

    private static string KindName(string kind) => kind switch { "docker" => "Docker", "project" => ".NET", _ => "Microfrontend" };

    private object Overview() => new
    {
        Docker = DockerList.Added.Select(n => Hub.Find($"docker:{n}")).OfType<RunnableInfo>().Select(r => new { r.Name, State = Hub.State(r.Ref).ToString() }),
        DotNet = Projects.All.Select(p => new
        {
            p.Name,
            State = Hub.State($"project:{p.Id}").ToString(),
            Urls = Catalog.Info(p.ProjectPath)?.Profile(p.Profile)?.Urls.Select(u => ProjectReader.ShiftUrl(u, p.PortOffset)),
            Instance = p.InstanceOf is not null,
        }),
        Microfrontends = Commands.OfKind(CommandKinds.SingleSpa).Select(c => new { c.Name, c.Role, c.Ports, State = Hub.State($"command:{c.Id}").ToString() }),
        Startups = Startups.All.Select(p => new { p.Name, State = (Runner.Get(p.Id)?.State ?? PlanState.Idle).ToString() }),
    };

    private async Task<object> Run(IReadOnlyList<string> names, bool start)
    {
        var items = names.Select(Resolve).DistinctBy(i => i.Ref).ToList();
        var results = await Task.WhenAll(items.Select(async item =>
        {
            var result = start ? await Hub.StartAsync(item.Ref) : await Hub.StopAsync(item.Ref);
            return new { item.Name, result.Ok, result.Message };
        }));
        return results;
    }

    private string Move(Found item, Found target, bool after)
    {
        if (item.Kind != target.Kind) throw new McpException("Items can only be moved next to an item of the same kind");
        switch (item.Kind)
        {
            case "docker": DockerList.Move(item.Key, target.Key, after); break;
            case "project": Projects.Move(item.Key, target.Key, after); break;
            default: Commands.Move(item.Key, target.Key, after); break;
        }
        return $"Moved {item.Name} {(after ? "after" : "before")} {target.Name}";
    }

    private string AddProject(string path, string? name)
    {
        var full = Folder(path);
        var file = File.Exists(full) ? full : Directory.Exists(full)
            ? ProjectReader.Scan(full, includeNonRunnable: false, maxDepth: 1).Select(p => p.Path).FirstOrDefault() ?? throw new McpException($"No runnable .NET project directly in {full}. Try dotnet_find")
            : throw new McpException($"{full} was not found");

        if (Projects.Contains(file)) throw new McpException("That project is already in Projects");
        var info = ProjectReader.Read(file);
        if (!info.IsRunnable) throw new McpException($"{info.Name} is not something that can run");
        Projects.Add([new ProjectEntry(ProjectStore.NewId(), string.IsNullOrWhiteSpace(name) ? info.Name : name, info.Path, info.Solutions.FirstOrDefault(), info.DefaultProfile, [], "")]);
        return $"Added {name ?? info.Name}";
    }

    private string Remove(Found item, string kind)
    {
        if (item.Kind != kind) throw new McpException($"{item.Name} is a {KindName(item.Kind)} item. Use the matching remove tool");
        if (kind == "project") Projects.Remove(item.Key); else Commands.Remove(item.Key);
        return $"Removed {item.Name} from the list";
    }

    private StartupPlan Plan(string name) =>
        Startups.All.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || p.Id == name)
            ?? throw new McpException($"No startup is called '{name}'. Startups: {string.Join(", ", Startups.All.Select(p => p.Name))}");

    private object DescribePlan(StartupPlan plan) => new
    {
        plan.Name,
        plan.Description,
        State = (Runner.Get(plan.Id)?.State ?? PlanState.Idle).ToString(),
        Steps = plan.Steps.Select((s, i) => new
        {
            Step = i + 1,
            Items = s.Items.Select(r => Hub.Find(r)?.Name ?? $"{r} (missing)"),
            WaitForPrevious = i == 0 || s.WaitForPrevious,
            s.TimeoutSeconds,
            IgnoreFailures = s.ContinueOnFailure,
        }),
    };

    private List<StartupStep> ReadSteps(JsonNode? node)
    {
        if (node is not JsonArray array || array.Count == 0) throw new McpException("Give at least one step");
        var steps = new List<StartupStep>();
        foreach (var step in array.OfType<JsonObject>())
        {
            var names = (step["items"] as JsonArray)?.Select(n => n?.GetValue<string>() ?? "").Where(n => n.Length > 0).ToList() ?? [];
            if (names.Count == 0) throw new McpException("Every step needs at least one item");
            var refs = names.Select(n => Resolve(n).Ref).Distinct().ToArray();
            steps.Add(new StartupStep(
                refs,
                step["waitForPrevious"]?.GetValue<bool>() ?? true,
                step["timeoutSeconds"]?.GetValue<int>() is > 0 and var seconds ? seconds : StartupStep.DefaultTimeoutSeconds,
                step["ignoreFailures"]?.GetValue<bool>() ?? false));
        }
        return steps;
    }

    private static string Folder(string path) => Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')));
}
