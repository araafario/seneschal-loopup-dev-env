using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace DevEnvironment.Projects;

/// <summary>A port that is written into the code of the project (see <see cref="ProjectReader.CodePorts"/>).</summary>
public sealed record CodePort(int Port, string File, int Line);

/// <summary>One "Project" profile from Properties/launchSettings.json.</summary>
public sealed record LaunchProfile(string Name, string[] Urls, Dictionary<string, string> Env)
{
    /// <summary>The ports the profile makes the app listen on (from applicationUrl and ASPNETCORE_URLS).</summary>
    public IReadOnlyList<int> Ports =>
        Urls.Concat(Env.TryGetValue("ASPNETCORE_URLS", out var urls) ? urls.Split(';') : [])
            .Select(ProjectReader.PortOf)
            .OfType<int>()
            .Distinct()
            .ToList();
}

/// <summary>What a .csproj and the files around it say about a project.</summary>
public sealed record ProjectInfo(
    string Name,
    string Path,
    string Directory,
    string[] TargetFrameworks,
    bool IsWeb,
    bool IsTest,
    bool IsRunnable,
    IReadOnlyList<LaunchProfile> Profiles,
    IReadOnlyList<string> Solutions)
{
    public LaunchProfile? Profile(string? name) =>
        name is null ? null : Profiles.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The profile a new entry should start with: the first one, which is what Visual Studio selects too.</summary>
    public string? DefaultProfile => Profiles.Count > 0 ? Profiles[0].Name : null;

    public string Kind => IsWeb ? "Web" : IsTest ? "Tests" : IsRunnable ? "App" : "Library";
}

/// <summary>Reads .csproj, launchSettings.json and finds the solutions that contain a project.</summary>
public static partial class ProjectReader
{
    private static readonly string[] SkippedFolders = ["bin", "obj", "node_modules", ".git", ".vs", ".idea", ".deleted", "packages", "TestResults"];

