using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DevEnvironment.Shared;

namespace DevEnvironment.Logging;

public sealed record LogLine(long Seq, string Text);

/// <summary>Something whose log can be shown in the log viewer. <see cref="Path"/> is the file that is followed.</summary>
public sealed record LogSource(string Id, string Title, string Group, string Path);

public interface ILogSourceProvider
{
    IReadOnlyList<LogSource> Sources();

    event Action? Changed;
}

/// <summary>Every log the viewer can show, from all providers.</summary>
public sealed class LogSources
{
    private readonly IReadOnlyList<ILogSourceProvider> _providers;

    public LogSources(IEnumerable<ILogSourceProvider> providers)
    {
        _providers = [.. providers];
        foreach (var provider in _providers) provider.Changed += () => Changed?.Invoke();
    }

    public event Action? Changed;

    public IReadOnlyList<LogSource> All => _providers.SelectMany(p => p.Sources()).ToList();

    public LogSource? Find(string? id) => id is null ? null : All.FirstOrDefault(s => s.Id == id);
}

/// <summary>Follows one log file: keeps the last lines in memory and notices new ones.</summary>
public sealed partial class LogFeed
{
    private const int MaxLines = 5000;
    private const int InitialBytes = 256 * 1024;

    private readonly string _path;
    private readonly object _gate = new();
    private readonly List<LogLine> _lines = [];
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private string _partial = "";
    private long _position;
    private long _seq;
    private bool _started;

    public LogFeed(string path) => _path = path;

    /// <summary>Raised when new lines were read (from the hub's timer thread).</summary>
    public event Action? Changed;

    public IReadOnlyList<LogLine> Snapshot()
    {
        lock (_gate) return [.. _lines];
    }

    public long LastSeq
    {
        get { lock (_gate) return _seq; }
    }

    /// <summary>Reads what was added to the file since the last call.</summary>
    internal void Poll()
    {
        bool added;
        try
        {
            added = Read();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return; // the file is busy or gone for a moment; try again next tick
        }
        if (added) Changed?.Invoke();
    }

    private bool Read()
    {
        if (!File.Exists(_path)) return false;
        using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = stream.Length;

        long from;
        var restarted = false;
        if (!_started)
        {
            from = Math.Max(0, length - InitialBytes);
            _started = true;
        }
        else if (length < _position)
        {
            from = 0; // the file was replaced or emptied
            restarted = true;
        }
        else if (length == _position)
        {
            return false;
        }
        else
        {
            from = _position;
        }

        stream.Seek(from, SeekOrigin.Begin);
        var buffer = new byte[length - from];
        var read = stream.Read(buffer, 0, buffer.Length);
        _position = from + read;

        var chars = new char[_decoder.GetCharCount(buffer, 0, read)];
        _decoder.GetChars(buffer, 0, read, chars, 0);
        var text = _partial + new string(chars);

        var parts = text.Split('\n');
        _partial = parts[^1];
        var skipFirst = from > 0 && !restarted && _lines.Count == 0; // started in the middle of a line

        lock (_gate)
        {
            if (restarted) _lines.Add(new LogLine(++_seq, "--- log restarted ---"));
            for (var i = skipFirst ? 1 : 0; i < parts.Length - 1; i++)
                _lines.Add(new LogLine(++_seq, Ansi().Replace(parts[i].TrimEnd('\r'), "")));
            if (_lines.Count > MaxLines) _lines.RemoveRange(0, _lines.Count - MaxLines + 1000);
        }
        return parts.Length > 1 || restarted;
    }

    [GeneratedRegex("\u001b\\[[0-9;?]*[A-Za-z]")]
    private static partial Regex Ansi();
}

/// <summary>Shares one <see cref="LogFeed"/> per file between every pane that shows it, and polls them while any pane is open.</summary>
public sealed class LogHub : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (LogFeed Feed, int Users)> _feeds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _timer;

    public LogHub() => _timer = new Timer(_ => Poll(), null, TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500));

    public LogFeed Acquire(string path)
    {
        lock (_gate)
        {
            var entry = _feeds.TryGetValue(path, out var existing) ? existing : (new LogFeed(path), 0);
            _feeds[path] = (entry.Item1, entry.Item2 + 1);
            entry.Item1.Poll();
            return entry.Item1;
        }
    }

    public void Release(string path)
    {
        lock (_gate)
        {
            if (!_feeds.TryGetValue(path, out var entry)) return;
            if (entry.Users <= 1) _feeds.Remove(path);
            else _feeds[path] = (entry.Feed, entry.Users - 1);
        }
    }

    public void Dispose() => _timer.Dispose();

    private void Poll()
    {
        List<LogFeed> feeds;
        lock (_gate) feeds = _feeds.Values.Select(v => v.Feed).ToList();
        foreach (var feed in feeds) feed.Poll();
    }
}

