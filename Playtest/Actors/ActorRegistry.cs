using System;
using System.Collections.Generic;
using System.Linq;
using LabApi.Features.Wrappers;
using Mirror;
using PlayerRoles;
using PlaytestHarness.Core;
using PlaytestHarness.Monitors;
using PlaytestHarness.Telemetry;
using UnityEngine;

namespace PlaytestHarness.Actors;

/// <summary>
/// Tracks harness-owned actors for the active run: spawn bookkeeping, cleanup by nickname prefix,
/// the role watchdog (an EXTERNAL role change hitting an owned actor is a violation — plan risk
/// "Role-stomping plugins"), and ordered-move marks that let TeleportMonitor distinguish
/// harness-ordered teleports from unordered position jumps.
/// </summary>
internal sealed class ActorRegistry : IDisposable
{
    private readonly string _nicknamePrefix;
    private readonly Dictionary<ReferenceHub, Actor> _actors = new();
    private readonly Dictionary<ReferenceHub, float> _orderedMoveMarks = new();
    private readonly Dictionary<ReferenceHub, RoleTypeId> _expectedRoles = new();
    private readonly Dictionary<ReferenceHub, ExpectedRoleMove> _expectedRoleMoves = new();
    private readonly Dictionary<ReferenceHub, Queue<FeatureRoleAllowance>> _featureRoles = new();
    private readonly Dictionary<ReferenceHub, Queue<FeatureMoveAllowance>> _featureMoves = new();
    private readonly Dictionary<ReferenceHub, FeatureTransitAllowance> _featureTransits = new();
    private readonly HashSet<ushort> _trackedWorldItems = new();
    private readonly List<Violation> _watchdogViolations = new();
    private readonly EventLog _log;
    private readonly Movement.IMovementProvider? _movement;

    /// <summary>Seconds after an ordered move during which a large positional jump is considered intended.</summary>
    private const float OrderedMoveGraceSeconds = 1.5f;

    private sealed class ExpectedRoleMove
    {
        public RoleTypeId Role;
        public Vector3 Destination;
        public double Deadline;
    }

    private sealed class FeatureRoleAllowance
    {
        public string Reason = string.Empty;
        public string Label = string.Empty;
        public RoleTypeId Role;
        public double Deadline;
    }

    private sealed class FeatureMoveAllowance
    {
        public string Reason = string.Empty;
        public string Label = string.Empty;
        public Func<Vector3, bool> Destination = _ => false;
        public double Deadline;
    }

    private sealed class FeatureTransitAllowance
    {
        public string Reason = string.Empty;
        public Vector3 Start;
        public float MaxHorizontalDistance;
        public float MinY;
        public float MaxY;
        public Func<bool> PreArmOverrideGate = () => false;
        public bool PreArmAnchorReady;
        public Func<bool> ContinuousOverrideGate = () => false;
        public float OverrideMinY;
        public float OverrideMaxY;
        public float MaxOverrideSpeed;
        public float WithinSeconds;
        public double Deadline;
        public bool Armed;
        public bool MoveObserved;
        public int BudgetFrame = -1;
        public float RemainingGatedDownBudget;
    }

    public ActorRegistry(string nicknamePrefix, EventLog log, Movement.IMovementProvider? movement = null)
    {
        _nicknamePrefix = nicknamePrefix;
        _log = log;
        _movement = movement;
        PlayerRoleManager.OnServerRoleSet += OnServerRoleSet;
        PlayerRoleManager.OnRoleChanged += OnRoleChanged;
    }

    public IReadOnlyCollection<Actor> Actors => _actors.Values;

    public string NicknamePrefix => _nicknamePrefix;

    /// <summary>Role watchdog violations observed so far (drained by MonitorHost).</summary>
    public IReadOnlyList<Violation> WatchdogViolations => _watchdogViolations;

    public bool TryGetActor(ReferenceHub hub, out Actor actor) => _actors.TryGetValue(hub, out actor!);

    /// <summary>
    /// Raised when an actor completed WaitReady (role + placement + settle). TeleportMonitor uses
    /// this to attach its native position-override hook and seed its per-actor position.
    /// </summary>
    public event Action<Actor>? ActorReady;

