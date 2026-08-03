using System;
using System.Collections.Generic;
using System.Linq;
using MEC;
using Mirror;
using PlaytestHarness.Actors;
using PlaytestHarness.Monitors;
using PlaytestHarness.Movement;
using PlaytestHarness.Telemetry;

namespace PlaytestHarness.Core;

/// <summary>
/// MEC coroutine orchestrator. Sequential execution, per-scenario timeout enforcement (kills the
/// scenario coroutine and records FAIL), cleanup barrier between scenarios, results recorded ONLY on
/// completion: RequireException = FAIL, other exception = ERROR, ran to completion = PASS. A scenario
/// whose Supported range excludes the requested level records SKIP without being started. Monitor
/// violations recorded during a scenario FAIL it unless consumed by an exact bounded ExpectViolation slot.
/// </summary>
internal sealed class ScenarioRunner
{
    private readonly PlaytestConfig _config;
    private readonly string _runsDirectory;
    private readonly IMovementProvider _movement;

    private CoroutineHandle _runHandle;
    private IEnumerator<float>? _runEnumerator;
    private CoroutineHandle _childHandle;
    private List<Stack<IEnumerator<float>>>? _activeStacks;

    internal ScenarioRunner(PlaytestConfig config, string runsDirectory, IMovementProvider movement)
    {
        _config = config;
        _runsDirectory = runsDirectory;
        _movement = movement;
    }

    /// <summary>The movement seam for this runner (BotOrdersProvider or absent).</summary>
    public IMovementProvider Movement => _movement;

    public bool IsRunning { get; private set; }

    public string? CurrentScenario { get; private set; }

    public Fidelity? CurrentLevel { get; private set; }

    /// <summary>Digest + summary path of the last completed run (for 'ptest report').</summary>
    public RunSummary? LastSummary { get; private set; }

    /// <summary>Starts a run on the MEC pump. Returns false if a run is already active.</summary>
    public bool TryStartRun(IReadOnlyList<Scenario> scenarios, Fidelity level, Action<RunSummary>? onCompleted = null)
    {
        if (IsRunning)
        {
            return false;
        }

        IsRunning = true;
        CurrentLevel = level;
        _runEnumerator = RunCoroutine(scenarios, level, onCompleted);
        _runHandle = Timing.RunCoroutine(_runEnumerator);
        return true;
    }

    /// <summary>
    /// Aborts the active run (round restart, plugin disable). Kills the scenario + run coroutines,
    /// disposes the scenario enumerator stacks (finally-based cleanup runs), then disposes the run
    /// enumerator so its finally restores PauseIdleMode, closes the log, and resets runner state —
    /// MEC's KillCoroutines never calls Dispose, so this is done explicitly. Returns false when idle.
    /// </summary>
    public bool AbortRun(string reason)
    {
        if (!IsRunning)
        {
            return false;
        }

        EventLog.Line("RUN", $"ABORTED: {reason}", error: true);
        Timing.KillCoroutines(_childHandle);
        Timing.KillCoroutines(_runHandle);
        if (_activeStacks != null)
        {
            DisposeAllStacks(_activeStacks);
            _activeStacks = null;
        }

        try
        {
            _runEnumerator?.Dispose();
        }
        finally
        {
            _runEnumerator = null;
        }

        CleanupOwnedDummies();
        return true;
    }

    /// <summary>Destroys all harness-owned dummies (nickname prefix match). Used between scenarios and by 'ptest cleanup'.</summary>
    public int CleanupOwnedDummies()
    {
        List<ReferenceHub> owned = ReferenceHub.AllHubs
            .Where(hub => hub.IsDummy && (hub.nicknameSync.MyNick?.StartsWith(_config.NicknamePrefix, StringComparison.Ordinal) ?? false))
            .ToList();
        foreach (ReferenceHub hub in owned)
        {
            // Release provider bookkeeping first (needs the hub alive), then destroy.
            _movement.Release(hub);
            NetworkServer.Destroy(hub.gameObject);
        }

        return owned.Count;
    }

