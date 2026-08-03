using System;
using System.Collections.Generic;
using System.Linq;
using PlayerRoles.FirstPersonControl;
using PlaytestHarness.Actors;
using PlaytestHarness.Core;
using PlaytestHarness.Probes;
using PlaytestHarness.Telemetry;
using UnityEngine;

namespace PlaytestHarness.Monitors;

/// <summary>
/// Two detection layers (PT-006 closed the sampling blind spots):
///
/// 1) NATIVE OVERRIDE HOOK — on actor-ready the monitor subscribes to the FPC module's
///    OnServerPositionOverwritten (decompiled FirstPersonMovementModule.ServerOverridePosition
///    invokes it after every server position override — the path ALL code teleports take:
///    TryOverridePosition, LabAPI Position, RA bring/goto, pocket dimension...). The hook is
///    synchronous and displacement-independent: a first-frame snap, a 0.3 m nudge, a snap during
///    a server hitch, and a snap in the scenario's final frame are ALL observed. At EndToEnd ANY
///    override is a violation; below e2e an override must consume a fresh one-shot ordered-move
///    mark (fast-travel/quick TP mark exactly one override each — PT-007: the mark is consumed by
///    the very override it was created for, so a stale mark can no longer excuse a later
///    unauthorized jump, and spawn-time marks are cleared at ready by the registry).
///
/// 2) PER-TICK DISPLACEMENT SCAN (backup net) — flags position jumps above physically-plausible
///    per-tick budgets for anything that bypasses ServerOverridePosition. Seeding happens at
///    actor-ready (not first tick), and the runner calls a synchronous TickNow before evaluating
///    each scenario result.
///
/// The hook is re-attached when the FPC module instance changes (role change) and detached on
/// actor destruction/dispose — modules are PARENTED TO POOLED role objects, a leaked delegate
/// would fire for whoever gets the pooled role next.
/// </summary>
internal sealed class TeleportMonitor : IRoundMonitor, IDisposable
{
    /// <summary>Fastest legitimate horizontal speed (sprint ~6 m/s; margin for lunges under grace marks).</summary>
    private const float MaxLegitHorizontalSpeed = 8f;

    /// <summary>Fastest legitimate upward speed (jump impulse peaks well under this).</summary>
    private const float MaxLegitRiseSpeed = 6f;

    /// <summary>Fastest legitimate downward speed (free fall; FPC terminal velocity is below this).</summary>
    private const float MaxLegitFallSpeed = 40f;

    // Continuous feature transit only excuses the production roof-punch glide: near-vertical, never
    // upward, and at most one small override beyond physically plausible fall since the last sample.
    private const float TransitMaxHorizontalOverride = 0.5f;
    private const float TransitMaxUpOverride = 0.1f;
    private const float TransitExtraDownOverride = 1.5f;

    /// <summary>Only sub-millimetre floating-point drift is treated as a true override no-op.</summary>
    private const float OverrideNoOpEpsilon = 0.0001f;

    private readonly ActorRegistry _registry;
    private readonly EventLog _log;
    private readonly Fidelity _level;
    private readonly float _thresholdMeters;
    // Keyed by Actor, not ReferenceHub: destroyed hubs throw NRE from GetHashCode (it reads the
    // dead gameObject), which poisons any dictionary they key.
    private readonly List<Violation> _violations = new();
    private readonly Dictionary<Actor, Vector3> _lastPositions = new();
    private readonly Dictionary<Actor, float> _lastPositionTimes = new();
    private readonly Dictionary<Actor, (FirstPersonMovementModule Module, Action Handler)> _hooks = new();
    private float _lastTickTime;

    public TeleportMonitor(ActorRegistry registry, EventLog log, Fidelity level, float thresholdMeters)
    {
        _registry = registry;
        _log = log;
        _level = level;
        _thresholdMeters = thresholdMeters;
        _lastTickTime = Time.time;
        _registry.ActorReady += OnActorReady;
        _registry.ActorRoleChanging += OnActorRoleChanging;
        _registry.ActorRoleChanged += OnActorRoleChanged;
    }

