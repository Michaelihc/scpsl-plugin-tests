using System;
using System.Collections.Generic;
using System.Linq;
using MEC;
using PlayerRoles;
using PlaytestHarness.Actors;
using Respawning;
using Respawning.Waves;
using PlaytestHarness.Telemetry;

namespace PlaytestHarness.Core;

/// <summary>
/// What a scenario touches while it runs: fidelity, assertions, telemetry, yield-based wait
/// helpers, the actor API (SpawnActor + verbs on <see cref="Actor"/>), the pre-arranged-state
/// scope (<see cref="Arrange"/>), and read access to the run's monitors.
/// </summary>
/// <remarks>
/// Wait design note: <see cref="WaitUntil"/> and all actor verbs do NOT use MEC's
/// Timing.WaitUntilDone sub-coroutines. MEC catches exceptions thrown inside a sub-coroutine at its
/// own pump loop, which would swallow a RequireException and let the scenario continue as if the
/// wait succeeded. Instead each verb registers a pending sub-enumerator that the runner pumps
/// inline inside its own try/catch, so Require failures and timeouts inside verbs surface as
/// proper FAILs. Registering SEVERAL verbs before one yield runs them CONCURRENTLY (round-robin
/// pump): <c>a.WalkTo(x); b.WalkTo(y); yield return ctx.WaitOneFrame();</c>.
/// </remarks>
public sealed class ScenarioContext
{
    private readonly EventLog _log;
    private readonly string _scenarioName;
    private readonly Queue<IEnumerator<float>> _pendingWaits = new();
    private readonly List<ExpectedViolation> _expectedViolations = new();
    private int _arrangementDepth;

    private sealed class ExpectedViolation
    {
        public string Monitor = string.Empty;
        public string Actor = string.Empty;
        public double NotBefore;
        public double NotAfter;
        public int Remaining;

        public bool TryConsume(Monitors.Violation violation)
        {
            if (Remaining <= 0 || violation.AtSeconds < NotBefore || violation.AtSeconds > NotAfter ||
                !string.Equals(violation.Monitor, Monitor, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(violation.Actor, Actor, StringComparison.Ordinal))
            {
                return false;
            }

            Remaining--;
            return true;
        }
    }

    internal ScenarioContext(Fidelity fidelity, EventLog log, string scenarioName, ActorRegistry registry,
        Movement.IMovementProvider movement, PlaytestConfig config, Monitors.IViolationSource monitors)
    {
        Fidelity = fidelity;
        _log = log;
        _scenarioName = scenarioName;
        Registry = registry;
        Movement = movement;
        Config = config;
        Monitors = monitors;
    }

    /// <summary>The tier this run executes at.</summary>
    public Fidelity Fidelity { get; }

    /// <summary>Runner/fulfiller-only actor bookkeeping. Never exposed to external scenarios.</summary>
    internal ActorRegistry Registry { get; }

    /// <summary>Runner/fulfiller-only movement seam. Scenario code uses Actor movement verbs.</summary>
    internal Movement.IMovementProvider Movement { get; }

    /// <summary>Fulfiller/monitor configuration. Scenario bodies use batteries-included verbs.</summary>
    internal PlaytestConfig Config { get; }

    /// <summary>
    /// READ-ONLY access to immutable violation snapshots (monitor self-tests assert on these).
    /// The runtime object is a separate capability-limited facade, not the live monitor host; it
    /// cannot be downcast to lifecycle/control APIs (PT-001).
    /// </summary>
    public Monitors.IViolationSource Monitors { get; }

    /// <summary>True when at least one bounded violation waiver was declared.</summary>
    public bool ViolationsExpected => _expectedViolations.Count > 0;

    /// <summary>True while inside an <see cref="Arrange"/>/<see cref="BeginArrangement"/> scope.</summary>
    public bool ArrangementActive => _arrangementDepth > 0;

    /// <summary>Seconds since the run started (telemetry clock).</summary>
    public double ElapsedSeconds => _log.ElapsedSeconds;

    /// <summary>
    /// Assertion. On failure throws <see cref="RequireException"/>, which the runner records as FAIL.
    /// There is no Pass() — a scenario passes by running to completion.
    /// </summary>
    public void Require(bool condition, string message)
    {
        Emit(new TelemetryEvent("require", message, new Dictionary<string, object?> { ["ok"] = condition }));
        if (!condition)
        {
            throw new RequireException(message);
        }
    }