    private IEnumerator<float> RunCoroutine(IReadOnlyList<Scenario> scenarios, Fidelity level, Action<RunSummary>? onCompleted)
    {
        DateTime startedUtc = DateTime.UtcNow;
        RunSummary? summary = null;
        EventLog? log = null;
        ActorRegistry? registry = null;
        MonitorHost? monitors = null;
        bool idleModeWasPaused = false;
        bool idleModeChanged = false;
        bool roundWasLocked = false;
        bool roundLockChanged = false;
        bool emittedSubscribed = false;

        // EVERYTHING after IsRunning=true (TryStartRun) sits inside this try so its finally always
        // releases runner state — a setup failure (unwritable runs dir, monitor ctor throw) must
        // not leave the runner permanently active with leaked subscriptions (PT-015).
        try
        {
            string? setupError = null;
            int seed = 0;
            try
            {
                seed = MapGeneration.SeedSynchronizer.Seed;
                summary = new RunSummary(level, seed, startedUtc);
                log = new EventLog(_runsDirectory, level, startedUtc);
                summary.JsonlPath = log.JsonlPath;

                // Actor + monitor infrastructure lives for the whole run: the registry watches owned
                // actors (role watchdog), the monitor host ticks BoundsMonitor/TeleportMonitor.
                registry = new ActorRegistry(_config.NicknamePrefix, log, _movement);
                monitors = new MonitorHost(registry, log, _config.MonitorSampleIntervalSeconds);
                monitors.Add(new BoundsMonitor(registry, log));
                monitors.Add(new TeleportMonitor(registry, log, level, _config.TeleportThresholdMeters));
                monitors.Start();
                log.Emitted += monitors.OnEvent;
                emittedSubscribed = true;

                // Empty-server MEC stall trap: idle mode throttles the pump; keep it paused while a run is active.
                idleModeWasPaused = LabApi.Features.Wrappers.Server.PauseIdleMode;
                LabApi.Features.Wrappers.Server.PauseIdleMode = true;
                idleModeChanged = true;

                // Dummy-only rounds satisfy native end conditions the moment a scenario kills/cleans its
                // actors (all-dead => round restart => run aborted). Lock the round for the run's duration.
                roundWasLocked = LabApi.Features.Wrappers.Round.IsLocked;
                LabApi.Features.Wrappers.Round.IsLocked = true;
                roundLockChanged = true;
            }
            catch (Exception e)
            {
                setupError = $"run setup FAILED before any scenario: {e.GetType().Name}: {e.Message}";
            }

            if (setupError != null || summary == null || log == null || registry == null || monitors == null)
            {
                EventLog.Line("RUN", setupError ?? "run setup FAILED (incomplete state)", error: true);
                yield break;
            }

            log.Emit(new TelemetryEvent("run_start", null, new Dictionary<string, object?>
            {
                ["level"] = FidelityParser.Label(level),
                ["seed"] = seed,
                ["scenarios"] = scenarios.Select(s => s.Name).ToList(),
            }));
            EventLog.Line("RUN", $"start level={FidelityParser.Label(level)} seed={seed} scenarios={scenarios.Count}");
            foreach (Scenario scenario in scenarios)
            {
                double scenarioStart = log.ElapsedSeconds;

                // Scenario metadata (Supported/TimeoutSeconds getters) is scenario code too: an
                // exception here must become an ERROR result, not abort the whole run inside MEC.
                // Supported is checked first so an unsupported level still records SKIP even when
                // other metadata would throw.
                FidelityRange supported = default;
                float timeout = 0f;
                string? metaError = null;
                bool levelSupported = false;
                try
                {
                    supported = scenario.Supported;
                    levelSupported = supported.Contains(level);
                    if (levelSupported)
                    {
                        scenario.SetLevel(level);
                        timeout = scenario.TimeoutSeconds;
                    }
                }
                catch (Exception e)
                {
                    metaError = $"scenario metadata threw {e.GetType().Name}: {e.Message}";
                }

                if (metaError != null)
                {
                    summary.Results.Add(new ScenarioResult(scenario.Name, ScenarioOutcome.Error, 0, metaError));
                    EventLog.Line("RESULT", $"scenario={scenario.Name} outcome=ERROR {metaError}", error: true);
                    log.Emit(new TelemetryEvent("result", metaError, new Dictionary<string, object?>
                    {
                        ["outcome"] = "error",
                    })
                    { Scenario = scenario.Name });
                    continue;
                }

                if (!levelSupported)
                {
                    string skipMsg = $"supported={supported} excludes requested level {FidelityParser.Label(level)}";
                    summary.Results.Add(new ScenarioResult(scenario.Name, ScenarioOutcome.Skip, 0, skipMsg));
                    EventLog.Line("RESULT", $"scenario={scenario.Name} outcome=SKIP {skipMsg}");
                    log.Emit(new TelemetryEvent("result", skipMsg, new Dictionary<string, object?>
                    {
                        ["outcome"] = "skip",
                    })
                    { Scenario = scenario.Name });
                    continue;
                }

                EventLog.Line("SCENARIO", $"start name={scenario.Name} level={FidelityParser.Label(level)} timeout={timeout:0.#}s");
                log.Emit(new TelemetryEvent("scenario_start", null, new Dictionary<string, object?>
                {
                    ["timeout_s"] = timeout,
                })
                { Scenario = scenario.Name });

                CurrentScenario = scenario.Name;
                // The live MonitorHost never crosses into scenario code. Scenarios receive a
                // separate facade whose runtime type exposes only immutable violation snapshots.
                ScenarioContext ctx = new(level, log, scenario.Name, registry, _movement, _config,
                    new ViolationView(monitors));
                ResultBox box = new();
                List<Stack<IEnumerator<float>>> stacks = new();
                _activeStacks = stacks;
                monitors.OnScenarioBoundary();
                // Sequence watermark, not a count: the per-scenario delta is exact regardless of
                // list ordering (equal-timestamp merges cannot misattribute an old violation).
                int violationWatermark = monitors.AllViolations()
                    .Select(v => v.Sequence)
                    .DefaultIfEmpty(0)
                    .Max();
                CoroutineHandle child = Timing.RunCoroutine(ScenarioBody(scenario, ctx, box, stacks));
                _childHandle = child;

                double deadline = log.ElapsedSeconds + timeout;
                while (child.IsRunning && log.ElapsedSeconds < deadline)
                {
                    yield return Timing.WaitForOneFrame;
                }

                double duration = log.ElapsedSeconds - scenarioStart;
                ScenarioResult result;
                if (child.IsRunning)
                {
                    // Timeout: kill the scenario coroutine and record FAIL. Never a hang.
                    // MEC's KillCoroutines does NOT dispose the enumerator, so run the scenario's
                    // finally-based cleanup explicitly by disposing the remaining enumerator stacks.
                    Timing.KillCoroutines(child);
                    DisposeAllStacks(stacks);
                    result = new ScenarioResult(scenario.Name, ScenarioOutcome.Fail, duration,
                        $"timeout after {timeout:0.#}s (coroutine killed)");
                }
                else
                {
                    result = box.ToResult(scenario.Name, duration);
                }

                _activeStacks = null;

                // Final synchronous monitor sample BEFORE evaluating the result and destroying the
                // actors — a snap in the scenario's last frames must not slip between the previous
                // periodic tick and cleanup (PT-006).
                monitors.TickNow();

                // Contract-breaking monitor violations FAIL the scenario they occurred in (plan
                // §Monitors: "visible in telemetry/run summary AND fail the active scenario").
                // ExpectViolation waives only exact monitor+actor+count slots inside their declared
                // time windows (PT-008). Extra, late, or unrelated violations still fail.
                List<Violation> newViolations = monitors.AllViolations()
                    .Where(v => v.Sequence > violationWatermark)
                    .ToList();
                List<Violation> unexpected = newViolations.Where(v => !ctx.TryConsumeExpectedViolation(v)).ToList();
                if (unexpected.Count > 0 && result.Outcome == ScenarioOutcome.Pass)
                {
                    result = new ScenarioResult(scenario.Name, ScenarioOutcome.Fail, duration,
                        $"{unexpected.Count} unexpected monitor violation(s) during scenario ({newViolations.Count} total); last unexpected: {unexpected.Last()}");
                }

                int violationDelta = newViolations.Count;

                summary.Results.Add(result);
                bool bad = result.Outcome is ScenarioOutcome.Fail or ScenarioOutcome.Error;
                EventLog.Line("RESULT",
                    $"scenario={result.Name} outcome={result.Outcome.ToString().ToUpperInvariant()} duration={result.DurationSeconds:0.##}s{(result.Message != null ? " msg=" + result.Message : string.Empty)}",
                    error: bad);
                log.Emit(new TelemetryEvent("result", result.Message, new Dictionary<string, object?>
                {
                    ["outcome"] = result.Outcome.ToString().ToLowerInvariant(),
                    ["duration_s"] = Math.Round(result.DurationSeconds, 3),
                    ["violations"] = violationDelta,
                })
                { Scenario = scenario.Name });

                CurrentScenario = null;

                // Cleanup barrier: no scenario starts with the previous scenario's dummies alive.
                registry.DestroyAll();
                // NetworkServer.Destroy and the provider's removal hooks settle at frame end. Let
                // exact actor cleanup finish before the nickname-prefix safety sweep, avoiding a
                // second destroy request against the same still-registered hubs.
                yield return Timing.WaitForOneFrame;
                int removed = CleanupOwnedDummies();
                if (removed > 0)
                {
                    EventLog.Line("STEP", $"cleanup-barrier removed {removed} leftover dummy player(s) after {scenario.Name}");
                }

                // IdleMode's OnPlayerRemoved hook calls SetIdleMode(true) UNCONDITIONALLY (it
                // ignores PauseIdleMode — decompiled IdleMode.Start). The safety sweep is also
                // frame-delayed, so let its hooks drain before forcing idle off for the cleanup wait.
                yield return Timing.WaitForOneFrame;
                IdleMode.SetIdleMode(false);

                // Native actor teardown can materialize actor-owned exact serials at frame end.
                // Sweep only the provenance-tracked serial set after that delay; ambient pickups that
                // appeared during the scenario are unrelated and must survive.
                yield return Timing.WaitForSeconds(0.5f);
                registry.DestroyTrackedWorldItems();
            }

            foreach (Violation violation in monitors.AllViolations())
            {
                summary.Violations.Add(violation.ToString());
            }

            string summaryPath = summary.Write(log.ElapsedSeconds);
            log.Emit(new TelemetryEvent("run_result", null, new Dictionary<string, object?>
            {
                ["level"] = FidelityParser.Label(level),
                ["passed"] = summary.Passed,
                ["failed"] = summary.Failed,
                ["skipped"] = summary.Skipped,
                ["summary"] = summaryPath,
            }));

            // The final grep line — headless drivers key off this exact shape.
            EventLog.Line("RUN_RESULT",
                $"level={FidelityParser.Label(level)} passed={summary.Passed} failed={summary.Failed} skipped={summary.Skipped} summary={summaryPath}",
                error: summary.Failed > 0);
        }
        finally
        {
            // Null-guarded + per-step try: setup may have failed at ANY point (PT-015); one
            // cleanup failure must not stop the rest, and runner state ALWAYS resets.
            if (idleModeChanged)
            {
                try { LabApi.Features.Wrappers.Server.PauseIdleMode = idleModeWasPaused; } catch { /* best-effort */ }
            }

            if (roundLockChanged)
            {
                try { LabApi.Features.Wrappers.Round.IsLocked = roundWasLocked; } catch { /* best-effort */ }
            }

            if (emittedSubscribed && log != null && monitors != null)
            {
                try { log.Emitted -= monitors.OnEvent; } catch { /* best-effort */ }
            }

            try { monitors?.Stop(); } catch { /* best-effort */ }
            try { registry?.DestroyAll(); } catch { /* best-effort */ }
            try { registry?.Dispose(); } catch { /* best-effort */ }
            try { log?.Dispose(); } catch { /* best-effort */ }
            try { IdleMode.SetIdleMode(false); } catch { /* best-effort */ }
            if (summary != null)
            {
                LastSummary = summary;
            }

            IsRunning = false;
            CurrentScenario = null;
            CurrentLevel = null;
            if (summary != null)
            {
                try { onCompleted?.Invoke(summary); } catch { /* observer's problem */ }
            }
        }
    }