    /// <summary>Raised synchronously before a tracked ready actor's current role module is replaced.</summary>
    public event Action<Actor>? ActorRoleChanging;

    /// <summary>Raised after a harness-arranged role replacement returns and the new module is available.</summary>
    public event Action<Actor>? ActorRoleChanged;

    internal void Track(Actor actor)
    {
        _actors[actor.Hub] = actor;
    }

    /// <summary>
    /// Called by CompleteSpawn the moment IsReady is set. Clears any leftover ordered-move mark
    /// from spawn placement (pre-ready moves are not monitor-visible, so their marks must never
    /// survive into the monitored phase and excuse a later unauthorized TP — PT-007), then
    /// notifies monitors.
    /// </summary>
    internal void NotifyReady(Actor actor)
    {
        _orderedMoveMarks.Remove(actor.Hub);
        ActorReady?.Invoke(actor);
    }

    internal void Untrack(ReferenceHub hub)
    {
        _actors.Remove(hub);
        _orderedMoveMarks.Remove(hub);
        _expectedRoles.Remove(hub);
        _expectedRoleMoves.Remove(hub);
        _featureRoles.Remove(hub);
        _featureMoves.Remove(hub);
        _featureTransits.Remove(hub);
    }

    /// <summary>
    /// Tracks an exact scenario-created world serial. Cleanup destroys only these serials, never
    /// unrelated pickups that happened to appear while the scenario was running.
    /// </summary>
    internal void TrackWorldItem(WorldItem item, string reason)
    {
        if (item == null || item.Serial == 0)
        {
            throw new ArgumentException("A valid exact world item is required.", nameof(item));
        }

        TrackWorldSerial(item.Serial, item.Type, reason);
    }

    private void TrackWorldSerial(ushort serial, ItemType type, string reason)
    {
        if (!_trackedWorldItems.Add(serial))
        {
            return;
        }

        EventLog.Line("STEP", $"tracked scenario world item serial={serial} type={type} reason='{reason}'");
        _log.Emit(new TelemetryEvent("world_item_tracked", reason, new Dictionary<string, object?>
        {
            ["serial"] = serial,
            ["type"] = type.ToString(),
        }));
    }

    /// <summary>
    /// Captures every exact item serial still owned by a harness actor before actor destruction.
    /// Native teardown can materialize those same serials as pickups at frame end; retaining exact
    /// provenance lets the delayed cleanup remove them without touching ambient/player pickups.
    /// </summary>
    private void TrackActorInventoryForCleanup()
    {
        foreach (Actor actor in _actors.Values)
        {
            if (actor.Hub?.inventory?.UserInventory?.Items == null)
            {
                continue;
            }

            foreach (InventorySystem.Items.ItemBase item in actor.Hub.inventory.UserInventory.Items.Values)
            {
                if (item != null)
                {
                    TrackWorldSerial(item.ItemSerial, item.ItemTypeId, $"{actor.Label} inventory teardown");
                }
            }

            // Player teardown converts reserve ammo into NEW pickup serials, which cannot be tied
            // back to an actor after the fact. Clear reserve ammo on the harness-owned actor before
            // destruction so those secondary pickups are never created; unrelated world ammo is untouched.
            int reserveUnits = actor.Hub.inventory.UserInventory.ReserveAmmo.Values.Sum(value => (int)value);
            if (reserveUnits > 0)
            {
                actor.Hub.inventory.UserInventory.ReserveAmmo.Clear();
                _log.Emit(new TelemetryEvent("actor_ammo_cleanup", "prevent teardown ammo pickups", new Dictionary<string, object?>
                {
                    ["actor"] = actor.Label,
                    ["units"] = reserveUnits,
                }));
                EventLog.Line("STEP",
                    $"cleared {reserveUnits} reserve-ammo unit(s) from harness actor {actor.Label} before teardown");
            }
        }
    }

