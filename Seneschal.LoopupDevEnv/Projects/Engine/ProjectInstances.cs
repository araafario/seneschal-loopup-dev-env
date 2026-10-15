using System.Text.RegularExpressions;

namespace DevEnvironment.Projects;

/// <summary>
/// Makes more copies of a project that listen on their own ports. A copy is the same project with a number in its name and a port offset
/// above every offset in use, so a new one never lands on the port of one that is already there.
/// </summary>
public static partial class ProjectInstances
{
    public static IEnumerable<ProjectEntry> Family(ProjectStore store, ProjectEntry entry) =>
        store.All.Where(e => e.ProjectPath.Equals(entry.ProjectPath, StringComparison.OrdinalIgnoreCase));

    /// <summary>The highest number already used in a name such as "Gateway #3" (1 for the first, which has no number).</summary>
    public static int HighestNumber(ProjectStore store, ProjectEntry entry) =>
        Family(store, entry).Select(e => Numbered().Match(e.Name))
            .Where(m => m.Success)
            .Select(m => int.Parse(m.Groups[1].Value))
            .DefaultIfEmpty(1)
            .Max();

    public static int HighestOffset(ProjectStore store, ProjectEntry entry) =>
        Family(store, entry).Select(e => e.PortOffset).DefaultIfEmpty(0).Max();

    public static List<ProjectEntry> Create(ProjectStore store, ProjectEntry entry, int count, int step)
    {
        var number = HighestNumber(store, entry);
        var offset = HighestOffset(store, entry);
        return Enumerable.Range(0, count).Select(i => entry with
        {
            Id = ProjectStore.NewId(),
            Name = $"{entry.Name} #{number + 1 + i}",
            Env = new Dictionary<string, string>(entry.Env),
            InstanceOf = entry.Id,
            PortOffset = offset + step * (i + 1),
        }).ToList();
    }

    [GeneratedRegex(@" #(\d+)$")]
    private static partial Regex Numbered();
}