    public string Name => "TeleportMonitor";

    public IReadOnlyList<Violation> Violations => _violations;

    public void OnEvent(TelemetryEvent e)
    {
    }

    /// <summary>Seeds the displacement scan and attaches the native override hook the moment the actor is ready.</summary>
    private void OnActorReady(Actor actor)
    {
        if (!actor.Hub || !actor.Hub.gameObject)
        {
            return;
        }

        Rebaseline(actor, actor.Hub.transform.position, Time.time);
        AttachHook(actor);
    }

    private void OnActorRoleChanging(Actor actor) => DetachHook(actor);

    private void OnActorRoleChanged(Actor actor)
    {
        if (!actor.Hub || !actor.Hub.gameObject)
        {
            return;
        }

        if (!_lastPositions.ContainsKey(actor))
        {
            Rebaseline(actor, actor.Hub.transform.position, Time.time);
        }

        AttachHook(actor);
    }

    private void AttachHook(Actor actor)
    {
        if (actor.Hub.roleManager?.CurrentRole is not IFpcRole fpc)
        {
            DetachHook(actor);
            return;
        }

        FirstPersonMovementModule module = fpc.FpcModule;
        if (_hooks.TryGetValue(actor, out (FirstPersonMovementModule Module, Action Handler) existing))
        {
            if (ReferenceEquals(existing.Module, module))
            {
                return;
            }

            DetachHook(actor);
        }

        Action handler = () => OnPositionOverridden(actor);
        module.OnServerPositionOverwritten += handler;
        _hooks[actor] = (module, handler);
    }

    private void DetachHook(Actor actor)
    {
        if (!_hooks.TryGetValue(actor, out (FirstPersonMovementModule Module, Action Handler) hook))
        {
            return;
        }

        _hooks.Remove(actor);
        // The module object stays a valid C# reference even when its role was pooled/destroyed.
        if (hook.Module != null)
        {
            hook.Module.OnServerPositionOverwritten -= hook.Handler;
        }
    }

