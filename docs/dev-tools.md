# Projects and Logs

Run your Docker containers, .NET projects and microfrontends from one page and follow their logs side by side.
Two of the three tools of the extension, which share code, listed under the **LoopUp** group (the third, the Token Generator, is in the [README](../README.md)):

| Tool | Id | What it does |
| --- | --- | --- |
| Projects | `projects` | Start, stop and watch your Docker containers, .NET projects and microfrontends |
| Logs | `logs` | Follow several logs at once in a grid |

Docker containers are managed on the Projects page too (see below). Both are `Persistent`, so the search box, selection, panes and scroll positions stay while you visit other pages.

## .NET

Add the projects you work on and control them from one list.

- **Add projects**: browse to a project, solution or folder, or scan a folder tree. The `.csproj` and `Properties/launchSettings.json` are read for the profiles and ports.
- **Start, stop, restart** each project. A start runs `dotnet run` in its own process that writes to a log file and is **not tied to Seneschal**.
  The process id is saved and checked again at startup, so a project keeps running while the app restarts and is picked up again afterwards.
- **Status**: running, stopped, ended on its own (crashed or closed by hand) or *external*, which means something else already listens on the project's port, for example the same project running in Visual Studio.
  For an external one you can stop the process that holds it, after a confirmation.
- **Edit**: display name, launch profile, the solution to open in Visual Studio, extra environment variables (they win over the profile's own) and arguments passed after `--`.
- **More actions**: open the folder in VS Code, show it in Explorer, open the solution in Visual Studio, or remove it from the list. Removing never touches the project's files.
- **Busy and loading:** a thin bar on a section's header shows while that section is still reading (the first look at Docker, the first read of the project files) or while something is running (a start, stop or restart). It never shows for the regular refresh.
  A row is locked while it is being started, stopped or restarted and says so ("Stopping..."), so a second click cannot start a second stop. This includes stopping a process that runs elsewhere, after you confirm it.
- **Show logs** (the terminal icon on a row) opens one big log window for that project alone, with its running state at the top. It is fixed to that project and never changes the Logs tool's layout or panes. Pause, clear, filter and wrap work inside the window only.

## Several instances of a .NET project

Run the same project more than once, for example to try scale-out where several instances compete for the same messages.
Open a project's menu and choose **Add instances...**: pick how many and a **port step** (100 by default), and the dialog shows the address each instance will get.
An instance is a row of its own (named `Gateway #2`, marked *instance*, kept under the project), so it can be started, stopped, filtered, ordered, shown in the Logs tool and used in a Smart Startup like any other.

**How it works**

- **A port of its own.** An instance listens on the project's addresses moved by its **port offset** (offset 100 turns `http://localhost:5039` into `http://localhost:5139`), given with `--urls` after the `--`. Each new instance goes above every offset already in use, so two never share a port. The offset can be changed under *Edit*.
- **One build.** Instances share the project's `bin` folder, so two builds at once would break on locked files. The first instance to start builds, the others wait for that build and then run it with `--no-build`. While any instance runs, the others never rebuild.
- **Siblings are not strangers.** A stopped instance is not reported as *running elsewhere* just because another instance holds the project's files: processes below one this runner started are recognised as its own.
- **Stopping one stops only that one.** Each instance has its own process, log and state, and survives an app restart like any project.

**When a service cannot be scaled**

Some services fix their port in code (`ListenAnyIP(5010)` in `Program.cs`). The address in code wins over anything given from outside, so a second instance would fail with *address in use*. The dialog finds these (it names the file and line) and warns before anything is added.
Each instance is also given a **`PortOffset`** setting (an environment variable, which .NET reads as configuration). A service opts in with one line:

```csharp
builder.WebHost.ConfigureKestrel(o => o.ListenAnyIP(5010 + builder.Configuration.GetValue("PortOffset", 0)));
```

Without the setting the offset is 0, so the service behaves exactly as before. A line that mentions `PortOffset` is no longer reported.
A port written in code is also what Seneschal waits for and checks when the launch profile has none, so *ready* and *port already in use* work for these projects too.

**Not covered.** Everything else two instances might share is up to the service: a database or read model that expects one writer, a fixed file path, or a message endpoint that must be unique. Competing consumers on one queue (the usual Rebus setup) is what instances are good for.

## Ordering the lists

The rows of every section (Smart Startup, Docker, .NET and Microfrontends) can be **dragged into a new order**. Each row has a small grip (six dots) at the left that is faint until you hover over the row, with the tooltip *Drag to reorder*. The whole row can be dragged, not only the grip. A line shows where the row will land (above or below the one under the pointer), and the order is saved with the list, so it is the same after a restart. A row only moves within its own section, and dragging works while a filter is on.
New items are added at the end, in the order they were shown in the picker. `wwwroot/sortable.js` does the dragging (Blazor Server cannot start a native drag by itself) and `ListMove` moves the item in the saved list.

## Smart Startup

A startup says what to start and in which order, across containers, .NET projects and microfrontends. It sits at the top of the Projects page.

- **New startup** opens an editor with a name, an optional **description** (shown under the name in the list) and a card for each **step**. Add items to a step from a picker that shows everything on the page, grouped by kind. An item can belong to one step only.
- **Items of a step always start together.** Each step after the first has a choice, *After step N* or *With step N*:
  - **After step N** means the step starts only when everything before it is ready.
  - **With step N** means it starts at the same moment as the step before it.
  So "databases first, then the services, and the microfrontends at the same time as the services" is three steps with the last one's switch turned off.
- Each step also has *Timeout* (120 seconds by default) and *Ignore failures*. A failure or a timeout ends the startup unless the step says to keep going.
- **Ready** means what each section already shows: a container that is running (and healthy when it has a health check), a .NET project or microfrontend that is running (its port is listening). Something that was already running counts as ready and is left alone.
- A running startup shows its progress: a dot per item that goes from grey to amber to green or red. **Stop waiting** ends the wait and leaves what already started running.
- **Stop** (after a confirmation) stops everything in the startup, **last step first**.
- Startups are saved in `startup.json`. Starting does not need this page to stay open, the items themselves keep running when Seneschal restarts.

## Microfrontends

A section for **single-spa** packages (the root config, applications and utility modules), started with the dev server of their package, such as `npm start`.

- **Add microfrontends** scans a folder (the last one is remembered and scanned again right away) and lists the single-spa packages in it with their role and port. Only packages that can be started are listed, so libraries and component kits are left out.
  A package counts as single-spa when it depends on `single-spa`, or when its start script runs a webpack or vite dev server on a known port.
- **Roles** are read from the package: *Root config* (depends on `single-spa-layout` or is named `...root-config`), *Application* (depends on a `single-spa-*` package) and *Utility module* (served the same way but not an application).
- **Ports** come from the start script (`--port 7023`, `PORT=7023`) or from `webpack.config`, `vite.config` or `rspack.config` (`port: 7023`). The package manager comes from the lock file (npm, pnpm, yarn or bun).
- The toolbar is the same as the other sections: *Select all*, **Start** and **Stop** for the ticked rows. Anything already running is left alone when you press Start.
- Two packages that want the **same port** are flagged in the scan and on the row. Starting both in one go starts the first and warns about the other, and a port that is taken by something else (even a dev server in a terminal) shows the row as *running elsewhere* with the option to stop that process.
- Like the .NET runner each one is started detached with its output in a log file, so it keeps running when Seneschal restarts and is picked up again. Stopping ends the process and everything it started (through `taskkill /T`, because `npm start` runs four processes deep and the built-in tree kill leaves the last one behind).
- **Edit** changes the command, role, port, shell (cmd or PowerShell) and environment variables.

The engine underneath is generic (a folder, a command line, a shell, ports, an optional ready text), so other kinds of commands can get their own section later.

## Docker

The **Docker** section works like **Services**: a collapsible header, then its own toolbar (**Add containers**, a filter, *Select all* and **Start** and **Stop** for the ticked rows) and a list.

- **Add containers** opens a picker that reads the containers from Docker (`docker ps -a`), grouped by compose project, with a filter and a checkbox per group.
  Only the containers you pick are listed, and the list is saved in `docker.json`. Remove one from the row menu, which never touches the container itself.
- Rows show the compose service name, the image, the compose project, the health state and links to published ports.
  A container that was removed from Docker stays in the list as *not found* until you remove it.
- **Start, stop and restart** use `docker start`, `docker stop` and `docker restart` on the existing containers. Nothing is created or removed, so it is quick after a reboot.
- When the engine does not answer (usually after a reboot) the section says *Docker is not running* with a **Start Docker Desktop** button, and fills in by itself once Docker is up.
- Every listed running container is followed with `docker logs --follow` into a file, so it shows up in the Logs tool under the *Docker* group, and the terminal icon on a row opens its own log window (same as for projects).
  The followers live in a Windows job object, so they end with the app even if it is killed or restarted.

Starting several containers at once does not follow `depends_on`, so a container that needs another one may exit and need a second start. Ordered starts are planned as stacks.

## Logs

The Logs tool is where you arrange logs the way you want. Its layout and the log in each pane are saved and only change when you change them there. The log windows opened from the Projects page are separate and leave all of that alone.

- Layouts: one pane, two or three or four side by side, two or three or four stacked, and a 2 by 2 grid.
- Each pane has a log picker (*None* leaves it empty), a filter with a clear button, **Pause**, **Clear** and a wrap toggle.
  **Pause** freezes the view of that pane only. The process and the log keep going, and the pane says how many lines are waiting.
- Long lines either scroll sideways or wrap. The toolbar sets it for every pane and a pane can override it for itself.
- The view follows the newest line and stops following when you scroll up.
- Lines are colored by level (`fail` and `crit`, `warn`, and dimmed `trce` and `dbug`).
- A pane shows at most the last 1000 lines. Panes that show the same file share one reader, and files are only polled while a pane is on screen.

The Logs tool shows whatever an `ILogSourceProvider` offers. Today the Projects tool is the one provider (`ProjectLogSources`). The interface lives in this extension rather than in the SDK, so other extensions cannot offer logs to it yet.

## For the AI assistant

Every feature is also a tool for the Assistant (`IToolMcp` on `ProjectsTool` and `LogsTool`). Items are named as the page names them.

| Tool | Does |
| --- | --- |
| `projects_overview` `projects_status` | Everything in Projects with its state, or only the named items |
| `projects_start` `projects_stop` `projects_move` | Start, stop and reorder containers, .NET projects and microfrontends |
| `docker_available` `docker_add` `docker_remove` | Choose which containers are listed |
| `dotnet_find` `dotnet_add` `dotnet_add_instances` `dotnet_remove` | Find, add, copy (more instances on other ports) and remove .NET projects |
| `microfrontend_find` `microfrontend_add` `microfrontend_remove` | Find, add and remove single-spa microfrontends |
| `startup_list` `startup_run` `startup_status` `startup_stop` `startup_create` `startup_update` `startup_move` `startup_delete` | Smart Startup |
| `logs_sources` `logs_read` `logs_show` | List logs, read the end of one (optionally filtered), show one on the Logs page |

Anything that removes something (`*_remove`, `startup_delete`) asks the user first in the assistant panel.
## Layout

```
Seneschal.LoopupDevEnv/   the extension, as one Razor class library built against the Seneschal.Sdk package
  extension.json          manifest: id loopup-dev-env, styles devenvironment.css and loopup-token-generator.css
  wwwroot/                devenvironment.css, loopup-token-generator.css, sortable.js
  Token/                  the Token Generator (see the README)
Projects/
  ProjectsTool.cs         tool metadata, services and the startup hook
  ProjectsView.razor      the page: the Docker and Services sections
  AddProjectsDialog.razor, EditProjectDialog.razor
  Engine/
    ProjectStore.cs       the saved list (projects.json)
    ProjectReader.cs      reads .csproj, launch profiles, ports and finds the solutions
    ProjectRunner.cs      starts and stops processes, tracks them and finds port conflicts
    ProjectLogSources.cs  offers each project's log to the Logs tool
  Commands/
    CommandStore.cs       the entries and the last scanned folder (commands.json)
    CommandRunner.cs      starts and stops commands, tracks them and finds port conflicts
    SingleSpaScanner.cs   finds single-spa packages, their role, command and port
    CommandLogSources.cs  offers each command's log to the Logs tool
  Startup/
    RunnableHub.cs        treats containers, projects and microfrontends alike (state, start, stop)
    StartupStore.cs       the startups and their steps (startup.json)
    StartupRunner.cs      runs a startup in waves and stops it in reverse
  AddInstancesDialog.razor  adds instances of a project
  Docker/
    DockerCli.cs          runs the docker command line
    DockerRunner.cs       reads the containers and starts, stops and restarts them
    DockerStore.cs        the containers you added (docker.json)
    DockerLogs.cs         follows the containers' logs into files for the Logs tool
  SectionFrame.razor      the collapsible frame shared by the sections
  StartupSection.razor, StartupPlanDialog.razor, PickRunnablesDialog.razor   the Smart Startup section, its editor and the item picker
  ProcessLogDialog.razor  the log window of one project or container
  DockerSection.razor, AddContainersDialog.razor   the Docker section and its picker
  DotNetSection.razor     the .NET section
  MicrofrontendsSection.razor, AddMicrofrontendsDialog.razor, EditMicrofrontendDialog.razor   the Microfrontends section
    FileLocks.cs          asks Windows which process holds a file (Restart Manager)
Logs/
  LogsTool.cs, LogsView.razor (toolbar and grid), LogPane.razor (one pane)
Logging/Logging.cs        ILogSourceProvider, LogSources, LogFeed, LogHub, LogViewerState
Projects/ProjectsMcp.cs  the tools the AI assistant gets for Projects
Shared/                   ErrorReport and ErrorsDialog (short toast, details popup), FileLockers, SortableList and ListMove (drag to reorder), DevEnvironmentPaths, ShellLauncher (VS Code, Explorer, Visual Studio), ChildProcessJob, ProcessTools (shared by the runners)
```

## Data

Everything is kept in the extension's data folder, `%AppData%/Seneschal/extension-data/loopup-dev-env`:

| File | Content |
| --- | --- |
| `projects.json` | The .NET projects you added |
| `startup.json` | Your startups |
| `commands.json` | The microfrontends you added and the last folder you scanned |
| `docker.json` | The containers you added |
| `logviewer.json` | The layout and which log is in each pane |
| `logs/` | One log file per project that the runner started and one per followed container (`docker-<name>.log`) |

A broken `projects.json` never stops the app, it starts with an empty list.
Files from earlier copies are copied over once, from the app's own data folder and from `extension-data/dev-environment` (the folder of the Dev Environment extension that used to ship with Seneschal), and nothing that already exists is overwritten. The originals stay where they were.

## Build and restart

A rebuild is loaded by Seneschal by itself, with no restart: the extension's services (the runners, the stores, the log hub) live in a container of their own that is replaced, and the old one is disposed, which stops its timers and ends the log followers. The new services read their saved files again, and projects the runner started are picked up again by their saved process ids, exactly as after a restart. They keep running through it.

```powershell
dotnet build Seneschal.LoopupDevEnv
```

This stages `Seneschal.LoopupDevEnv/artifacts/extensions/loopup-dev-env/` and packs `Seneschal.LoopupDevEnv/artifacts/packages/loopup-dev-env.snx`.