    /// <summary>Informational step line — mirrored to the server log ([Playtest] STEP) and the JSONL stream.</summary>
    public void Info(string message)
    {
        EventLog.Line("STEP", $"scenario={_scenarioName} {message}");
        Emit(new TelemetryEvent("step", message));
    }

    /// <summary>Emits a raw telemetry event to the JSONL stream (scenario name is stamped automatically).</summary>
    public void Emit(TelemetryEvent evt)
    {
        evt.Scenario = _scenarioName;
        _log.Emit(evt);
    }

    /// <summary>
    /// Records the run as SKIP with an explicit reason (e.g. movement provider absent). Never a
    /// crash, never a silent pass.
    /// </summary>
    public void Skip(string reason)
    {
        Emit(new TelemetryEvent("skip", reason));
        throw new SkipException(reason);
    }

    /// <summary>
    /// Declares a bounded waiver for deliberate monitor testing. A waiver matches ONLY the named
    /// monitor, exact actor label, requested count, and time window beginning now. Every additional,
    /// late, wrong-actor, or wrong-monitor violation remains unexpected and fails the scenario.
    /// </summary>
    public void ExpectViolation(string reason, string monitor, string actor, int count = 1, float withinSeconds = 10f)
    {
        if (string.IsNullOrWhiteSpace(monitor) || string.IsNullOrWhiteSpace(actor))
        {
            throw new ArgumentException("ExpectViolation requires an exact monitor name and actor label.");
        }

        if (count <= 0 || withinSeconds <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "ExpectViolation count and time window must be positive.");
        }

        ExpectedViolation expected = new()
        {
            Monitor = monitor,
            Actor = actor,
            NotBefore = ElapsedSeconds,
            NotAfter = ElapsedSeconds + withinSeconds,
            Remaining = count,
        };
        _expectedViolations.Add(expected);

        Emit(new TelemetryEvent("expect_violation", reason, new Dictionary<string, object?>
        {
            ["monitor"] = monitor,
            ["actor"] = actor,
            ["count"] = count,
            ["within_s"] = withinSeconds,
        }));
        EventLog.Line("STEP",
            $"scenario={_scenarioName} EXPECTS monitor={monitor} actor={actor} count={count} within={withinSeconds:0.##}s: {reason}");
    }

    /// <summary>Consumes one exact expected-violation slot. Runner-owned.</summary>
    internal bool TryConsumeExpectedViolation(Monitors.Violation violation)
    {
        foreach (ExpectedViolation expected in _expectedViolations)
        {
            if (expected.TryConsume(violation))
            {
                return true;
            }
        }

        return false;
    }

    // ---- pre-arranged state (the second Standard shortcut) --------------------------------------

    /// <summary>
    /// The PRE-ARRANGED-STATE shortcut (plan tier table): start mid-objective, pre-grant gear.
    /// Allowed at Quick (trivially) and Standard (telemetry-logged); FORBIDDEN at EndToEnd — e2e
    /// acquires everything through real play. Arrangement-gated APIs (GiveItem at Standard) throw
    /// outside a scope.
    /// </summary>
    public void Arrange(string reason, Action arrangement)
    {
        using (BeginArrangement(reason))
        {
            arrangement();
        }
    }

    /// <summary>Scope form of <see cref="Arrange"/> for arrangements that need yields inside.</summary>
    public IDisposable BeginArrangement(string reason)
    {
        if (Fidelity == Fidelity.EndToEnd)
        {
            throw new InvalidOperationException(
                $"Pre-arranged state ('{reason}') is forbidden at EndToEnd — the full path acquires everything through real play (plan tier table).");
        }

        _arrangementDepth++;
        Emit(new TelemetryEvent("arrange", reason, new Dictionary<string, object?>
        {
            ["level"] = FidelityParser.Label(Fidelity),
        }));
        EventLog.Line("STEP", $"scenario={_scenarioName} ARRANGE ({FidelityParser.Label(Fidelity)}): {reason}");
        return new ArrangementScope(this);
    }

    private sealed class ArrangementScope : IDisposable
    {
        private ScenarioContext? _ctx;

        public ArrangementScope(ScenarioContext ctx) => _ctx = ctx;