    /// <summary>
    /// Destroys every grounded pickup whose exact serial is scenario-owned. When clearMissing is
    /// false, absent serials stay pending because actor destruction may spawn them one frame later.
    /// </summary>
    internal int DestroyTrackedWorldItems(bool clearMissing = true)
    {
        int trackedBefore = _trackedWorldItems.Count;
        int destroyed = 0;
        foreach (ushort serial in _trackedWorldItems.ToArray())
        {
            Pickup? pickup = Pickup.List.FirstOrDefault(candidate =>
                candidate is { IsDestroyed: false } && candidate.Serial == serial);
            if (pickup == null)
            {
                continue;
            }

            try
            {
                ItemType type = pickup.Type;
                pickup.Destroy();
                _trackedWorldItems.Remove(serial);
                destroyed++;
                _log.Emit(new TelemetryEvent("world_item_cleanup", "exact scenario-owned serial", new Dictionary<string, object?>
                {
                    ["serial"] = serial,
                    ["type"] = type.ToString(),
                }));
            }
            catch (Exception exception)
            {
                EventLog.Line("STEP",
                    $"world item cleanup failed serial={serial}: {exception.GetBaseException().Message}", error: true);
            }
        }

        int pending = _trackedWorldItems.Count;
        if (clearMissing)
        {
            _trackedWorldItems.Clear();
        }

        EventLog.Line("STEP",
            $"exact world item cleanup destroyed={destroyed} tracked={trackedBefore} pending_after_scan={pending} clear_missing={clearMissing}");
        return destroyed;
    }

    /// <summary>
    /// Marks that the harness is about to perform ONE position override on this hub. The mark is
    /// consumed synchronously by TeleportMonitor's native override hook inside that very override
    /// (PT-007) — one mark excuses exactly one override; it never lingers to excuse a later jump.
    /// INTERNAL: scenario assemblies must not be able to pre-authorize teleports (PT-001).
    /// </summary>
    internal void MarkOrderedMove(ReferenceHub hub) => _orderedMoveMarks[hub] = Time.time;

    /// <summary>Cancels a pending mark (the ordered move failed before performing its override).</summary>
    internal void ClearOrderedMove(ReferenceHub hub) => _orderedMoveMarks.Remove(hub);

    /// <summary>
    /// Consumes the hub's pending ordered-move mark if still fresh. Returns true when the observed
    /// jump was harness-ordered. Stale marks (never observed within the grace window) expire.
    /// </summary>
    internal bool TryConsumeOrderedMove(ReferenceHub hub)
    {
        if (!_orderedMoveMarks.TryGetValue(hub, out float at))
        {
            return false;
        }

        _orderedMoveMarks.Remove(hub);
        return Time.time - at <= OrderedMoveGraceSeconds;
    }

    /// <summary>Marks a harness-initiated role change so the watchdog does not flag it. Harness-internal (PT-001).</summary>
    internal void MarkExpectedRole(ReferenceHub hub, RoleTypeId role) => _expectedRoles[hub] = role;

    /// <summary>
    /// Authorizes only the native SpectatorRole.SpawnObject relocation to its documented
    /// Vector3.up * 6000 position. Other role changes use RoleSpawnFlags.None and receive no
    /// spatial allowance, so an unexpected role-change warp remains a monitor violation.
    /// </summary>
    internal void MarkExpectedRoleMove(ReferenceHub hub, RoleTypeId role)
    {
        if (role != RoleTypeId.Spectator)
        {
            return;
        }

        _expectedRoleMoves[hub] = new ExpectedRoleMove
        {
            Role = role,
            Destination = Vector3.up * 6000f,
            Deadline = _log.ElapsedSeconds + OrderedMoveGraceSeconds,
        };
    }

    internal void ClearExpectedRoleMove(ReferenceHub hub) => _expectedRoleMoves.Remove(hub);

    internal bool TryConsumeExpectedRoleMove(ReferenceHub hub, Vector3 destination, out string detail)
    {
        detail = string.Empty;
        if (!_expectedRoleMoves.TryGetValue(hub, out ExpectedRoleMove expected))
        {
            return false;
        }

        if (_log.ElapsedSeconds > expected.Deadline)
        {
            _expectedRoleMoves.Remove(hub);
            return false;
        }

        if (hub.roleManager?.CurrentRole?.RoleTypeId != expected.Role ||
            Vector3.Distance(destination, expected.Destination) > 0.25f)
        {
            return false;
        }

        _expectedRoleMoves.Remove(hub);
        detail = $"harness role change consumed native {expected.Role} spawn relocation";
        return true;
    }

