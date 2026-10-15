using System.Diagnostics;

namespace DevEnvironment.Shared;

/// <summary>
/// Opens things on the desktop (Explorer, Visual Studio, the editor). When SENESCHAL_NO_SHELL is set nothing is launched and
/// the intended command is appended to shell.log in the extension's data folder instead (used by automated runs).
/// </summary>
internal sealed class ShellLauncher
{
    private static bool Suppressed => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SENESCHAL_NO_SHELL"));

    /// <summary>Opens Explorer with the file selected, or the folder itself.</summary>
    public void Reveal(string path)
    {
        var args = File.Exists(path) ? $"/select,\"{path}\"" : $"\"{path}\"";
        Run("explorer.exe", args);
    }

    /// <summary>Opens a file with the app Windows associates with it: a .sln or .slnx opens in Visual Studio.</summary>
    public void Open(string path)
    {
        if (Suppressed)
        {
            Run("shell-open", $"\"{path}\"");
            return;
        }
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    /// <summary>Opens a folder in VS Code when available, otherwise in Explorer.</summary>
    public void OpenInEditor(string folder)
    {
        if (!Suppressed && OnPath("code.cmd") is { } code)
            Run("cmd.exe", $"/c \"\"{code}\" -r \"{folder}\"\"");
        else
            Run("explorer.exe", $"\"{folder}\"");
    }

    private static void Run(string file, string args)
    {
        if (Suppressed)
        {
            File.AppendAllText(DevEnvironmentPaths.FilePath("shell.log"), $"{file} {args}{Environment.NewLine}");
            return;
        }
        Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true });
    }

    private static string? OnPath(string exe) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir.Trim(), exe))
            .FirstOrDefault(File.Exists);
}
