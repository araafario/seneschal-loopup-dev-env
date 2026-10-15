using System.Text.Json;

namespace DevEnvironment.Token;

/// <summary>The environment last used and the email last used for each, so the next token is one click away. Passwords are never saved.</summary>
public sealed class TokenSettings
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private sealed record Saved(string? Environment, Dictionary<string, string>? Emails);

    private readonly object _gate = new();
    private readonly string _file;
    private string _environment = TokenEnvironment.All[0].Id;
    private readonly Dictionary<string, string> _emails = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="dataDirectory">The extension's own folder, which the app gives it.</param>
    public TokenSettings(string dataDirectory)
    {
        _file = Path.Combine(dataDirectory, "token.json");
        AdoptEarlier(dataDirectory);

        try
        {
            if (!File.Exists(_file)) return;
            var saved = JsonSerializer.Deserialize<Saved>(File.ReadAllText(_file));
            if (saved?.Environment is { } environment) _environment = TokenEnvironment.Find(environment).Id;
            foreach (var (key, value) in saved?.Emails ?? []) _emails[key] = value;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Start with the defaults
        }
    }

    public string Environment
    {
        get { lock (_gate) return _environment; }
    }

    public string EmailFor(string environment)
    {
        lock (_gate) return _emails.GetValueOrDefault(environment, TokenEnvironment.Find(environment).DefaultEmail);
    }

    public void Remember(string environment, string email)
    {
        lock (_gate)
        {
            _environment = environment;
            _emails[environment] = email.Trim();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
                File.WriteAllText(_file, JsonSerializer.Serialize(new Saved(_environment, _emails), Json));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Not being able to remember is not worth stopping for
            }
        }
    }

    /// <summary>
    /// Before this was an extension of its own, the tool was part of Dev Environment, which kept this same file in its data folder (a sibling of
    /// this one). It is copied over once, when there is no file here yet.
    /// </summary>
    private void AdoptEarlier(string dataDirectory)
    {
        try
        {
            if (File.Exists(_file)) return;
            var parent = Path.GetDirectoryName(dataDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var earlier = parent is null ? null : Path.Combine(parent, "dev-environment", "token.json");
            if (earlier is null || !File.Exists(earlier)) return;
            Directory.CreateDirectory(dataDirectory);
            File.Copy(earlier, _file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Start with nothing remembered
        }
    }
}