        public void Dispose()
        {
            if (_ctx != null)
            {
                _ctx._arrangementDepth--;
                _ctx = null;
            }
        }
    }

    // ---- round ----------------------------------------------------------------------------------

    /// <summary>
    /// Starts the native round if needed and waits until the round is actually active. The runner's
    /// temporary lock remains owned/restored by the runner; scenarios never write Round.IsLocked.
    /// </summary>
    public float EnsureRoundStarted(float timeoutSeconds = 30f)
        => RegisterPending(RoundFulfiller.EnsureStarted(this, timeoutSeconds));

    /// <summary>
    /// Test-isolation scope that force-pauses native reinforcement wave timers while preserving and
    /// restoring each timer's prior pause state. Direct feature-owned spawns still run normally.
    /// This prevents an ambient native wave from stealing exact Spectator candidate pools mid-slice.
    /// </summary>
    public IDisposable PauseNativeWaves(string reason)
    {
        if (Fidelity == Fidelity.EndToEnd)
        {
            throw new InvalidOperationException(
                $"Native-wave pause ('{reason}') is forbidden at EndToEnd — e2e keeps ambient round systems live.");
        }

        List<(TimeBasedWave Wave, bool WasPaused)> states = WaveManager.Waves
            .OfType<TimeBasedWave>()
            .Select(wave => (wave, wave.Timer.IsForcefullyPaused))
            .ToList();
        foreach ((TimeBasedWave wave, _) in states)
        {
            wave.Timer.IsForcefullyPaused = true;
        }

        Emit(new TelemetryEvent("native_waves_paused", reason, new Dictionary<string, object?>
        {
            ["count"] = states.Count,
        }));
        EventLog.Line("STEP", $"scenario={_scenarioName} native waves PAUSED count={states.Count}: {reason}");
        return new NativeWavePauseScope(this, reason, states);
    }

    private sealed class NativeWavePauseScope : IDisposable
    {
        private ScenarioContext? _ctx;
        private readonly string _reason;
        private readonly List<(TimeBasedWave Wave, bool WasPaused)> _states;

        internal NativeWavePauseScope(
            ScenarioContext ctx,
            string reason,
            List<(TimeBasedWave Wave, bool WasPaused)> states)
        {
            _ctx = ctx;
            _reason = reason;
            _states = states;
        }

        public void Dispose()
        {
            if (_ctx == null)
            {
                return;
            }

            foreach ((TimeBasedWave wave, bool wasPaused) in _states)
            {
                wave.Timer.IsForcefullyPaused = wasPaused;
            }

            _ctx.Emit(new TelemetryEvent("native_waves_restored", _reason, new Dictionary<string, object?>
            {
                ["count"] = _states.Count,
            }));
            EventLog.Line("STEP", $"scenario={_ctx._scenarioName} native waves RESTORED count={_states.Count}: {_reason}");
            _ctx = null;
        }
    }

    // ---- actors ---------------------------------------------------------------------------------

    /// <summary>
    /// Spawns a harness-owned actor dummy. Placement/tier semantics live in the SpawnSpec + verbs.
    /// Usage: <c>Actor a = ctx.SpawnActor("walker", RoleTypeId.ClassD, SpawnSpec.Native());
    /// yield return a.WaitReady();</c> — WaitReady completes role assignment, placement and settle.
    /// Set <paramref name="useMovementProvider"/> false for a plain RA dummy when a test must drive
    /// <c>FpcMotor.ReceivedPosition</c> directly through the native motor.
    /// </summary>
    public Actor SpawnActor(
        string label,
        RoleTypeId role,
        SpawnSpec spec,
        bool useMovementProvider = true)
    {
        spec.EnforceTier(Fidelity);
        return MovementFulfiller.CreateActor(this, label, role, spec, useMovementProvider);
    }

    /// <summary>
    /// Creates a non-spatial Spectator candidate for an exact feature-owned spawn pool. WaitReady
    /// verifies candidate readiness without a fake grounded check; spatial verbs become meaningful
    /// after the feature transitions the actor to a live FPC role.
    /// </summary>
    public Actor SpawnCandidate(string label) => SpawnActor(label, RoleTypeId.Spectator, SpawnSpec.Candidate());

