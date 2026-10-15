namespace DevEnvironment.Projects.Startup;

public enum PlanState
{
    Idle,
    Running,

    /// <summary>Everything in the startup is ready.</summary>
    Ready,
    Failed,
    Cancelled,
    Stopping,
}

public enum ItemPhase
{
    Waiting,
    Starting,
    Ready,

    /// <summary>It was already running, so there was nothing to do.</summary>
    AlreadyRunning,
    Failed,
}

public sealed record ItemRun(ItemPhase Phase, string? Message);

/// <summary>What the page shows about a startup that was run or is running.</summary>
public sealed record PlanStatus(
    PlanState State,
    string Message,
    int Wave,
    int Waves,
    DateTime StartedAt,
    DateTime? FinishedAt,
    IReadOnlyDictionary<string, ItemRun> Items)
{
    public bool Busy => State is PlanState.Running or PlanState.Stopping;
}

/// <summary>
/// Runs a startup. The steps are grouped into waves: a step that waits for the previous one begins a new wave, a step that does not
/// joins the wave of the step before it. All items of a wave are started together and the next wave begins when every one of them
/// is ready. An item that fails or does not get ready in time ends the startup unless its step says to keep going.
/// Stopping goes through the waves in reverse. The items themselves keep running when this app does.
/// </summary>
public sealed class StartupRunner
{
    private sealed class PlanRun
    {
        public PlanState State;
        public string Message = "";
        public int Wave;
        public int Waves;
        public DateTime StartedAt;
        public DateTime? FinishedAt;
        public readonly Dictionary<string, ItemRun> Items = [];
        public CancellationTokenSource Cts = new();
    }

    private static readonly TimeSpan PollEvery = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan StoppedGrace = TimeSpan.FromSeconds(10);

    private readonly StartupStore _store;
    private readonly RunnableHub _hub;
    private readonly object _gate = new();
    private readonly Dictionary<string, PlanRun> _runs = [];

    public event Action? Changed;

    public StartupRunner(StartupStore store, RunnableHub hub)
    {
        _store = store;
        _hub = hub;
    }

    public PlanStatus? Get(string planId)
    {
        lock (_gate)
        {
            if (!_runs.TryGetValue(planId, out var run)) return null;
            return new PlanStatus(run.State, run.Message, run.Wave, run.Waves, run.StartedAt, run.FinishedAt, new Dictionary<string, ItemRun>(run.Items));
        }
    }

    public bool AnyBusy
    {
        get { lock (_gate) return _runs.Values.Any(r => r.State is PlanState.Running or PlanState.Stopping); }
    }

    /// <summary>The waves of a startup: which steps begin together and which wait for the ones before.</summary>
    public static List<List<StartupStep>> Waves(StartupPlan plan)
    {
        var waves = new List<List<StartupStep>>();
        foreach (var step in plan.Steps.Where(s => s.Items.Length > 0))
        {
            if (waves.Count == 0 || step.WaitForPrevious) waves.Add([step]);
            else waves[^1].Add(step);
        }
        return waves;
    }

    public void Start(string planId)
    {
        if (_store.Get(planId) is not { } plan) return;

        PlanRun run;
        lock (_gate)
        {
            if (_runs.TryGetValue(planId, out var existing) && existing.State is PlanState.Running or PlanState.Stopping) return;
            run = new PlanRun { State = PlanState.Running, Message = "Starting", StartedAt = DateTime.UtcNow };
            foreach (var item in plan.Steps.SelectMany(s => s.Items).Distinct())
                run.Items[item] = new ItemRun(ItemPhase.Waiting, null);
            _runs[planId] = run;
        }
        Changed?.Invoke();
        _ = Task.Run(() => RunStart(plan, run));
    }

    /// <summary>Stops waiting. What was already started keeps running.</summary>
    public void Cancel(string planId)
    {
        lock (_gate)
        {
            if (_runs.TryGetValue(planId, out var run) && run.State == PlanState.Running) run.Cts.Cancel();
        }
    }

    /// <summary>Stops everything in the startup, last wave first.</summary>
    public void Stop(string planId)
    {
        if (_store.Get(planId) is not { } plan) return;

        PlanRun run;
        lock (_gate)
        {
            if (_runs.TryGetValue(planId, out var existing))
            {
                if (existing.State == PlanState.Stopping) return;
                if (existing.State == PlanState.Running) existing.Cts.Cancel();
            }
            run = new PlanRun { State = PlanState.Stopping, Message = "Stopping", StartedAt = DateTime.UtcNow };
            foreach (var item in plan.Steps.SelectMany(s => s.Items).Distinct())
                run.Items[item] = new ItemRun(ItemPhase.Waiting, null);
            _runs[planId] = run;
        }
        Changed?.Invoke();
        _ = Task.Run(() => RunStop(plan, run));
    }

