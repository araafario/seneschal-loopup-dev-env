using Seneschal.Sdk;
using DevEnvironment.Logging;
using DevEnvironment.Shared;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;

namespace DevEnvironment.Logs;

// Shows any log that a tool offers through ILogSourceProvider, several side by side.
public sealed class LogsTool : IToolModule, IToolLifecycle, IToolMcp
{
    public ToolMetadata Metadata { get; } = new(
        Id: "logs",
        Title: "Logs",
        Description: "Follow several logs side by side",
        Group: "LoopUp",
        Icon: Icons.Material.Filled.Terminal,
        ComponentType: typeof(LogsView),
        SearchTerms: ["log", "logs", "console", "output", "tail"])
    {
        // Keeps the panes, their filters and scroll positions while you visit other pages
        Persistent = true,
        Prompts = ["Show me the last errors in my logs"],
    };

    public void ConfigureServices(IServiceCollection services, IExtensionContext context)
    {
        DevEnvironmentPaths.Initialize(context.DataDirectory);

        services.AddSingleton<LogSources>();
        services.AddSingleton<LogHub>();
        services.AddSingleton<LogViewerState>();
    }

    public IEnumerable<McpTool> McpTools(IServiceProvider services)
    {
        var sources = services.GetRequiredService<LogSources>();
        var viewer = services.GetRequiredService<LogViewerState>();

        LogSource Find(string name)
        {
            var all = sources.All;
            var matches = all.Where(s => s.Id == name).ToList();
            if (matches.Count == 0) matches = all.Where(s => s.Title.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 0) matches = all.Where(s => s.Title.Contains(name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 1) return matches[0];
            throw new McpException(matches.Count == 0
                ? $"No log is called '{name}'. Logs: {string.Join(", ", all.Select(s => s.Title))}"
                : $"'{name}' matches several logs: {string.Join(", ", matches.Select(s => $"{s.Title} ({s.Id})"))}. Use the id");
        }
        yield return new McpTool("logs_sources", "List the logs that can be read: one per project, container and microfrontend",
            McpEffect.Read, McpSchema.None, _ => sources.All.Select(s => new { s.Id, s.Title, s.Group }));

        yield return new McpTool("logs_read", "Read the end of a log, optionally only the lines that contain some text. Use it to find out why something failed",
            McpEffect.Read, McpSchema.Object().Text("source", "The log's title or id from logs_sources", required: true)
                .Number("lines", "How many of the last lines (default 100, at most 500)").Text("contains", "Only lines that contain this text (not case sensitive)"),
            args =>
            {
                var source = Find(args.Text("source"));
                var wanted = Math.Clamp(args.Number("lines", 100), 1, 500);
                var contains = args.Optional("contains");
                var lines = ReadTail(source.Path, 600_000)
                    .Where(l => contains is null || l.Contains(contains, StringComparison.OrdinalIgnoreCase))
                    .TakeLast(wanted)
                    .ToList();
                return lines.Count == 0 ? "The log has nothing" + (contains is null ? "" : $" that contains '{contains}'") : string.Join('\n', lines);
            });

        yield return new McpTool("logs_show", "Show a log on the Logs page, in the first pane",
            McpEffect.Change, McpSchema.Object().Text("source", "The log's title or id from logs_sources", required: true),
            args =>
            {
                var source = Find(args.Text("source"));
                viewer.Show(source.Id);
                return $"Showing {source.Title} on the Logs page";
            });
    }

    /// <summary>The last lines of a file that another process may be writing to.</summary>
    private static IEnumerable<string> ReadTail(string path, int maxBytes)
    {
        if (!File.Exists(path)) return [];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var start = Math.Max(0, stream.Length - maxBytes);
        stream.Seek(start, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
        var text = reader.ReadToEnd().Replace("\r\n", "\n");
        var lines = text.Split('\n').ToList();
        if (start > 0 && lines.Count > 0) lines.RemoveAt(0); // the first line is cut
        return lines.Where(l => l.Length > 0);
    }
}