    /// <summary>
    /// Consumes the expected-role mark once spawn completed (WaitReady). From then on ANY external
    /// role set — including one to the same role — is a watchdog violation.
    /// </summary>
    internal void ClearExpectedRole(ReferenceHub hub) => _expectedRoles.Remove(hub);

    /// <summary>
    /// Installs ordered, time-bounded expectations for a feature-owned transition. These are consumed
    /// one-for-one by the normal watchdog/teleport monitor; neither monitor is disabled.
    /// </summary>
    internal void AuthorizeFeatureTransition(Actor actor, string reason, FeatureTransitionSpec spec)
    {
        double deadline = _log.ElapsedSeconds + spec.WithinSeconds;
        if (spec.Roles.Count > 0)
        {
            Queue<FeatureRoleAllowance> roles = new();
            foreach (RoleTransitionExpectation expected in spec.Roles)
            {
                roles.Enqueue(new FeatureRoleAllowance
                {
                    Reason = reason,
                    Label = expected.Label,
                    Role = expected.Role,
                    Deadline = deadline,
                });
            }

            _featureRoles[actor.Hub] = roles;
        }

        if (spec.Positions.Count > 0)
        {
            Queue<FeatureMoveAllowance> moves = new();
            foreach (PositionTransitionExpectation expected in spec.Positions)
            {
                moves.Enqueue(new FeatureMoveAllowance
                {
                    Reason = reason,
                    Label = expected.Label,
                    Destination = expected.Destination,
                    Deadline = deadline,
                });
            }

            _featureMoves[actor.Hub] = moves;
        }
    }

    /// <summary>
    /// True while an actor is between ordered feature destinations (for example inside an off-map
    /// cinematic pocket awaiting its final landing). BoundsMonitor suppresses its ordinary room/ground
    /// oracle only for this bounded window; expiry or consumption of the final destination re-arms it.
    /// </summary>
    internal bool IsFeatureSpatialTransitionActive(ReferenceHub hub)
    {
        bool pendingMove = _featureMoves.TryGetValue(hub, out Queue<FeatureMoveAllowance> moves) &&
            moves.Count > 0 && _log.ElapsedSeconds <= moves.Peek().Deadline;
        bool activeTransit = _featureTransits.TryGetValue(hub, out FeatureTransitAllowance transit) &&
            transit.Armed && _log.ElapsedSeconds <= transit.Deadline;
        return pendingMove || activeTransit;
    }

    internal bool HasPendingFeatureMoves(ReferenceHub hub) =>
        _featureMoves.TryGetValue(hub, out Queue<FeatureMoveAllowance> moves) && moves.Count > 0;

    /// <summary>
    /// Revokes this actor's remaining position allowances. Verbs that authorize a transition and
    /// then fail must call this so a leaked allowance cannot silently excuse a later unrelated
    /// teleport inside its time window.
    /// </summary>
    internal void RevokeFeatureMoves(ReferenceHub hub, string reason)
    {
        if (_featureMoves.TryGetValue(hub, out Queue<FeatureMoveAllowance> moves) && moves.Count > 0)
        {
            EventLog.Line("STEP", $"revoked {moves.Count} pending feature-move allowance(s): {reason}");
            _featureMoves.Remove(hub);
        }
    }