    private async Task RunStart(StartupPlan plan, PlanRun run)
    {
        var waves = Waves(plan);
        var ct = run.Cts.Token;
        lock (_gate) run.Waves = waves.Count;
        try
        {
            for (var i = 0; i < waves.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                lock (_gate)
                {
                    run.Wave = i + 1;
                    run.Message = waves.Count == 1 ? "Starting" : $"Starting {i + 1} of {waves.Count}";
                }
                Changed?.Invoke();

                var tasks = waves[i].SelectMany(step => step.Items.Distinct().Select(item => StartItem(run, item, step, ct))).ToList();
                var results = await Task.WhenAll(tasks);

                if (results.FirstOrDefault(r => !r.Ok && !r.Step.ContinueOnFailure) is { Step: not null } failed)
                {
                    Finish(run, PlanState.Failed, $"{_hub.Find(failed.Item)?.Name ?? failed.Item}: {Message(run, failed.Item)}");
                    return;
                }
            }
            Finish(run, PlanState.Ready, $"Ready in {(DateTime.UtcNow - run.StartedAt).TotalSeconds:0}s");
        }
        catch (OperationCanceledException)
        {
            Finish(run, PlanState.Cancelled, "Cancelled");
        }
        catch (Exception ex)
        {
            Finish(run, PlanState.Failed, ex.Message);
        }
    }

    private async Task<(StartupStep Step, string Item, bool Ok)> StartItem(PlanRun run, string item, StartupStep step, CancellationToken ct)
    {
        try
        {
            if (_hub.Find(item) is null)
            {
                Set(run, item, ItemPhase.Failed, "Missing");
                return (step, item, false);
            }

            var state = _hub.State(item);
            if (state == ItemState.Ready)
            {
                Set(run, item, ItemPhase.AlreadyRunning, null);
                return (step, item, true);
            }

            Set(run, item, ItemPhase.Starting, null);
            if (state != ItemState.Starting)
            {
                var started = await _hub.StartAsync(item);
                if (!started.Ok)
                {
                    Set(run, item, ItemPhase.Failed, started.Message);
                    return (step, item, false);
                }
            }

            var timeout = TimeSpan.FromSeconds(Math.Max(5, step.TimeoutSeconds));
            var began = DateTime.UtcNow;
            DateTime? stoppedSince = null;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                switch (_hub.State(item))
                {
                    case ItemState.Ready:
                        Set(run, item, ItemPhase.Ready, null);
                        return (step, item, true);
                    case ItemState.Failed:
                        Set(run, item, ItemPhase.Failed, "Exited");
                        return (step, item, false);
                    case ItemState.Stopped:
                        stoppedSince ??= DateTime.UtcNow;
                        if (DateTime.UtcNow - stoppedSince > StoppedGrace)
                        {
                            Set(run, item, ItemPhase.Failed, "Stopped");
                            return (step, item, false);
                        }
                        break;
                    default:
                        stoppedSince = null;
                        break;
                }

                if (DateTime.UtcNow - began > timeout)
                {
                    Set(run, item, ItemPhase.Failed, "Timed out");
                    return (step, item, false);
                }
                await Task.Delay(PollEvery, ct);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Set(run, item, ItemPhase.Failed, ex.Message);
            return (step, item, false);
        }
    }

    private async Task RunStop(StartupPlan plan, PlanRun run)
    {
        try
        {
            var waves = Waves(plan);
            lock (_gate) run.Waves = waves.Count;
            for (var i = waves.Count - 1; i >= 0; i--)
            {
                lock (_gate) run.Wave = waves.Count - i;
                var items = waves[i].SelectMany(s => s.Items).Distinct().Where(item => _hub.Find(item) is not null && _hub.State(item) != ItemState.Stopped).ToList();
                await Task.WhenAll(items.Select(item => _hub.StopAsync(item)));
            }
            lock (_gate) _runs.Remove(plan.Id);
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Finish(run, PlanState.Failed, ex.Message);
        }
    }

    private void Set(PlanRun run, string item, ItemPhase phase, string? message)
    {
        lock (_gate) run.Items[item] = new ItemRun(phase, message);
        Changed?.Invoke();
    }

    private string Message(PlanRun run, string item)
    {
        lock (_gate) return run.Items.TryGetValue(item, out var i) ? i.Message ?? "Failed" : "Failed";
    }

    private void Finish(PlanRun run, PlanState state, string message)
    {
        lock (_gate)
        {
            run.State = state;
            run.Message = message;
            run.FinishedAt = DateTime.UtcNow;
        }
        Changed?.Invoke();
    }
}