public enum LogLayout
{
    Single,
    Cols2,
    Rows2,
    Grid2x2,
    Cols3,
    Rows3,
    Cols4,
    Rows4,
}

/// <summary>Which layout the log viewer uses and which log is in each pane. Saved in the extension's data folder (logviewer.json).</summary>
public sealed class LogViewerState
{
    public const int MaxPanes = 4;
    private static readonly string FilePath = DevEnvironmentPaths.FilePath("logviewer.json");

    private sealed record Saved(LogLayout Layout, string?[] Panes, bool Wrap = false);

    private readonly object _gate = new();
    private LogLayout _layout = LogLayout.Cols2;
    private bool _wrap;
    private readonly string?[] _panes = new string?[MaxPanes];

    public event Action? Changed;

    public LogViewerState()
    {
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<Saved>(File.ReadAllText(FilePath)) is { } saved)
            {
                _layout = saved.Layout;
                _wrap = saved.Wrap;
                for (var i = 0; i < Math.Min(MaxPanes, saved.Panes.Length); i++) _panes[i] = saved.Panes[i];
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Start with the default layout
        }
    }

    public LogLayout Layout
    {
        get { lock (_gate) return _layout; }
    }

    /// <summary>True wraps long lines in every pane; false keeps one line per row and scrolls sideways.</summary>
    public bool Wrap
    {
        get { lock (_gate) return _wrap; }
    }

    public void SetWrap(bool wrap)
    {
        lock (_gate) _wrap = wrap;
        SaveAndNotify();
    }

    public static int PaneCount(LogLayout layout) => layout switch
    {
        LogLayout.Single => 1,
        LogLayout.Cols2 or LogLayout.Rows2 => 2,
        LogLayout.Cols3 or LogLayout.Rows3 => 3,
        _ => 4,
    };

    public string? Pane(int index)
    {
        lock (_gate) return index is >= 0 and < MaxPanes ? _panes[index] : null;
    }

    public void SetLayout(LogLayout layout)
    {
        lock (_gate) _layout = layout;
        SaveAndNotify();
    }

    public void SetPane(int index, string? sourceId)
    {
        if (index is < 0 or >= MaxPanes) return;
        lock (_gate) _panes[index] = string.IsNullOrEmpty(sourceId) ? null : sourceId;
        SaveAndNotify();
    }

    /// <summary>Shows these logs, one per pane, in a layout that fits their number.</summary>
    public void ShowMany(IReadOnlyList<string> sourceIds)
    {
        if (sourceIds.Count == 0) return;
        lock (_gate)
        {
            _layout = sourceIds.Count switch { 1 => LogLayout.Single, 2 => LogLayout.Cols2, 3 => LogLayout.Cols3, _ => LogLayout.Grid2x2 };
            for (var i = 0; i < MaxPanes; i++) _panes[i] = i < sourceIds.Count ? sourceIds[i] : null;
        }
        SaveAndNotify();
    }

    /// <summary>Shows one log: in a pane that already has it, else the first empty pane, else the last pane.</summary>
    public void Show(string sourceId)
    {
        lock (_gate)
        {
            var count = PaneCount(_layout);
            var visible = _panes.Take(count).ToList();
            if (visible.Contains(sourceId)) return;
            var empty = visible.IndexOf(null);
            _panes[empty >= 0 ? empty : count - 1] = sourceId;
        }
        SaveAndNotify();
    }

    private void SaveAndNotify()
    {
        try
        {
            Saved snapshot;
            lock (_gate) snapshot = new Saved(_layout, [.. _panes], _wrap);
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(snapshot));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cosmetic; the layout still applies until the app restarts
        }
        Changed?.Invoke();
    }
}
