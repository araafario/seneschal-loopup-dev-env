using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace DevEnvironment.Projects.Docker;

public sealed record DockerOutput(int ExitCode, string Output, string Error)
{
    public bool Ok => ExitCode == 0;

    /// <summary>The first meaningful line of the error, for a message the user can read.</summary>
    public string Reason => Error.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "Unknown error";
}

/// <summary>Runs the docker command line and returns what it printed. Arguments are passed one by one, so nothing needs quoting.</summary>
internal static class DockerCli
{
    public const int NotFound = -2;
    public const int TimedOut = -1;

    public static async Task<DockerOutput> RunAsync(IEnumerable<string> args, TimeSpan timeout)
    {
        var start = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException("The process did not start.");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return new DockerOutput(NotFound, "", "The docker command was not found");
        }

        using (process)
        {
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(timeout);
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
                return new DockerOutput(TimedOut, "", "Docker did not answer in time");
            }
            return new DockerOutput(process.ExitCode, await output, await error);
        }
    }
}