    /// <summary>
    /// Fires synchronously inside every native ServerOverridePosition on a ready actor. This is
    /// the authoritative "a code teleport happened" signal — no threshold, no sampling window.
    /// </summary>
    private void OnPositionOverridden(Actor actor)
    {
        if (!actor.Hub || !actor.Hub.gameObject || !actor.IsReady)
        {
            return;
        }

        float now = Time.time;
        Vector3 pos = actor.Hub.transform.position;
        _lastPositions.TryGetValue(actor, out Vector3 last);
        if (!_lastPositionTimes.TryGetValue(actor, out float lastPositionAt))
        {
            lastPositionAt = now;
        }

        // One-shot: each ordered move (fast-travel/quick TP) marks exactly the one override it
        // performs; the mark is consumed HERE, synchronously, by that very override (PT-007).
        bool ordered = _registry.TryConsumeOrderedMove(actor.Hub);
        float distance = Vector3.Distance(last, pos);
        // FpcMotor can call ServerOverridePosition with its already-current authoritative position;
        // only sub-millimetre drift is a sync no-op. Small real overrides remain observable, including at e2e.
        // Still consume an ordered mark bound to the call.
        if (distance <= OverrideNoOpEpsilon)
        {
            Rebaseline(actor, pos, now);
            return;
        }

        if (ordered && _level != Fidelity.EndToEnd)
        {
            // Harness-ordered TP (fast-travel/quick) — legal below e2e. Re-baseline the
            // displacement scan so the jump is not double-reported by the next tick.
            Rebaseline(actor, pos, now);
            return;
        }

        if (_level != Fidelity.EndToEnd &&
            _registry.TryConsumeExpectedRoleMove(actor.Hub, pos, out string roleMoveDetail))
        {
            Rebaseline(actor, pos, now);
            EventLog.Line("STEP", $"actor={actor.Label} {roleMoveDetail} pos={PlacementProbes.Format(pos)}");
            return;
        }

        if (_level != Fidelity.EndToEnd && _registry.TryConsumeFeatureMove(actor.Hub, pos, out string featureDetail))
        {
            Rebaseline(actor, pos, now);
            EventLog.Line("STEP", $"actor={actor.Label} {featureDetail} pos={PlacementProbes.Format(pos)}");
            _log.Emit(new TelemetryEvent("feature_transition", featureDetail, new Dictionary<string, object?>
            {
                ["actor"] = actor.Label,
                ["kind"] = "position_override",
                ["pos"] = new[] { pos.x, pos.y, pos.z },
            }));
            return;
        }

        // Production may drive one override per frame after this monitor sampled earlier in the same
        // frame. Use at least the simulation frame delta so a speed-bounded Time.deltaTime step is accepted.
        float transitElapsed = Mathf.Max(0.001f, Time.deltaTime, now - lastPositionAt);
        float transitMaxDown = Mathf.Max(
            TransitExtraDownOverride,
            (MaxLegitFallSpeed * transitElapsed) + TransitExtraDownOverride);
        if (_level != Fidelity.EndToEnd &&
            _registry.TryAllowFeatureTransitMove(
                actor.Hub,
                last,
                pos,
                TransitMaxHorizontalOverride,
                TransitMaxUpOverride,
                transitMaxDown,
                transitElapsed,
                out string transitDetail,
                out bool firstTransitMove))
        {
            Rebaseline(actor, pos, now);
            if (firstTransitMove)
            {
                EventLog.Line("STEP", $"actor={actor.Label} {transitDetail} pos={PlacementProbes.Format(pos)}");
                _log.Emit(new TelemetryEvent("feature_transition", transitDetail, new Dictionary<string, object?>
                {
                    ["actor"] = actor.Label,
                    ["kind"] = "position_transit",
                    ["pos"] = new[] { pos.x, pos.y, pos.z },
                }));
            }

            return;
        }

        string kind = ordered
            ? "ordered TP at EndToEnd (ANY teleport is a violation at e2e)"
            : "UNAUTHORIZED position override (native ServerOverridePosition without an ordered-move mark)";
        Violation violation = new(Name, actor.Label,
            $"{kind}: {PlacementProbes.Format(last)} -> {PlacementProbes.Format(pos)} (dist={distance:0.##}m)",
            _log.ElapsedSeconds);
        _violations.Add(violation);
        EventLog.Line("VIOLATION", violation.ToString(), error: true);
        _log.Emit(new TelemetryEvent("violation", violation.Detail, new Dictionary<string, object?>
        {
            ["monitor"] = Name,
            ["actor"] = actor.Label,
            ["kind"] = "position_override",
            ["dist_m"] = Math.Round(distance, 2),
            ["ordered"] = ordered,
        }));

        // Re-baseline either way — the violation is recorded once, not once per layer.
        Rebaseline(actor, pos, now);
    }

    private void Rebaseline(Actor actor, Vector3 position, float at)
    {
        _lastPositions[actor] = position;
        _lastPositionTimes[actor] = at;
    }