    /// <summary>
    /// Pumps the scenario enumerator inline (manual MoveNext inside try/catch) so RequireException /
    /// arbitrary exceptions surface as FAIL/ERROR instead of dying silently inside MEC's pump.
    /// Verb sub-enumerators registered via ctx.RegisterPending are pumped under the same try/catch:
    /// ONE stack per verb chain, several stacks round-robin CONCURRENTLY (two actors can walk at
    /// once). Stack 0 is the scenario body; it is only pumped when no verb stack is pending, so
    /// 'yield return actor.Verb()' blocks the scenario until that verb (and all verbs registered in
    /// the same frame) completed. PASS is recorded only when the body completes — never early. The
    /// stacks are caller-owned so timeout/abort paths can dispose them (MEC's kill never calls
    /// Dispose); every exit path disposes what remains so try/finally cleanup in scenarios runs.
    /// </summary>
    private static IEnumerator<float> ScenarioBody(Scenario scenario, ScenarioContext ctx, ResultBox box,
        List<Stack<IEnumerator<float>>> stacks)
    {
        Stack<IEnumerator<float>> main = new();
        stacks.Add(main);
        try
        {
            main.Push(scenario.Run(ctx));
        }
        catch (SkipException e)
        {
            box.Set(ScenarioOutcome.Skip, e.Message);
            yield break;
        }
        catch (RequireException e)
        {
            box.Set(ScenarioOutcome.Fail, e.Message);
            yield break;
        }
        catch (Exception e)
        {
            box.Set(ScenarioOutcome.Error, $"{e.GetType().Name}: {e.Message}");
            yield break;
        }

        // Wait values per stack (MEC absolute-time semantics: WaitForSeconds returns LocalTime+t,
        // WaitForOneFrame is -inf). A stack whose wait lies in the future is skipped this frame.
        Dictionary<Stack<IEnumerator<float>>, float> waitUntil = new();
        bool mainCompleted = false;

        while (stacks.Count > 0)
        {
            bool anyVerbStacks = stacks.Count > 1;
            bool pumpedAny = false;
            // Iterate over a snapshot: pumping can add new stacks (verbs registered mid-frame).
            foreach (Stack<IEnumerator<float>> stack in stacks.ToArray())
            {
                // While verb stacks exist, the scenario body (stack 0) is parked — its most recent
                // 'yield return verb()' means "wait for the verbs".
                if (anyVerbStacks && ReferenceEquals(stack, main))
                {
                    continue;
                }

                if (waitUntil.TryGetValue(stack, out float at) && at > Timing.LocalTime)
                {
                    continue;
                }

                pumpedAny = true;
                bool moved;
                float yielded = 0f;
                try
                {
                    moved = stack.Peek().MoveNext();
                    if (moved)
                    {
                        yielded = stack.Peek().Current;
                    }
                }
                catch (SkipException e)
                {
                    DisposeAllStacks(stacks);
                    box.Set(ScenarioOutcome.Skip, e.Message);
                    yield break;
                }
                catch (RequireException e)
                {
                    DisposeAllStacks(stacks);
                    box.Set(ScenarioOutcome.Fail, e.Message);
                    yield break;
                }
                catch (Exception e)
                {
                    DisposeAllStacks(stacks);
                    EventLog.Line("STEP", $"scenario ERROR stack:\n{e}", error: true);
                    box.Set(ScenarioOutcome.Error, $"{e.GetType().Name}: {e.Message}");
                    yield break;
                }

                if (!moved)
                {
                    stack.Pop().Dispose();
                    waitUntil.Remove(stack);

                    // A verb registered as a SIDE EFFECT of this final MoveNext (e.g. a scenario
                    // ending with 'actor.Soak(30f);' and no trailing yield) must still RUN — it is
                    // part of the scenario's declared behavior, and discarding it would record a
                    // PASS for work that never executed (PT-003).
                    List<IEnumerator<float>>? lastPending = ctx.TakePendingWaits();
                    if (lastPending != null)
                    {
                        foreach (IEnumerator<float> verb in lastPending)
                        {
                            Stack<IEnumerator<float>> verbStack = new();
                            verbStack.Push(verb);
                            stacks.Add(verbStack);
                        }
                    }

                    if (stack.Count == 0)
                    {
                        if (ReferenceEquals(stack, main))
                        {
                            // Scenario body completed. Verb stacks registered up to and including
                            // the final MoveNext keep running; PASS is recorded only when they all
                            // drained (or one of them failed — handled by the catches above).
                            mainCompleted = true;
                            stacks.Remove(stack);
                            if (stacks.Count == 0)
                            {
                                box.Set(ScenarioOutcome.Pass, null);
                                yield break;
                            }

                            continue;
                        }

                        stacks.Remove(stack);
                        if (mainCompleted && stacks.Count == 0)
                        {
                            // Last outstanding verb drained after body completion => PASS.
                            box.Set(ScenarioOutcome.Pass, null);
                            yield break;
                        }
                    }

                    continue;
                }

                // Verbs registered during this MoveNext become their own concurrent stacks (the
                // registering stack ALSO keeps its own yielded wait — a 'ctx.Wait(5)' yielded
                // right after registering a verb still holds its 5 s once the verbs drain).
                List<IEnumerator<float>>? pending = ctx.TakePendingWaits();
                if (pending != null)
                {
                    if (pending.Count == 1 && !ReferenceEquals(stack, main))
                    {
                        // A verb awaiting a sub-verb: nest inline on the same stack (sequential).
                        stack.Push(pending[0]);
                        waitUntil.Remove(stack);
                        continue;
                    }

                    foreach (IEnumerator<float> verb in pending)
                    {
                        Stack<IEnumerator<float>> verbStack = new();
                        verbStack.Push(verb);
                        stacks.Add(verbStack);
                    }
                }

                waitUntil[stack] = yielded;
            }

            if (!pumpedAny)
            {
                // Everything is waiting on time — idle one frame.
                yield return Timing.WaitForOneFrame;
                continue;
            }

            yield return Timing.WaitForOneFrame;
        }

        // stacks emptied without the main stack finishing (defensive; should be unreachable).
        box.Set(ScenarioOutcome.Error, "scenario pump ended without a result");
    }

