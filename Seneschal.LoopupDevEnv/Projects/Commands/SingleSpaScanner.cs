using System.Text.Json;
using System.Text.RegularExpressions;

namespace DevEnvironment.Projects.Commands;

/// <summary>A single-spa package found on disk that can be started.</summary>
public sealed record MicrofrontendFound(
    string Name,
    string PackageName,
    string Directory,
    string Role,
    int? Port,
    string CommandLine);

/// <summary>
/// Finds the single-spa packages under a folder: the root config, the applications and the utility modules that are served by a dev server.
/// A package is taken for one when it depends on single-spa, or when its start script runs a webpack or vite dev server on a port.
/// Packages that cannot be started (libraries, component kits) are left out.
/// </summary>
public static partial class SingleSpaScanner
{
    // "package" is where several of these repos copy their package.json when they publish, which is not a second package
    private static readonly string[] SkippedFolders = ["node_modules", ".git", ".vs", ".idea", "dist", "build", "coverage", ".deleted", "package"];
    private static readonly string[] StartScripts = ["start", "dev", "serve"];
    private static readonly string[] ConfigPatterns = ["webpack.config.*", "vite.config.*", "rspack.config.*"];

    /// <summary>The packages in this folder and the folders below it (two levels by default, which covers a folder of repos).</summary>
    public static IReadOnlyList<MicrofrontendFound> Scan(string path, int maxDepth = 2, int maxFolders = 400)
    {
        var folder = File.Exists(path) ? Path.GetDirectoryName(path)! : path;
        var found = new List<MicrofrontendFound>();
        var visited = 0;
        Walk(folder, 0);
        return found.OrderBy(f => f.Role == SingleSpaRoles.Root ? 0 : 1).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();

        void Walk(string dir, int depth)
        {
            if (++visited > maxFolders) return;
            try
            {
                if (File.Exists(Path.Combine(dir, "package.json")) && Read(dir) is { } package)
                {
                    // A package's own folders (src, config, a published copy) are never other packages
                    found.Add(package);
                    return;
                }
                if (depth >= maxDepth) return;
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    var name = Path.GetFileName(sub);
                    if (!name.StartsWith('.') && !SkippedFolders.Contains(name, StringComparer.OrdinalIgnoreCase))
                        Walk(sub, depth + 1);
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // A folder we cannot read is skipped
            }
        }
    }

    /// <summary>The package in this folder, or null when it is not a single-spa package that can be started.</summary>
    public static MicrofrontendFound? Read(string directory)
    {
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "package.json")),
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            root = doc.RootElement.Clone();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
        if (root.ValueKind != JsonValueKind.Object) return null;

        var packageName = root.TryGetProperty("name", out var nameProperty) && nameProperty.GetString() is { Length: > 0 } n ? n : Path.GetFileName(directory);

        // The script that starts it
        var scripts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("scripts", out var scriptsProperty) && scriptsProperty.ValueKind == JsonValueKind.Object)
        {
            foreach (var script in scriptsProperty.EnumerateObject())
                if (script.Value.ValueKind == JsonValueKind.String) scripts[script.Name] = script.Value.GetString() ?? "";
        }
        var startName = StartScripts.FirstOrDefault(scripts.ContainsKey);
        if (startName is null) return null;
        var startScript = scripts[startName];

        var dependencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var section in new[] { "dependencies", "devDependencies", "peerDependencies" })
        {
            if (root.TryGetProperty(section, out var deps) && deps.ValueKind == JsonValueKind.Object)
                foreach (var dep in deps.EnumerateObject()) dependencies.Add(dep.Name);
        }

        var port = PortInScript(startScript) ?? PortInConfig(directory);
        var usesSingleSpa = dependencies.Any(d => d.Equals("single-spa", StringComparison.OrdinalIgnoreCase) || d.StartsWith("single-spa-", StringComparison.OrdinalIgnoreCase));
        var runsDevServer = DevServer().IsMatch(startScript);
        if (!usesSingleSpa && !(runsDevServer && port is not null)) return null;

        var role = dependencies.Contains("single-spa-layout") || packageName.EndsWith("root-config", StringComparison.OrdinalIgnoreCase)
            ? SingleSpaRoles.Root
            : usesSingleSpa ? SingleSpaRoles.Application : SingleSpaRoles.Utility;

        return new MicrofrontendFound(Path.GetFileName(directory), packageName, directory, role, port, StartCommand(directory, startName));
    }

    /// <summary>The command that runs a script with the package manager the folder uses (npm unless a pnpm, yarn or bun lock file is there).</summary>
    public static string StartCommand(string directory, string script)
    {
        string manager;
        if (File.Exists(Path.Combine(directory, "pnpm-lock.yaml"))) manager = "pnpm";
        else if (File.Exists(Path.Combine(directory, "yarn.lock"))) manager = "yarn";
        else if (File.Exists(Path.Combine(directory, "bun.lockb")) || File.Exists(Path.Combine(directory, "bun.lock"))) manager = "bun";
        else manager = "npm";

        return manager == "npm" && script == "start" ? "npm start" : $"{manager} run {script}";
    }

    /// <summary>A port named in the script itself, such as "webpack server --port 7023" or "PORT=7023 node server.js".</summary>
    internal static int? PortInScript(string script)
    {
        var match = ScriptPort().Match(script);
        if (!match.Success) return null;
        var digits = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
        return int.TryParse(digits, out var port) ? port : null;
    }

    /// <summary>A port written in the webpack, vite or rspack config of the folder, such as "port: 7023".</summary>
    internal static int? PortInConfig(string directory)
    {
        foreach (var pattern in ConfigPatterns)
        {
            foreach (var file in Directory.EnumerateFiles(directory, pattern))
            {
                try
                {
                    var match = ConfigPort().Match(File.ReadAllText(file));
                    if (match.Success && int.TryParse(match.Groups[1].Value, out var port)) return port;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Try the next file
                }
            }
        }
        return null;
    }

    [GeneratedRegex(@"(?:--port|-p)[ =](\d{2,5})|\bPORT=(\d{2,5})")]
    private static partial Regex ScriptPort();

    [GeneratedRegex(@"\bport\s*:\s*(\d{2,5})")]
    private static partial Regex ConfigPort();

    [GeneratedRegex(@"\b(webpack|webpack-dev-server|vite|rspack|rsbuild)\b")]
    private static partial Regex DevServer();
}