    public void Tick()
    {
        float now = Time.time;
        float tickDelta = Mathf.Max(0.001f, now - _lastTickTime);
        _lastTickTime = now;

        // Snapshot: cleanup barriers destroy actors between/inside ticks.
        foreach (Actor actor in _registry.Actors.ToArray())
        {
            ReferenceHub hub = actor.Hub;
            if (!hub || !hub.gameObject || !actor.IsReady)
            {
                // Spawn-time placement (native role spawnpoint warp) is not a teleport violation;
                // monitoring starts once WaitReady completed. Unity-bool checks catch mid-destroy hubs.
                continue;
            }

            // Role changes swap the FPC module instance — keep the override hook attached to the
            // CURRENT module (the role watchdog separately flags the external role set itself).
            AttachHook(actor);

            Vector3 pos = hub.transform.position;
            if (!_lastPositions.TryGetValue(actor, out Vector3 last))
            {
                // Defensive: ready actors are seeded by OnActorReady; this only covers actors that
                // became FPC after a non-FPC phase.
                Rebaseline(actor, pos, now);
                continue;
            }

            Rebaseline(actor, pos, now);

            // Component-wise budgets over the ACTUAL tick interval, with the configured threshold
            // as the floor — a hitchy tick or long monitor interval does not flag legitimate
            // running/falling, while real TPs (instant multi-meter snaps in any direction) trip.
            // Overrides through ServerOverridePosition were already handled (and re-baselined) by
            // the synchronous hook; anything tripping HERE bypassed the native override path.
            Vector3 delta = pos - last;
            float horizontal = new Vector2(delta.x, delta.z).magnitude;
            float up = Mathf.Max(0f, delta.y);
            float down = Mathf.Max(0f, -delta.y);
            float allowedHorizontal = Mathf.Max(_thresholdMeters, MaxLegitHorizontalSpeed * tickDelta);
            float allowedUp = Mathf.Max(_thresholdMeters, MaxLegitRiseSpeed * tickDelta);
            float allowedDown = Mathf.Max(_thresholdMeters, MaxLegitFallSpeed * tickDelta);
            if (horizontal <= allowedHorizontal && up <= allowedUp && down <= allowedDown)
            {
                continue;
            }

            if (_level != Fidelity.EndToEnd &&
                _registry.TryConsumeExpectedRoleMove(actor.Hub, pos, out string roleMoveDetail))
            {
                EventLog.Line("STEP",
                    $"actor={actor.Label} {roleMoveDetail} pos={PlacementProbes.Format(pos)} (displacement scan)");
                continue;
            }

            if (_level != Fidelity.EndToEnd && _registry.TryConsumeFeatureMove(actor.Hub, pos, out string featureDetail))
            {
                EventLog.Line("STEP", $"actor={actor.Label} {featureDetail} pos={PlacementProbes.Format(pos)} (displacement scan)");
                _log.Emit(new TelemetryEvent("feature_transition", featureDetail, new Dictionary<string, object?>
                {
                    ["actor"] = actor.Label,
                    ["kind"] = "position_displacement",
                    ["pos"] = new[] { pos.x, pos.y, pos.z },
                }));
                continue;
            }

            Violation violation = new(Name, actor.Label,
                $"UNORDERED move (bypassed ServerOverridePosition): dH={horizontal:0.##}m dUp={up:0.##}m dDown={down:0.##}m in one tick (tickDelta={tickDelta:0.###}s, budgets H/U/D={allowedHorizontal:0.##}/{allowedUp:0.##}/{allowedDown:0.##}m) {PlacementProbes.Format(last)} -> {PlacementProbes.Format(pos)}",
                _log.ElapsedSeconds);
            _violations.Add(violation);
            EventLog.Line("VIOLATION", violation.ToString(), error: true);
            _log.Emit(new TelemetryEvent("violation", violation.Detail, new Dictionary<string, object?>
            {
                ["monitor"] = Name,
                ["actor"] = actor.Label,
                ["moved_h_m"] = Math.Round(horizontal, 2),
                ["moved_up_m"] = Math.Round(up, 2),
                ["moved_down_m"] = Math.Round(down, 2),
                ["tick_delta_s"] = Math.Round(tickDelta, 3),
            }));
        }

        // Drop stale entries + hooks (actors destroyed between ticks).
        List<Actor>? stale = null;
        foreach (Actor tracked in _lastPositions.Keys)
        {
            if (!tracked.Hub || !tracked.Hub.gameObject)
            {
                (stale ??= new List<Actor>()).Add(tracked);
            }
        }

        if (stale != null)
        {
            foreach (Actor tracked in stale)
            {
                _lastPositions.Remove(tracked);
                _lastPositionTimes.Remove(tracked);
                DetachHook(tracked);
            }
        }
    }

    public void Dispose()
    {
        _registry.ActorReady -= OnActorReady;
        _registry.ActorRoleChanging -= OnActorRoleChanging;
        _registry.ActorRoleChanged -= OnActorRoleChanged;
        foreach (Actor actor in _hooks.Keys.ToArray())
        {
            DetachHook(actor);
        }
    }
}