    internal void BeginFeatureTransit(
        Actor actor,
        string reason,
        float withinSeconds,
        float maxHorizontalDistance,
        float minY,
        float maxY,
        Func<bool> preArmOverrideGate,
        Func<bool> continuousOverrideGate,
        float overrideMinY,
        float overrideMaxY,
        float maxOverrideSpeed)
    {
        if (_featureTransits.ContainsKey(actor.Hub))
        {
            throw new RequireException(
                $"feature transit '{reason}': {actor.Label} already has a queued or active transit");
        }

        FeatureTransitAllowance transit = new()
        {
            Reason = reason,
            MaxHorizontalDistance = maxHorizontalDistance,
            MinY = minY,
            MaxY = maxY,
            PreArmOverrideGate = preArmOverrideGate,
            ContinuousOverrideGate = continuousOverrideGate,
            OverrideMinY = overrideMinY,
            OverrideMaxY = overrideMaxY,
            MaxOverrideSpeed = maxOverrideSpeed,
            WithinSeconds = withinSeconds,
        };
        _featureTransits[actor.Hub] = transit;

        // When ordered feature destinations are still pending, queue the transit now. Consuming the
        // final destination atomically arms it at that exact position, leaving no BoundsMonitor gap.
        if (HasPendingFeatureMoves(actor.Hub))
        {
            return;
        }

        Vector3 start = actor.Position;
        if (!TryArmFeatureTransit(transit, start))
        {
            _featureTransits.Remove(actor.Hub);
            throw new RequireException(
                $"feature transit '{reason}': {actor.Label} starts at Y={start.y:0.##}, outside [{minY:0.##}, {maxY:0.##}]");
        }
    }

    private bool TryArmFeatureTransit(FeatureTransitAllowance transit, Vector3 start)
    {
        if (start.y < transit.MinY || start.y > transit.MaxY)
        {
            return false;
        }

        transit.Start = start;
        transit.Deadline = _log.ElapsedSeconds + transit.WithinSeconds;
        transit.Armed = true;
        transit.PreArmAnchorReady = false;
        transit.MoveObserved = false;
        transit.BudgetFrame = -1;
        transit.RemainingGatedDownBudget = 0f;
        return true;
    }

    internal bool TryAllowFeatureTransitMove(
        ReferenceHub hub,
        Vector3 origin,
        Vector3 destination,
        float maxHorizontalStep,
        float maxUpStep,
        float maxDownStep,
        float elapsedSinceOrigin,
        out string detail,
        out bool firstObservation)
    {
        detail = string.Empty;
        firstObservation = false;
        if (!_featureTransits.TryGetValue(hub, out FeatureTransitAllowance? transit))
        {
            return false;
        }

        Vector3 delta = destination - origin;
        float horizontal = new Vector2(delta.x, delta.z).magnitude;
        float up = Mathf.Max(0f, delta.y);
        float down = Mathf.Max(0f, -delta.y);

        if (!transit.Armed)
        {
            if (!transit.PreArmAnchorReady ||
                !_featureMoves.TryGetValue(hub, out Queue<FeatureMoveAllowance>? moves) ||
                moves.Count == 0 ||
                _log.ElapsedSeconds > moves.Peek().Deadline ||
                transit.Start.y < transit.MinY || transit.Start.y > transit.MaxY ||
                destination.y < transit.MinY || destination.y > transit.MaxY ||
                !TryEvaluateFeatureTransitGate(hub, transit, transit.PreArmOverrideGate, "pre-arm"))
            {
                return false;
            }

            Vector3 anchorDelta = destination - transit.Start;
            float anchorHorizontal = new Vector2(anchorDelta.x, anchorDelta.z).magnitude;
            if (anchorHorizontal > maxHorizontalStep ||
                Mathf.Abs(anchorDelta.y) > maxUpStep ||
                horizontal > maxHorizontalStep ||
                Mathf.Abs(delta.y) > maxDownStep)
            {
                return false;
            }

            firstObservation = !transit.MoveObserved;
            transit.MoveObserved = true;
            detail = $"feature transit '{transit.Reason}' accepted gated pre-arm anchor correction";
            return true;
        }

        if (!TryGetActiveFeatureTransit(hub, destination, out transit) ||
            !TryEvaluateFeatureTransitGate(hub, transit, transit.ContinuousOverrideGate, "armed") ||
            destination.y < transit.OverrideMinY ||
            destination.y > transit.OverrideMaxY ||
            horizontal > maxHorizontalStep || up > maxUpStep || down > maxDownStep)
        {
            return false;
        }

        if (transit.BudgetFrame != Time.frameCount)
        {
            transit.BudgetFrame = Time.frameCount;
            float elapsed = Mathf.Max(0.001f, elapsedSinceOrigin);
            transit.RemainingGatedDownBudget =
                (transit.MaxOverrideSpeed * elapsed) + 0.05f;
        }

        if (down <= 0f || down > transit.RemainingGatedDownBudget)
        {
            return false;
        }

        transit.RemainingGatedDownBudget =
            Mathf.Max(0f, transit.RemainingGatedDownBudget - down);
        firstObservation = !transit.MoveObserved;
        transit.MoveObserved = true;
        detail = $"feature transit '{transit.Reason}' accepted gated production override";
        return true;
    }