    /// <summary>
    /// Disposes every enumerator on every stack, innermost first, so try/finally blocks in scenario
    /// code run even when the coroutine was killed (timeout/abort) or a nested wait threw.
    /// Dispose-time exceptions are logged and swallowed — cleanup of one frame never blocks the rest.
    /// </summary>
    private static void DisposeAllStacks(List<Stack<IEnumerator<float>>> stacks)
    {
        foreach (Stack<IEnumerator<float>> stack in stacks)
        {
            while (stack.Count > 0)
            {
                try
                {
                    stack.Pop().Dispose();
                }
                catch (Exception e)
                {
                    EventLog.Line("STEP", $"scenario cleanup (Dispose) threw {e.GetType().Name}: {e.Message}", error: true);
                }
            }
        }

        stacks.Clear();
    }

    private sealed class ResultBox
    {
        private ScenarioOutcome? _outcome;
        private string? _message;

        public void Set(ScenarioOutcome outcome, string? message)
        {
            _outcome = outcome;
            _message = message;
        }

        public ScenarioResult ToResult(string name, double duration)
        {
            // A finished coroutine that never reported (killed externally / MEC anomaly) is an ERROR,
            // never an implicit PASS.
            return new ScenarioResult(name, _outcome ?? ScenarioOutcome.Error, duration,
                _outcome == null ? "scenario coroutine ended without reporting a result" : _message);
        }
    }
}