    public static ProjectInfo Read(string projectPath, bool findSolutions = true)
    {
        var path = Path.GetFullPath(projectPath);
        var directory = Path.GetDirectoryName(path)!;

        XElement root;
        try
        {
            root = XDocument.Load(path).Root ?? throw new InvalidDataException("The project file is empty.");
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException($"The project file is not valid XML: {ex.Message}");
        }

        string? Property(string name) => root.Descendants()
            .Where(e => e.Name.LocalName == name)
            .Select(e => e.Value.Trim())
            .FirstOrDefault(v => v.Length > 0);

        var sdk = (string?)root.Attribute("Sdk") ?? "";
        var isWeb = sdk.Contains("Sdk.Web", StringComparison.OrdinalIgnoreCase);
        var outputType = Property("OutputType");
        var isTest = string.Equals(Property("IsTestProject"), "true", StringComparison.OrdinalIgnoreCase)
            || root.Descendants().Any(e => e.Name.LocalName == "PackageReference"
                && string.Equals((string?)e.Attribute("Include"), "Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase));
        var frameworks = (Property("TargetFrameworks") ?? Property("TargetFramework") ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return new ProjectInfo(
            Name: Property("AssemblyName") ?? Path.GetFileNameWithoutExtension(path),
            Path: path,
            Directory: directory,
            TargetFrameworks: frameworks,
            IsWeb: isWeb,
            IsTest: isTest,
            IsRunnable: !isTest && (isWeb || string.Equals(outputType, "Exe", StringComparison.OrdinalIgnoreCase)),
            Profiles: ReadProfiles(directory),
            Solutions: findSolutions ? FindSolutions(directory, Path.GetFileName(path)) : []);
    }

    /// <summary>The .csproj files under a folder (or the folder of a .sln), skipping build output and similar.</summary>
    public static IReadOnlyList<ProjectInfo> Scan(string path, bool includeNonRunnable, int maxDepth = 6, int maxProjects = 300)
    {
        var projects = new List<string>();
        if (File.Exists(path) && path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            projects.Add(path);
        else
        {
            var folder = File.Exists(path) ? Path.GetDirectoryName(path)! : path;
            Walk(folder, 0);

            void Walk(string dir, int depth)
            {
                if (projects.Count >= maxProjects) return;
                try
                {
                    projects.AddRange(Directory.EnumerateFiles(dir).Where(f => f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)));
                    if (depth >= maxDepth) return;
                    foreach (var sub in Directory.EnumerateDirectories(dir))
                    {
                        if (!SkippedFolders.Contains(Path.GetFileName(sub), StringComparer.OrdinalIgnoreCase))
                            Walk(sub, depth + 1);
                    }
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    // A folder we cannot read is simply skipped
                }
            }
        }

        var found = new List<ProjectInfo>();
        foreach (var project in projects)
        {
            try
            {
                var info = Read(project, findSolutions: false);
                if (includeNonRunnable || info.IsRunnable) found.Add(info);
            }
            catch (InvalidDataException)
            {
                // Not a readable project file
            }
        }
        return found.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The port in an address such as https://localhost:7228, http://*:5000 or http://[::]:8080, or null.</summary>
    public static int? PortOf(string url)
    {
        var match = PortAtEnd().Match(url.Trim());
        return match.Success && int.TryParse(match.Groups[1].Value, out var port) ? port : null;
    }

    /// <summary>The same address with its port moved by <paramref name="offset"/> (http://localhost:5039 and 100 give http://localhost:5139).</summary>
    public static string ShiftUrl(string url, int offset)
    {
        if (offset == 0 || PortOf(url) is not { } port) return url;
        var match = PortAtEnd().Match(url.Trim());
        return url.Trim()[..match.Index] + ":" + (port + offset) + (match.Value.EndsWith('/') ? "/" : "");
    }

    /// <summary>
    /// Ports that Program.cs fixes in code, such as ListenAnyIP(5010). A project like that cannot run a second copy on another port,
    /// because the address in code wins over anything given from outside.
    /// </summary>
    public static IReadOnlyList<CodePort> CodePorts(string directory)
    {
        var file = Path.Combine(directory, "Program.cs");
        if (!File.Exists(file)) return [];
        var found = new List<CodePort>();
        try
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains("PortOffset", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (Match m in FixedPort().Matches(lines[i]))
                {
                    var digits = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                    if (int.TryParse(digits, out var port)) found.Add(new CodePort(port, "Program.cs", i + 1));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing to report
        }
        return found;
    }

    private static List<LaunchProfile> ReadProfiles(string directory)
    {
        var file = Path.Combine(directory, "Properties", "launchSettings.json");
        if (!File.Exists(file)) return [];

        var result = new List<LaunchProfile>();
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file),
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (!doc.RootElement.TryGetProperty("profiles", out var profiles) || profiles.ValueKind != JsonValueKind.Object)
                return result;

            foreach (var profile in profiles.EnumerateObject())
            {
                var value = profile.Value;
                if (value.ValueKind != JsonValueKind.Object) continue;
                // IISExpress and Executable profiles are not something "dotnet run" can start
                if (value.TryGetProperty("commandName", out var command) && command.GetString() != "Project") continue;

                var urls = value.TryGetProperty("applicationUrl", out var url) && url.GetString() is { } text
                    ? text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    : [];
                var env = new Dictionary<string, string>();
                if (value.TryGetProperty("environmentVariables", out var vars) && vars.ValueKind == JsonValueKind.Object)
                {
                    foreach (var variable in vars.EnumerateObject())
                        env[variable.Name] = variable.Value.ToString();
                }
                result.Add(new LaunchProfile(profile.Name, urls, env));
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // A broken launchSettings.json just means no profiles
        }
        return result;
    }

    /// <summary>Solutions in the project's folder or above it that list this project, nearest first.</summary>
    private static List<string> FindSolutions(string directory, string projectFileName)
    {
        var result = new List<string>();
        var current = new DirectoryInfo(directory);
        for (var level = 0; level < 8 && current is not null; level++, current = current.Parent)
        {
            try
            {
                foreach (var file in current.GetFiles().Where(f => f.Extension.Equals(".sln", StringComparison.OrdinalIgnoreCase)
                                                                 || f.Extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase)))
                {
                    if (File.ReadAllText(file.FullName).Contains(projectFileName, StringComparison.OrdinalIgnoreCase))
                        result.Add(file.FullName);
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // Keep walking up
            }
        }
        return result;
    }

    [GeneratedRegex(@":(\d{1,5})/?$")]
    private static partial Regex PortAtEnd();

    [GeneratedRegex(@"\.Listen(?:AnyIP|Localhost|LocalHost)?\(\s*(\d{2,5})|UseUrls\(\s*""[^""]*:(\d{2,5})")]
    private static partial Regex FixedPort();
}