    private bool TryEvaluateFeatureTransitGate(
        ReferenceHub hub,
        FeatureTransitAllowance transit,
        Func<bool> gate,
        string phase)
    {
        try
        {
            return gate();
        }
        catch (Exception exception)
        {
            _featureTransits.Remove(hub);
            string actorLabel = _actors.TryGetValue(hub, out Actor? actor) ? actor.Label : "unknown";
            Violation violation = new(
                "FeatureTransitGate",
                actorLabel,
                $"feature transit '{transit.Reason}' {phase} gate threw {exception.GetType().Name}: {exception.Message}",
                _log.ElapsedSeconds);
            _watchdogViolations.Add(violation);
            EventLog.Line("VIOLATION", violation.ToString(), error: true);
            _log.Emit(new TelemetryEvent("violation", violation.Detail, new Dictionary<string, object?>
            {
                ["monitor"] = violation.Monitor,
                ["actor"] = violation.Actor,
                ["phase"] = phase,
            }));
            return false;
        }
    }

    internal bool TryCompleteFeatureTransit(ReferenceHub hub, Vector3 destination, out string detail)
    {
        if (!TryGetActiveFeatureTransit(hub, destination, out FeatureTransitAllowance transit))
        {
            detail = string.Empty;
            return false;
        }

        _featureTransits.Remove(hub);
        detail = $"feature transit '{transit.Reason}' completed";
        return true;
    }

    private bool TryGetActiveFeatureTransit(
        ReferenceHub hub,
        Vector3 destination,
        out FeatureTransitAllowance transit)
    {
        if (!_featureTransits.TryGetValue(hub, out transit!) ||
            !transit.Armed ||
            _log.ElapsedSeconds > transit.Deadline ||
            destination.y < transit.MinY ||
            destination.y > transit.MaxY ||
            Vector2.Distance(new Vector2(destination.x, destination.z), new Vector2(transit.Start.x, transit.Start.z)) >
                transit.MaxHorizontalDistance)
        {
            transit = null!;
            return false;
        }

        return true;
    }

    internal bool TryConsumeFeatureMove(ReferenceHub hub, Vector3 destination, out string detail)
    {
        detail = string.Empty;
        if (!_featureMoves.TryGetValue(hub, out Queue<FeatureMoveAllowance> moves) || moves.Count == 0)
        {
            return false;
        }

        FeatureMoveAllowance expected = moves.Peek();
        if (_log.ElapsedSeconds > expected.Deadline || !expected.Destination(destination))
        {
            return false;
        }

        moves.Dequeue();
        if (moves.Count == 0)
        {
            _featureMoves.Remove(hub);
            if (_featureTransits.TryGetValue(hub, out FeatureTransitAllowance? transit) &&
                !transit.Armed && !TryArmFeatureTransit(transit, destination))
            {
                _featureTransits.Remove(hub);
            }
        }
        else if (_featureTransits.TryGetValue(hub, out FeatureTransitAllowance? queuedTransit) &&
                 !queuedTransit.Armed)
        {
            queuedTransit.Start = destination;
            queuedTransit.PreArmAnchorReady = true;
        }

        detail = $"feature transition '{expected.Reason}' consumed position '{expected.Label}'";
        return true;
    }