    /// <summary>
    /// Registers an exact scenario-created world pickup for automatic cleanup on completion, failure,
    /// timeout, run abort, round restart, or plugin disable. Unrelated world pickups are never touched.
    /// Native Actor.DropHeldItem calls register automatically; use this for feature-created output.
    /// </summary>
    public void TrackWorldItem(WorldItem item, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("World-item tracking reason is required.", nameof(reason));
        }

        Registry.TrackWorldItem(item, reason);
    }

    /// <summary>
    /// Invokes a production feature entry point against an exact actor pool while installing ordered,
    /// actor-scoped, predicate-checked transition expectations. The callback receives read-only Player
    /// wrappers solely so it can call the feature's normal pool-aware API; it runs synchronously and the
    /// asynchronous allowances expire after spec.WithinSeconds. EndToEnd forbids this teleport-capable
    /// scope. Monitors remain live and reject every wrong, extra, late, or unrelated transition.
    /// </summary>
    public T TriggerFeature<T>(
        string reason,
        IReadOnlyList<Actor> actors,
        FeatureTransitionSpec spec,
        Func<IReadOnlyList<LabApi.Features.Wrappers.Player>, T> trigger)
    {
        if (Fidelity == Fidelity.EndToEnd)
        {
            throw new InvalidOperationException(
                $"Feature transition '{reason}' is forbidden at EndToEnd — e2e permits native spawns and zero teleports.");
        }

        if (actors == null || actors.Count == 0)
        {
            throw new ArgumentException("TriggerFeature requires at least one actor.", nameof(actors));
        }

        List<LabApi.Features.Wrappers.Player> players = actors.Select(actor =>
            LabApi.Features.Wrappers.Player.Get(actor.Hub)
            ?? throw new RequireException($"feature transition '{reason}': actor {actor.Label} has no Player wrapper")).ToList();

        AuthorizeFeatureTransitions(reason, actors, spec);
        return trigger(players.AsReadOnly());
    }

    /// <summary>
    /// Installs the same ordered, actor-scoped transition expectations as TriggerFeature without
    /// synchronously invoking a feature. Use this when real gameplay starts an asynchronous production
    /// branch later (for example theft grace expiring and spawning a response detail).
    /// </summary>
    public void ExpectFeatureTransitions(
        string reason,
        IReadOnlyList<Actor> actors,
        FeatureTransitionSpec spec)
    {
        if (Fidelity == Fidelity.EndToEnd)
        {
            throw new InvalidOperationException(
                $"Feature transition '{reason}' is forbidden at EndToEnd — e2e permits native spawns and zero teleports.");
        }

        if (actors == null || actors.Count == 0)
        {
            throw new ArgumentException("ExpectFeatureTransitions requires at least one actor.", nameof(actors));
        }

        AuthorizeFeatureTransitions(reason, actors, spec);
    }

    /// <summary>
    /// True once every actor's ordered feature position expectations have been independently observed
    /// and consumed by TeleportMonitor. Use this before opening a continuous physical transit so a
    /// role-module swap cannot hide the preceding feature teleport.
    /// </summary>
    public bool FeaturePositionsObserved(IReadOnlyList<Actor> actors)
    {
        if (actors == null || actors.Count == 0)
        {
            throw new ArgumentException("FeaturePositionsObserved requires at least one actor.", nameof(actors));
        }

        return actors.All(actor => !Registry.HasPendingFeatureMoves(actor.Hub));
    }

    /// <summary>
    /// Opens or queues a bounded feature-owned physical transit without moving anyone. When ordered
    /// feature destinations remain, consuming the final destination atomically arms the transit there;
    /// otherwise it starts at the actor's current position. While queued, near-anchor corrections require
    /// the spec's pre-arm production-state gate. Once armed, continuous overrides require their own
    /// production-state gate and speed/band constraints, with one shared budget per simulation frame.
    /// BoundsMonitor stays quiet only while
    /// a destination or armed transit is active; TeleportMonitor remains selective throughout.
    /// </summary>
    public void BeginFeatureTransit(string reason, IReadOnlyList<Actor> actors, FeatureTransitSpec spec)
    {
        if (Fidelity == Fidelity.EndToEnd)
        {
            throw new InvalidOperationException(
                $"Feature transit '{reason}' is forbidden at EndToEnd — e2e permits no transition allowances.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("Feature-transit reason is required.", nameof(reason));
        }

        if (actors == null || actors.Count == 0)
        {
            throw new ArgumentException("BeginFeatureTransit requires at least one actor.", nameof(actors));
        }

        if (spec == null)
        {
            throw new ArgumentNullException(nameof(spec));
        }

        foreach (Actor actor in actors)
        {
            Registry.BeginFeatureTransit(
                actor,
                reason,
                spec.WithinSeconds,
                spec.MaxHorizontalDistance,
                spec.MinY,
                spec.MaxY,
                () => spec.PreArmOverrideGate(actor),
                () => spec.ContinuousOverrideGate(actor),
                spec.OverrideMinY,
                spec.OverrideMaxY,
                spec.MaxOverrideSpeed);
        }

        Emit(new TelemetryEvent("feature_transit_authorized", reason, new Dictionary<string, object?>
        {
            ["actors"] = actors.Select(actor => actor.Label).ToList(),
            ["within_s"] = spec.WithinSeconds,
            ["max_horizontal_m"] = spec.MaxHorizontalDistance,
            ["min_y"] = spec.MinY,
            ["max_y"] = spec.MaxY,
            ["override_min_y"] = spec.OverrideMinY,
            ["override_max_y"] = spec.OverrideMaxY,
            ["override_max_speed"] = spec.MaxOverrideSpeed,
        }));
        EventLog.Line("STEP",
            $"scenario={_scenarioName} FEATURE TRANSIT '{reason}' actors={actors.Count} radius={spec.MaxHorizontalDistance:0.##}m y=[{spec.MinY:0.##},{spec.MaxY:0.##}] overrides=[{spec.OverrideMinY:0.##},{spec.OverrideMaxY:0.##}]@{spec.MaxOverrideSpeed:0.##}m/s within={spec.WithinSeconds:0.##}s");
    }

    /// <summary>
    /// Closes a feature transit without moving anyone. Each actor must still be inside its authorized
    /// corridor and deadline; scenarios should first assert the semantic arrival state (for example,
    /// grounded with gravity restored).
    /// </summary>
    public void CompleteFeatureTransit(string reason, IReadOnlyList<Actor> actors)
    {
        if (Fidelity == Fidelity.EndToEnd)
        {
            throw new InvalidOperationException(
                $"Feature transit completion '{reason}' is forbidden at EndToEnd.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("Feature-transit completion reason is required.", nameof(reason));
        }

        if (actors == null || actors.Count == 0)
        {
            throw new ArgumentException("CompleteFeatureTransit requires at least one actor.", nameof(actors));
        }

        foreach (Actor actor in actors)
        {
            if (!Registry.TryCompleteFeatureTransit(actor.Hub, actor.Position, out string detail))
            {
                throw new RequireException(
                    $"feature transit completion '{reason}': {actor.Label} at {actor.Position} is outside its active corridor or deadline");
            }

            EventLog.Line("STEP", $"actor={actor.Label} {detail}: {reason}");
            Emit(new TelemetryEvent("feature_transit_completed", detail, new Dictionary<string, object?>
            {
                ["actor"] = actor.Label,
                ["reason"] = reason,
                ["pos"] = new[] { actor.Position.x, actor.Position.y, actor.Position.z },
            }));
        }
    }

    private void AuthorizeFeatureTransitions(string reason, IReadOnlyList<Actor> actors, FeatureTransitionSpec spec)
    {
        foreach (Actor actor in actors)
        {
            Registry.AuthorizeFeatureTransition(actor, reason, spec);
        }

        Emit(new TelemetryEvent("feature_transition_authorized", reason, new Dictionary<string, object?>
        {
            ["actors"] = actors.Select(a => a.Label).ToList(),
            ["roles_per_actor"] = spec.Roles.Count,
            ["positions_per_actor"] = spec.Positions.Count,
            ["within_s"] = spec.WithinSeconds,
        }));
        EventLog.Line("STEP",
            $"scenario={_scenarioName} FEATURE '{reason}' actors={actors.Count} roles/actor={spec.Roles.Count} positions/actor={spec.Positions.Count} within={spec.WithinSeconds:0.#}s");
    }

    // ---- observer-facing stage beats ------------------------------------------------------------

    /// <summary>
    /// Announces one bilingual observer-facing stage globally using a short native Broadcast, mirrors
    /// it to STEP + JSONL telemetry, and holds the physical scene for the requested real duration.
    /// On normal completion the timed broadcast expires by itself; only an early exit
    /// (failure/timeout/run abort) clears broadcasts, so feature-sent broadcasts survive beat
    /// boundaries in passing runs.
    /// </summary>
    public float ViewingBeat(string english, string chinese, float seconds)
        => RegisterPending(ViewingBeatBody(english, chinese, seconds));

    private IEnumerator<float> ViewingBeatBody(string english, string chinese, float seconds)
    {
        if (string.IsNullOrWhiteSpace(english) || string.IsNullOrWhiteSpace(chinese))
        {
            throw new ArgumentException("ViewingBeat requires both English and Chinese observer text.");
        }

        if (seconds < 3f || seconds > 10f)
        {
            throw new ArgumentOutOfRangeException(nameof(seconds),
                "ViewingBeat must hold a physical stage for 3–10 seconds.");
        }

        string text = $"<size=26><b>PLAYTEST · {english}</b></size>\n<size=24>{chinese}</size>";
        ushort duration = (ushort)Math.Ceiling(seconds);
        Emit(new TelemetryEvent("viewing_beat", english, new Dictionary<string, object?>
        {
            ["english"] = english,
            ["chinese"] = chinese,
            ["duration_s"] = seconds,
        }));
        EventLog.Line("STAGE", $"scenario={_scenarioName} EN='{english}' CN='{chinese}' hold={seconds:0.##}s");
        LabApi.Features.Wrappers.Server.SendBroadcast(text, duration, global::Broadcast.BroadcastFlags.Normal,
            shouldClearPrevious: false);
        bool held = false;
        try
        {
            yield return Timing.WaitForSeconds(seconds);
            held = true;
        }
        finally
        {
            // Completed beats let the timed broadcast expire naturally — a global clear here would
            // also wipe broadcasts the production feature under test sent during the hold. Only an
            // early exit (failure/timeout/abort) clears, so a stale test banner never outlives its beat.
            if (!held)
            {
                try { LabApi.Features.Wrappers.Server.ClearBroadcasts(); } catch { /* test-only best effort */ }
            }
        }
    }

    // ---- waits ----------------------------------------------------------------------------------

    /// <summary>MEC yield value: real wall-clock wait. Usage: yield return ctx.Wait(2f);</summary>
    public float Wait(float seconds) => Timing.WaitForSeconds(seconds);

    /// <summary>MEC yield value: wait exactly one frame.</summary>
    public float WaitOneFrame() => Timing.WaitForOneFrame;

    /// <summary>
    /// Polls a predicate once per frame until it is true or <paramref name="timeoutSeconds"/> elapses.
    /// Timeout throws <see cref="RequireException"/> (= scenario FAIL). Usage (one-liner, yield-based):
    /// <code>yield return ctx.WaitUntil(() => cond, 5f, "cond description");</code>
    /// </summary>
    public float WaitUntil(Func<bool> predicate, float timeoutSeconds, string what = "condition")
    {
        return RegisterPending(WaitUntilBody(predicate, timeoutSeconds, what));
    }

    /// <summary>
    /// Registers a verb body as a pending sub-enumerator the runner pumps inline (shared by
    /// WaitUntil and all Actor verbs so their Require/timeout failures propagate). Registering
    /// several verbs before the next yield pumps them CONCURRENTLY.
    /// </summary>
    internal float RegisterPending(IEnumerator<float> body)
    {
        _pendingWaits.Enqueue(body);
        return Timing.WaitForOneFrame;
    }

    /// <summary>Drains all verbs registered since the last yield. Runner-owned.</summary>
    internal List<IEnumerator<float>>? TakePendingWaits()
    {
        if (_pendingWaits.Count == 0)
        {
            return null;
        }

        List<IEnumerator<float>> pending = new(_pendingWaits.Count);
        while (_pendingWaits.Count > 0)
        {
            pending.Add(_pendingWaits.Dequeue());
        }

        return pending;
    }

    private IEnumerator<float> WaitUntilBody(Func<bool> predicate, float timeoutSeconds, string what)
    {
        double deadline = _log.ElapsedSeconds + timeoutSeconds;
        while (!predicate())
        {
            if (_log.ElapsedSeconds >= deadline)
            {
                throw new RequireException($"WaitUntil timed out after {timeoutSeconds:0.##}s: {what}");
            }

            yield return Timing.WaitForOneFrame;
        }
    }
}
