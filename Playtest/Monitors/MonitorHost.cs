using System;
using System.Collections.Generic;
using System.Linq;
using MEC;
using PlaytestHarness.Actors;
using PlaytestHarness.Telemetry;

namespace PlaytestHarness.Monitors;

/// <summary>
/// Owns the run's monitors: ticks them on a MEC coroutine (~4 Hz) for the duration of the run,
/// fans telemetry events out to them, and aggregates violations (incl. the registry's role
/// watchdog) into the run summary. A monitor that THROWS is itself a violation (PT-018): the
/// oracle is not running correctly, so the scenario it was guarding must not silently PASS —
/// one MonitorFailure violation is recorded per monitor per scenario-visible failure burst.
/// Scenario code never receives this object: it gets a separate <see cref="ViolationView"/> facade.
/// The host type and every lifecycle/control member are harness-internal (PT-001).
/// </summary>
internal sealed class MonitorHost
{
    private readonly List<IRoundMonitor> _monitors = new();
    private readonly List<Violation> _failureViolations = new();
    private readonly HashSet<string> _failedMonitors = new();
    private readonly ActorRegistry _registry;
    private readonly EventLog _log;
    private readonly float _sampleIntervalSeconds;
    private CoroutineHandle _tickHandle;

    internal MonitorHost(ActorRegistry registry, EventLog log, float sampleIntervalSeconds = 0.25f)
    {
        _registry = registry;
        _log = log;
        _sampleIntervalSeconds = Math.Max(0.05f, sampleIntervalSeconds);
    }

    internal void Add(IRoundMonitor monitor) => _monitors.Add(monitor);

    internal void Start()
    {
        _tickHandle = Timing.RunCoroutine(TickLoop());
    }

    internal void OnEvent(TelemetryEvent e)
    {
        foreach (IRoundMonitor monitor in _monitors)
        {
            try
            {
                monitor.OnEvent(e);
            }
            catch (Exception ex)
            {
                RecordMonitorFailure(monitor, "OnEvent", ex);
            }
        }
    }

    /// <summary>All violations across monitors + the registry role watchdog + monitor failures.</summary>
    internal List<Violation> AllViolations()
    {
        List<Violation> all = _monitors.SelectMany(m => m.Violations).ToList();
        all.AddRange(_registry.WatchdogViolations);
        all.AddRange(_failureViolations);
        // Sequence tie-break: equal timestamps must never interleave differently between two
        // snapshots — the runner's per-scenario delta depends on a stable prefix.
        return all.OrderBy(v => v.AtSeconds).ThenBy(v => v.Sequence).ToList();
    }

    /// <summary>Total violation count (cheap snapshot for the runner's per-scenario delta check).</summary>
    internal int ViolationCount =>
        _monitors.Sum(m => m.Violations.Count) + _registry.WatchdogViolations.Count + _failureViolations.Count;

    /// <summary>
    /// Synchronous tick — the runner calls this right after a scenario body completes, BEFORE
    /// evaluating the result and destroying actors, so a snap in the final frames cannot slip
    /// between the last periodic sample and cleanup (PT-006 final-sample blind spot).
    /// </summary>
    internal void TickNow() => TickAll();

    /// <summary>
    /// Re-arms per-monitor failure dedup at scenario boundaries so a persistently-broken monitor
    /// fails EVERY scenario it was supposed to guard, not only the first one.
    /// </summary>
    internal void OnScenarioBoundary() => _failedMonitors.Clear();

    private IEnumerator<float> TickLoop()
    {
        while (true)
        {
            TickAll();
            yield return Timing.WaitForSeconds(_sampleIntervalSeconds);
        }
    }

    private void TickAll()
    {
        foreach (IRoundMonitor monitor in _monitors)
        {
            try
            {
                monitor.Tick();
            }
            catch (Exception ex)
            {
                RecordMonitorFailure(monitor, "Tick", ex);
            }
        }
    }

    /// <summary>
    /// A throwing monitor = a broken oracle = a violation (PT-018). Deduplicated per monitor per
    /// scenario (OnScenarioBoundary re-arms) so a crash-looping Tick at 4 Hz does not flood the log
    /// while still failing every affected scenario.
    /// </summary>
    private void RecordMonitorFailure(IRoundMonitor monitor, string phase, Exception ex)
    {
        EventLog.Line("MONITOR", $"{monitor.Name}.{phase} threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}", error: true);
        if (!_failedMonitors.Add(monitor.Name))
        {
            return;
        }

        Violation violation = new("MonitorFailure", monitor.Name,
            $"{monitor.Name}.{phase} threw {ex.GetType().Name}: {ex.Message} — the oracle is NOT running correctly; results it should have guarded are unreliable",
            _log.ElapsedSeconds);
        _failureViolations.Add(violation);
        EventLog.Line("VIOLATION", violation.ToString(), error: true);
        _log.Emit(new TelemetryEvent("violation", violation.Detail, new Dictionary<string, object?>
        {
            ["monitor"] = "MonitorFailure",
            ["actor"] = monitor.Name,
        }));
    }

    /// <summary>Stops monitor ticking and releases monitor hooks. Runner-owned; not an interface capability.</summary>
    internal void Stop()
    {
        Timing.KillCoroutines(_tickHandle);
        foreach (IRoundMonitor monitor in _monitors)
        {
            (monitor as IDisposable)?.Dispose();
        }
    }
}