    private bool TryConsumeFeatureRole(ReferenceHub hub, RoleTypeId role, out string detail)
    {
        detail = string.Empty;
        if (!_featureRoles.TryGetValue(hub, out Queue<FeatureRoleAllowance> roles) || roles.Count == 0)
        {
            return false;
        }

        FeatureRoleAllowance expected = roles.Peek();
        if (_log.ElapsedSeconds > expected.Deadline || expected.Role != role)
        {
            return false;
        }

        roles.Dequeue();
        if (roles.Count == 0)
        {
            _featureRoles.Remove(hub);
        }

        detail = $"feature transition '{expected.Reason}' consumed role '{expected.Label}' ({role})";
        return true;
    }

    private void OnRoleChanged(ReferenceHub hub, PlayerRoleBase previousRole, PlayerRoleBase newRole)
    {
        if (_actors.TryGetValue(hub, out Actor? actor) && actor.IsReady)
        {
            ActorRoleChanged?.Invoke(actor);
        }
    }

    private void OnServerRoleSet(ReferenceHub hub, RoleTypeId newRole, RoleChangeReason reason)
    {
        if (!_actors.TryGetValue(hub, out Actor? actor))
        {
            return;
        }

        if (actor.IsReady)
        {
            ActorRoleChanging?.Invoke(actor);
        }

        if (_expectedRoles.TryGetValue(hub, out RoleTypeId expected) && expected == newRole)
        {
            // The harness asked for this change (SpawnActor). Spawn churn (None -> Spectator ->
            // requested) can hit the expected role more than once before readiness, so the mark
            // survives until CompleteSpawn consumes it via ClearExpectedRole — after which an
            // external set to the SAME role is flagged like any other stomp.
            return;
        }

        if (!actor.IsReady)
        {
            // Spawn-time churn: fresh dummies natively pass through Spectator (reason=LateJoin)
            // before the harness role lands. The watchdog arms once WaitReady completed.
            return;
        }

        if (TryConsumeFeatureRole(hub, newRole, out string featureDetail))
        {
            EventLog.Line("STEP", $"actor={actor.Label} {featureDetail}");
            _log.Emit(new TelemetryEvent("feature_transition", featureDetail, new Dictionary<string, object?>
            {
                ["actor"] = actor.Label,
                ["kind"] = "role",
                ["role"] = newRole.ToString(),
            }));
            return;
        }

        Violation violation = new("RoleWatchdog", actor.Label,
            $"EXTERNAL role change to {newRole} (reason={reason}) hit an owned actor — role-stomping plugin on this port?",
            _log.ElapsedSeconds);
        _watchdogViolations.Add(violation);
        EventLog.Line("VIOLATION", violation.ToString(), error: true);
        _log.Emit(new TelemetryEvent("violation", violation.Detail, new Dictionary<string, object?>
        {
            ["monitor"] = violation.Monitor,
            ["actor"] = violation.Actor,
        }));
    }

    /// <summary>Destroys all live actors owned by this registry. Returns how many were removed.</summary>
    public int DestroyAll()
    {
        TrackActorInventoryForCleanup();
        DestroyTrackedWorldItems(clearMissing: false);

        // Untrack BEFORE Destroy: a destroyed ReferenceHub's GetHashCode throws NRE (reads the
        // dead gameObject), which would poison the dictionary Remove.
        List<ReferenceHub> hubs = _actors.Keys.Where(h => h != null).ToList();
        _actors.Clear();
        _orderedMoveMarks.Clear();
        _expectedRoles.Clear();
        _expectedRoleMoves.Clear();
        _featureRoles.Clear();
        _featureMoves.Clear();
        _featureTransits.Clear();
        foreach (ReferenceHub hub in hubs)
        {
            // Provider first (while the hub is alive), then destroy — Release cancels the bot
            // order state so the provider never leaks dead-hub bookkeeping.
            _movement?.Release(hub);
            if (hub.gameObject != null)
            {
                NetworkServer.Destroy(hub.gameObject);
            }
        }

        // Some native teardown paths spawn pickups synchronously with actor destruction. Remove
        // those now; the runner performs one final exact-serial sweep after the frame-end delay.
        DestroyTrackedWorldItems(clearMissing: false);
        return hubs.Count;
    }

    public void Dispose()
    {
        PlayerRoleManager.OnServerRoleSet -= OnServerRoleSet;
        PlayerRoleManager.OnRoleChanged -= OnRoleChanged;
    }
}
