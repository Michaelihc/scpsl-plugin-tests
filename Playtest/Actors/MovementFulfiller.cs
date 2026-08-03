using System;
using System.Collections.Generic;
using System.Linq;
using Interactables.Interobjects.DoorUtils;
using MapGeneration;
using MEC;
using NetworkManagerUtils.Dummies;
using PlayerRoles;
using PlayerRoles.FirstPersonControl;
using PlaytestHarness.Core;
using PlaytestHarness.Movement;
using PlaytestHarness.Probes;
using PlaytestHarness.Telemetry;
using RelativePositioning;
using UnityEngine;

namespace PlaytestHarness.Actors;

/// <summary>
/// Tier fulfillment for spawn/travel/settle/soak verbs. All placement mechanics live here
/// (batteries included): quick = raw TP, standard = fast-travel (probe-validated TP + settle),
/// EndToEnd = provider walk. Scenario code only sees one-liner verbs on <see cref="Actor"/>.
/// </summary>
internal static class MovementFulfiller
{
    private const float RoleAssignTimeout = 8f;
    private const float SoakAirborneLimitSeconds = 4f;
    private const float SoakSinkLimitMeters = 5f;

    /// <summary>Sentinel for "use the configured default" on verb parameters.</summary>
    internal const float ConfigDefault = -1f;

    // ---- spawn -----------------------------------------------------------------------------

    internal static Actor CreateActor(
        ScenarioContext ctx,
        string label,
        RoleTypeId role,
        SpawnSpec spec,
        bool useMovementProvider = true)
    {
        string nickname = ctx.Registry.NicknamePrefix + label;

        // Walk-capable actors normally use the movement provider. A scenario can deliberately request
        // a plain RA dummy when it needs the game's own ReceivedPosition/FpcMotor input path instead.
        ReferenceHub? hub = null;
        bool viaProvider = useMovementProvider && ctx.Movement.Available &&
                           ctx.Movement.TrySpawnActor(nickname, role, out hub);
        if (!viaProvider)
        {
            hub = DummyUtils.SpawnDummy(nickname);
        }

        if (hub == null)
        {
            throw new RequireException($"failed to spawn actor dummy '{label}' (provider={ctx.Movement.Name}, viaProvider={viaProvider})");
        }

        ctx.Registry.MarkExpectedRole(hub, role);
        Actor actor = new(ctx, hub, label, role, spec);
        ctx.Registry.Track(actor);

        if (!viaProvider)
        {
            // Fresh dummies spawn role=None — the role is set in CompleteSpawn AFTER one frame:
            // PlayerAuthenticationManager.Start (Unity lifecycle, end of spawn frame) assigns
            // UserId="ID_Dummy", and a same-frame ServerSetRole reaches keycard loadout code
            // (SerialNumberDetail.GetNumberForPlayer) with UserId still null => ArgumentNullException.
            actor.NeedsDeferredRoleSet = true;
        }
        else
        {
            // Park the provider-controlled bot immediately: without an order state the bot AI would
            // roam/fight on its own, wrecking determinism. Cancel == "hold position".
            ctx.Movement.Cancel(hub);
        }

        EventLog.Line("STEP", $"actor spawned label={label} role={role} spec={spec} provider={(viaProvider ? ctx.Movement.Name : "none")}");
        ctx.Emit(new TelemetryEvent("actor_spawn", label, new Dictionary<string, object?>
        {
            ["actor"] = label,
            ["role"] = role.ToString(),
            ["spec"] = spec.ToString(),
            ["via_provider"] = viaProvider,
        }));
        return actor;
    }

    /// <summary>Completes the spawn: role assigned, placement per spec + tier, settled.</summary>
    internal static IEnumerator<float> CompleteSpawn(ScenarioContext ctx, Actor actor)
    {
        if (actor.NeedsDeferredRoleSet)
        {
            // One frame so PlayerAuthenticationManager.Start assigned UserId (see CreateActor note).
            actor.NeedsDeferredRoleSet = false;
            yield return Timing.WaitForOneFrame;
            actor.Hub.roleManager.ServerSetRole(actor.RequestedRole, RoleChangeReason.RemoteAdmin, RoleSpawnFlags.All);
        }

        double deadline = ctx.ElapsedSeconds + RoleAssignTimeout;
        while (actor.Role != actor.RequestedRole)
        {
            if (ctx.ElapsedSeconds >= deadline)
            {
                throw new RequireException(
                    $"actor {actor.Label} never received role {actor.RequestedRole} (still {actor.Role} after {RoleAssignTimeout:0.#}s)");
            }

            yield return Timing.WaitForOneFrame;
        }

        // Let the FPC module/capsule initialize before any placement math.
        yield return Timing.WaitForSeconds(0.25f);

        if (actor.Spec.Kind == SpawnSpec.SpecKind.AtPosition)
        {
            foreach (float wait in Coroutines.Pump(PlaceAt(ctx, actor, actor.Spec.Position, actor.Spec.Unvalidated, "spawn-placement")))
            {
                yield return wait;
            }
        }
        else if (actor.Spec.Kind == SpawnSpec.SpecKind.NativeRole)
        {
            foreach (float wait in Coroutines.Pump(Settle(ctx, actor, ConfigDefault, ConfigDefault)))
            {
                yield return wait;
            }
        }
        else
        {
            // A provider-spawned bot can schedule a final requested-role correction shortly after
            // creation. Let that native/provider spawn churn drain before arming the watchdog and
            // handing the candidate to a feature; otherwise a delayed Spectator correction can land
            // after the feature already changed it to Tutorial and look like a role stomp.
            yield return Timing.WaitForSeconds(1.25f);
            if (actor.Role != actor.RequestedRole)
            {
                throw new RequireException(
                    $"candidate {actor.Label} did not stabilize as {actor.RequestedRole} (found {actor.Role})");
            }

            // A selectable candidate is intentionally non-spatial (normally Spectator). Do not run
            // the vacuous non-FPC grounded check; the feature transition must create spatial truth.
            EventLog.Line("STEP", $"candidate ready label={actor.Label} role={actor.Role} (non-spatial; awaiting feature transition)");
        }

        // Spawn complete: consume the expected-role mark so ANY further role set — even to the
        // same role — is a watchdog violation (external role-stomp detection).
        ctx.Registry.ClearExpectedRole(actor.Hub);
        actor.IsReady = true;
        // Seed + attach synchronous teleport monitoring at the readiness boundary, before scenario
        // code resumes from WaitReady. This closes the immediate-first-snap blind spot (PT-006).
        ctx.Registry.NotifyReady(actor);
        ctx.Emit(new TelemetryEvent("actor_ready", actor.Label, new Dictionary<string, object?>
        {
            ["actor"] = actor.Label,
            ["pos"] = PosArray(actor.Position),
            ["room"] = actor.RoomName,
        }));
        EventLog.Line("STEP", $"actor ready label={actor.Label} role={actor.Role} pos={PlacementProbes.Format(actor.Position)} room={actor.RoomName}");
    }

    internal static IEnumerator<float> ChangeRole(ScenarioContext ctx, Actor actor, RoleTypeId role)
    {
        if (ctx.Fidelity == Fidelity.EndToEnd)
        {
            throw new InvalidOperationException("ChangeRole is forbidden at EndToEnd.");
        }

        if (ctx.Fidelity == Fidelity.Standard && !ctx.ArrangementActive)
        {
            throw new InvalidOperationException(
                "ChangeRole at Standard must be created inside ctx.Arrange(reason, ...).");
        }

        return ChangeRoleBody(ctx, actor, role);
    }

    private static IEnumerator<float> ChangeRoleBody(ScenarioContext ctx, Actor actor, RoleTypeId role)
    {
        RoleTypeId before = actor.Role;
        if (before == role)
        {
            throw new RequireException($"ChangeRole: {actor.Label} is already {role}");
        }

        ctx.Registry.MarkExpectedRoleMove(actor.Hub, role);
        try
        {
            ctx.Registry.MarkExpectedRole(actor.Hub, role);
            try
            {
                actor.Hub.roleManager.ServerSetRole(role, RoleChangeReason.RemoteAdmin, RoleSpawnFlags.None);
            }
            finally
            {
                // ServerSetRole is synchronous. Close the one-call authorization before any wait so an
                // external same-role reset cannot hide behind the harness's expected-role mark.
                ctx.Registry.ClearExpectedRole(actor.Hub);
            }

            double deadline = ctx.ElapsedSeconds + RoleAssignTimeout;
            while (actor.Role != role)
            {
                if (ctx.ElapsedSeconds >= deadline)
                {
                    throw new RequireException(
                        $"ChangeRole: {actor.Label} never changed from {before} to {role}");
                }

                yield return Timing.WaitForOneFrame;
            }

            yield return Timing.WaitForSeconds(0.25f);
            ctx.Movement.Cancel(actor.Hub);
            ctx.Emit(new TelemetryEvent("actor_role_arranged", actor.Label, new Dictionary<string, object?>
            {
                ["actor"] = actor.Label,
                ["from"] = before.ToString(),
                ["to"] = role.ToString(),
            }));
            EventLog.Line("STEP", $"arranged native role change actor={actor.Label} {before}->{role} flags=None");
        }
        finally
        {
            ctx.Registry.ClearExpectedRoleMove(actor.Hub);
        }
    }

    // ---- placement primitives ---------------------------------------------------------------

    /// <summary>
    /// Raw TP (quick tier / validated fast-travel's final move). Marked as an ordered move; the
    /// mark is consumed synchronously by TeleportMonitor's override hook inside this very call
    /// (PT-007). A failed override cancels the mark so it can never excuse a later jump.
    /// </summary>
    private static void RawTeleport(ScenarioContext ctx, Actor actor, Vector3 target, string what)
    {
        ctx.Registry.MarkOrderedMove(actor.Hub);
        if (!actor.Hub.TryOverridePosition(target))
        {
            ctx.Registry.ClearOrderedMove(actor.Hub);
            throw new RequireException($"{what}: TryOverridePosition failed for {actor.Label} (role {actor.Role} not FPC?)");
        }
    }

    /// <summary>
    /// Placement with tier semantics: quick = raw TP; standard = FAST-TRAVEL (pre-validate ground/
    /// bounds/capsule, TP to the ground-snapped point, settle; invalid target = loud FAIL naming the
    /// failed probe). Unvalidated opt-in skips probes but is logged loudly.
    /// </summary>
    private static IEnumerator<float> PlaceAt(ScenarioContext ctx, Actor actor, Vector3 target, bool unvalidated, string what)
    {
        if (ctx.Fidelity == Fidelity.Quick)
        {
            RawTeleport(ctx, actor, target, what);
            yield return Timing.WaitForSeconds(0.2f);
            yield break;
        }

        if (unvalidated)
        {
            EventLog.Line("PROBE", $"{what}: actor={actor.Label} UNVALIDATED placement to {PlacementProbes.Format(target)} (explicit opt-out, probes skipped)", error: false);
            ctx.Emit(new TelemetryEvent("unvalidated_tp", what, new Dictionary<string, object?> { ["actor"] = actor.Label, ["pos"] = PosArray(target) }));
            RawTeleport(ctx, actor, target, what);
            yield return Timing.WaitForSeconds(0.2f);
            yield break;
        }

        // FAST-TRAVEL: probe first, then TP, then settle. Invalid target leaves the actor at its
        // old position (probe rejection throws BEFORE any mutation).
        float maxDrop = ctx.Config.FastTravelMaxDropMeters;
        CharacterControllerSettingsPreset settings = actor.CapsuleSettings;
        // ignoreHub: the actor's own capsule never obstructs its own placement; every OTHER
        // player's capsule does (PT-009 — no teleporting actors into occupied space).
        ProbeResult result = PlacementProbes.ValidateTeleportTarget(target, settings, maxDrop, actor.Hub);
        ctx.Emit(new TelemetryEvent("fast_travel_probe", result.ToString(), new Dictionary<string, object?>
        {
            ["actor"] = actor.Label,
            ["target"] = PosArray(target),
            ["ok"] = result.Ok,
            ["probe"] = result.FailedProbe,
        }));
        if (!result.Ok)
        {
            throw new RequireException(
                $"{what}: fast-travel target {PlacementProbes.Format(target)} REJECTED by probe '{result.FailedProbe}': {result.Detail}");
        }

        PlacementProbes.TryFindGround(target, maxDrop, out float groundY, out string groundName);
        Vector3 snapped = new(target.x, groundY + PlacementProbes.ExpectedRootHeight(settings), target.z);
        EventLog.Line("PROBE", $"{what}: actor={actor.Label} fast-travel validated -> {PlacementProbes.Format(snapped)} (ground='{groundName}')");
        RawTeleport(ctx, actor, snapped, what);

        foreach (float wait in Coroutines.Pump(Settle(ctx, actor, ConfigDefault, ConfigDefault)))
        {
            yield return wait;
        }
    }

    // ---- travel verbs -----------------------------------------------------------------------

    internal static IEnumerator<float> GoTo(ScenarioContext ctx, Actor actor, Vector3 target)
    {
        if (ctx.Fidelity == Fidelity.EndToEnd)
        {
            // e2e: GoTo == WalkTo — no TP verb exists at this tier.
            foreach (float wait in Coroutines.Pump(WalkBody(ctx, actor, target, "GoTo(e2e)")))
            {
                yield return wait;
            }

            yield break;
        }

        foreach (float wait in Coroutines.Pump(PlaceAt(ctx, actor, target, unvalidated: false, what: "GoTo")))
        {
            yield return wait;
        }
    }

    internal static IEnumerator<float> GoToRoom(ScenarioContext ctx, Actor actor, RoomName room)
    {
        Vector3 safePoint = ResolveSafePointInRoom(ctx, actor, room);
        foreach (float wait in Coroutines.Pump(GoTo(ctx, actor, safePoint)))
        {
            yield return wait;
        }
    }

    internal static IEnumerator<float> WalkTo(ScenarioContext ctx, Actor actor, Vector3 target)
    {
        if (ctx.Fidelity == Fidelity.Quick)
        {
            // Quick may TP.
            RawTeleport(ctx, actor, target, "WalkTo(quick)");
            yield return Timing.WaitForSeconds(0.2f);
            yield break;
        }

        foreach (float wait in Coroutines.Pump(WalkBody(ctx, actor, target, "WalkTo")))
        {
            yield return wait;
        }
    }

    internal static IEnumerator<float> WalkRoute(
        ScenarioContext ctx,
        Actor actor,
        IReadOnlyList<Vector3> waypoints)
    {
        if (waypoints == null || waypoints.Count == 0)
        {
            throw new ArgumentException("WalkRoute requires at least one waypoint.", nameof(waypoints));
        }

        for (int i = 0; i < waypoints.Count; i++)
        {
            Vector3 waypoint = waypoints[i];
            EventLog.Line("STEP",
                $"walk route actor={actor.Label} waypoint={i + 1}/{waypoints.Count} target={PlacementProbes.Format(waypoint)}");
            foreach (float wait in Coroutines.Pump(WalkTo(ctx, actor, waypoint)))
            {
                yield return wait;
            }
        }
    }

    internal static IEnumerator<float> WalkBeyondBoundary(
        ScenarioContext ctx,
        Actor actor,
        Vector3 center,
        float radius,
        float doorMargin)
    {
        if (radius <= 0f || doorMargin <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(radius),
                "WalkBeyondBoundary radius and door margin must be positive.");
        }

        RequireWalkCapable(ctx, actor, "WalkBeyondBoundary");
        if (!RoomUtils.TryGetRoom(actor.Position, out RoomIdentifier room) || room == null ||
            !DoorVariant.DoorsByRoom.TryGetValue(room, out HashSet<DoorVariant> roomDoors))
        {
            throw new RequireException(
                $"WalkBeyondBoundary: no room-connected doors resolve at {PlacementProbes.Format(actor.Position)} for {actor.Label}");
        }

        List<(DoorVariant Door, Vector3 Target, float RouteDistance)> candidates = new();
        foreach (DoorVariant door in roomDoors.Where(candidate => candidate != null))
        {
            Vector3 doorPosition = door.transform.position;
            Vector3 forward = Vector3.ProjectOnPlane(door.transform.forward, Vector3.up).normalized;
            if (forward.sqrMagnitude < 0.01f)
            {
                continue;
            }

            Vector3 a = doorPosition + (forward * doorMargin);
            Vector3 b = doorPosition - (forward * doorMargin);
            Vector3 target = HorizontalDistance(a, center) >= HorizontalDistance(b, center) ? a : b;
            Vector3 outward = target - center;
            outward.y = 0f;
            if (outward.sqrMagnitude < 0.01f)
            {
                continue;
            }

            float targetDistance = HorizontalDistance(target, center);
            if (targetDistance <= radius + 0.5f)
            {
                target += outward.normalized * (radius + 0.75f - targetDistance);
            }

            target.y = actor.Position.y;
            candidates.Add((door, target, Vector3.Distance(actor.Position, doorPosition)));
        }

        if (candidates.Count == 0)
        {
            throw new RequireException(
                $"WalkBeyondBoundary: room {room.Name} exposes no usable connected-door candidates for {actor.Label}");
        }

        List<string> failures = new();
        foreach ((DoorVariant door, Vector3 requested, _) in candidates.OrderBy(candidate => candidate.RouteDistance))
        {
            Vector3 target = SnapWalkTargetToStandingRoot(ctx, actor, requested);
            string doorLabel = string.IsNullOrWhiteSpace(door.DoorName) ? door.RequesterLogSignature : door.DoorName;
            if (!ctx.Movement.OrderWalk(actor.Hub, target))
            {
                MovementStatus rejected = ctx.Movement.Status(actor.Hub);
                failures.Add($"{doorLabel} rejected {rejected.State}:{rejected.Reason}");
                EventLog.Line("PROBE",
                    $"boundary door route rejected actor={actor.Label} door='{doorLabel}' target={PlacementProbes.Format(target)} state={rejected.State} reason='{rejected.Reason}'");
                continue;
            }

            double started = ctx.ElapsedSeconds;
            float timeout = WalkTimeout(ctx, actor.Position, target);
            EventLog.Line("STEP",
                $"boundary door route actor={actor.Label} door='{doorLabel}' target={PlacementProbes.Format(target)} radius={radius:0.##}m timeout={timeout:0.#}s");
            while (true)
            {
                float distance = HorizontalDistance(actor.Position, center);
                if (distance > radius)
                {
                    ctx.Movement.Cancel(actor.Hub);
                    foreach (float wait in Coroutines.Pump(Settle(ctx, actor, -1f, -1f)))
                    {
                        yield return wait;
                    }

                    distance = HorizontalDistance(actor.Position, center);
                    if (distance <= radius)
                    {
                        failures.Add($"{doorLabel} receded inside during settle ({distance:0.##}m)");
                        break;
                    }

                    ctx.Emit(new TelemetryEvent("boundary_crossing", "walked through connected door", new Dictionary<string, object?>
                    {
                        ["actor"] = actor.Label,
                        ["door"] = doorLabel,
                        ["position"] = PosArray(actor.Position),
                        ["center"] = PosArray(center),
                        ["radius_m"] = radius,
                        ["distance_m"] = Math.Round(distance, 3),
                        ["elapsed_s"] = Math.Round(ctx.ElapsedSeconds - started, 2),
                    }));
                    EventLog.Line("STEP",
                        $"boundary crossed actor={actor.Label} door='{doorLabel}' distance={distance:0.##}m radius={radius:0.##}m pos={PlacementProbes.Format(actor.Position)}");
                    yield break;
                }

                MovementStatus status = ctx.Movement.Status(actor.Hub);
                double elapsed = ctx.ElapsedSeconds - started;
                if (status.State is MovementState.Stalled or MovementState.OffMesh or MovementState.None ||
                    elapsed >= timeout)
                {
                    failures.Add($"{doorLabel} {status.State}:{status.Reason} remaining={status.DistanceRemaining:0.##}");
                    ctx.Movement.Cancel(actor.Hub);
                    break;
                }

                yield return Timing.WaitForSeconds(0.2f);
            }

            yield return Timing.WaitForOneFrame;
        }

        throw new RequireException(
            $"WalkBeyondBoundary: no connected-door route crossed radius {radius:0.##}m for {actor.Label}; " +
            $"tried {candidates.Count} candidate(s) ({string.Join(" | ", failures.Take(5))})");
    }

    internal static IEnumerator<float> WalkToRoom(ScenarioContext ctx, Actor actor, RoomName room)
    {
        if (ctx.Fidelity == Fidelity.Quick)
        {
            foreach (float wait in Coroutines.Pump(GoToRoom(ctx, actor, room)))
            {
                yield return wait;
            }

            yield break;
        }

        RequireWalkCapable(ctx, actor, "WalkTo(room)");
        if (!ctx.Movement.OrderWalkToRoom(actor.Hub, room))
        {
            MovementStatus failed = ctx.Movement.Status(actor.Hub);
            throw new RequireException(
                $"WalkTo(room): provider rejected walk order to {room} for {actor.Label}: state={failed.State} reason='{failed.Reason}'");
        }

        foreach (float wait in Coroutines.Pump(PollWalk(ctx, actor, $"room {room}", WalkTimeout(ctx, actor.Position, RoomCenter(room)))))
        {
            yield return wait;
        }

        // Room verification lives in the verb (batteries included). The provider's arrival goal is
        // the room's nearest mesh cell, which can sit at the doorway threshold where TryGetRoom
        // resolves to the neighboring hallway — accept in-room OR just-outside-the-bounds arrival,
        // fail loudly on anything farther.
        string arrivedRoom = PlacementProbes.RoomNameAt(actor.Position);
        if (arrivedRoom != room.ToString())
        {
            RoomIdentifier? identifier = RoomIdentifier.AllRoomIdentifiers.FirstOrDefault(r => r != null && r.Name == room);
            UnityEngine.Bounds bounds = identifier != null ? identifier.WorldspaceBounds : default;
            bounds.Expand(8f);
            if (identifier == null || !bounds.Contains(actor.Position))
            {
                throw new RequireException(
                    $"WalkTo(room): {actor.Label} arrived at {PlacementProbes.Format(actor.Position)} (room={arrivedRoom}) which is not in or adjacent to {room}");
            }

            EventLog.Line("STEP", $"walk arrived at {room}'s doorway threshold (TryGetRoom says {arrivedRoom}); within expanded room bounds - accepted");
        }
    }

    private static IEnumerator<float> WalkBody(ScenarioContext ctx, Actor actor, Vector3 target, string what)
    {
        RequireWalkCapable(ctx, actor, what);
        Vector3 walkTarget = SnapWalkTargetToStandingRoot(ctx, actor, target);
        if (!ctx.Movement.OrderWalk(actor.Hub, walkTarget))
        {
            MovementStatus failed = ctx.Movement.Status(actor.Hub);
            float directDistance = HorizontalDistance(actor.Position, walkTarget);
            if (failed.State == MovementState.OffMesh && directDistance <= 25f)
            {
                EventLog.Line("STEP",
                    $"{what}: navmesh rejected local target {PlacementProbes.Format(walkTarget)}; using native straight-line FPC fallback ({directDistance:0.##}m)");
                foreach (float wait in Coroutines.Pump(WalkDirectLocal(ctx, actor, walkTarget, WalkTimeout(ctx, actor.Position, walkTarget))))
                {
                    yield return wait;
                }
                yield break;
            }

            throw new RequireException(
                $"{what}: provider rejected walk order to {PlacementProbes.Format(walkTarget)} for {actor.Label}: state={failed.State} reason='{failed.Reason}'");
        }

        foreach (float wait in Coroutines.Pump(PollWalk(
            ctx,
            actor,
            PlacementProbes.Format(walkTarget),
            WalkTimeout(ctx, actor.Position, walkTarget),
            walkTarget)))
        {
            yield return wait;
        }
    }

    private static Vector3 SnapWalkTargetToStandingRoot(ScenarioContext ctx, Actor actor, Vector3 target)
    {
        float probeDrop = Mathf.Max(3f, ctx.Config.FastTravelMaxDropMeters);
        if (!PlacementProbes.TryFindGround(target, probeDrop, out float groundY, out string groundName))
        {
            return target;
        }

        Vector3 snapped = new(target.x,
            groundY + PlacementProbes.ExpectedRootHeight(actor.CapsuleSettings), target.z);
        if (Vector3.Distance(snapped, target) > 0.05f)
        {
            EventLog.Line("PROBE",
                $"walk target ground-snapped {PlacementProbes.Format(target)} -> {PlacementProbes.Format(snapped)} ground='{groundName}' actor={actor.Label}");
            ctx.Emit(new TelemetryEvent("walk_target_snap", null, new Dictionary<string, object?>
            {
                ["actor"] = actor.Label,
                ["requested"] = PosArray(target),
                ["snapped"] = PosArray(snapped),
                ["kind"] = "ground",
                ["ground"] = groundName,
            }));
        }

        return snapped;
    }

    internal static IEnumerator<float> MeasureWalkingSpeed(
        ScenarioContext ctx,
        Actor actor,
        Vector3 worldDirection,
        float durationSeconds,
        bool allowDirectionFallback)
    {
        if (durationSeconds <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(durationSeconds));
        }

        Vector3 direction = Vector3.ProjectOnPlane(worldDirection, Vector3.up);
        if (direction.sqrMagnitude < 1e-6f)
        {
            throw new ArgumentException("MeasureWalkingSpeed requires a non-zero horizontal direction.", nameof(worldDirection));
        }

        RequireFpc(actor, "MeasureWalkingSpeed");
        actor.LastMovementMeasurement = null;
        direction.Normalize();

        foreach (float wait in Coroutines.Pump(Settle(ctx, actor, -1f, -1f)))
        {
            yield return wait;
        }

        float laneDistance = Mathf.Clamp(durationSeconds * 7f, 2f, 12f);
        if (allowDirectionFallback)
        {
            direction = ResolveClearMeasurementDirection(ctx, actor, direction, laneDistance);
        }
        else if (TryDescribeMeasurementLaneBlock(actor, direction, laneDistance, out string blockDetail))
        {
            throw new RequireException(
                $"MeasureWalkingSpeed: fixed comparison lane is no longer clear for {actor.Label}: {blockDetail}");
        }

        bool directNativeMotor = !ctx.Movement.Controls(actor.Hub);
        if (!directNativeMotor)
        {
            ctx.Movement.Cancel(actor.Hub);
        }

        foreach (float wait in Coroutines.Pump(CombatFulfiller.LookAt(ctx, actor, actor.Position + (direction * 12f))))
        {
            yield return wait;
        }

        // Soft re-check after the yielding Settle/LookAt above: an external role change in that
        // window must surface as a Require FAIL (plus the watchdog violation), not an
        // InvalidCastException-shaped ERROR.
        RequireFpc(actor, "MeasureWalkingSpeed");
        IFpcRole fpc = (IFpcRole)actor.Hub.roleManager!.CurrentRole;
        FpcMotor motor = fpc.FpcModule.Motor;
        Vector3 start = actor.Position;
        Vector3 previous = start;
        float pathDistance = 0f;
        float simulationElapsed = 0f;
        double started = ctx.ElapsedSeconds;
        double lastGrounded = started;
        try
        {
            while (simulationElapsed < durationSeconds)
            {
                if (directNativeMotor)
                {
                    // Same native dummy path proven by toy-tricks-demo: feed one nearby position per
                    // tick into FpcMotor. A distant target is rejected as desync; the small step lets
                    // the real motor own speed, effects, animation, gravity, collision, and step-up.
                    Vector3 current = actor.Position;
                    Vector3 received = current + (direction * 0.45f);
                    received.y = current.y;
                    motor.ReceivedPosition = new RelativePosition(received);
                }
                else if (!ctx.Movement.DriveLocal(actor.Hub, direction))
                {
                    throw new RequireException(
                        $"MeasureWalkingSpeed: native local input unavailable for provider-controlled actor {actor.Label}");
                }

                yield return Timing.WaitForOneFrame;
                simulationElapsed += Mathf.Max(0.0001f, Time.deltaTime);

                Vector3 currentPosition = actor.Position;
                pathDistance += Vector3.ProjectOnPlane(currentPosition - previous, Vector3.up).magnitude;
                previous = currentPosition;

                if (actor.Hub.IsGrounded())
                {
                    lastGrounded = ctx.ElapsedSeconds;
                }
                else if (ctx.ElapsedSeconds - lastGrounded >= 0.75f)
                {
                    throw new RequireException(
                        $"MeasureWalkingSpeed: {actor.Label} remained airborne for {ctx.ElapsedSeconds - lastGrounded:0.##}s at {PlacementProbes.Format(currentPosition)}");
                }

                if (currentPosition.y < start.y - 3f ||
                    !PlacementProbes.TryFindGround(currentPosition, 3f, out _, out _))
                {
                    throw new RequireException(
                        $"MeasureWalkingSpeed: ground probe failed for {actor.Label} at {PlacementProbes.Format(currentPosition)} (startY={start.y:0.##})");
                }
            }
        }
        finally
        {
            if (directNativeMotor)
            {
                motor.ReceivedPosition = new RelativePosition(actor.Position);
            }
            else
            {
                ctx.Movement.StopLocal(actor.Hub);
            }
        }

        Vector3 end = actor.Position;
        float elapsed = Mathf.Max(0.001f, simulationElapsed);
        float wallElapsed = Mathf.Max(0.001f, (float)(ctx.ElapsedSeconds - started));
        float projected = Vector3.Dot(Vector3.ProjectOnPlane(end - start, Vector3.up), direction);
        if (projected < -0.05f)
        {
            throw new RequireException(
                $"MeasureWalkingSpeed: {actor.Label} moved backwards {projected:0.###}m during forward input");
        }

        if (pathDistance > 0.25f && projected < pathDistance * 0.65f)
        {
            throw new RequireException(
                $"MeasureWalkingSpeed: {actor.Label} path was dominated by sideways motion (projected={projected:0.###}m path={pathDistance:0.###}m)");
        }

        string inputPath = directNativeMotor ? "fpc-received-position" : "movement-provider-desired-move";
        MovementMeasurement measurement = new(start, end, direction, elapsed, projected, pathDistance, inputPath);
        actor.LastMovementMeasurement = measurement;
        ctx.Emit(new TelemetryEvent("movement_measurement", null, new Dictionary<string, object?>
        {
            ["actor"] = actor.Label,
            ["elapsed_s"] = Math.Round(elapsed, 3),
            ["wall_elapsed_s"] = Math.Round(wallElapsed, 3),
            ["projected_distance_m"] = Math.Round(projected, 3),
            ["path_distance_m"] = Math.Round(pathDistance, 3),
            ["average_projected_speed_mps"] = Math.Round(measurement.AverageProjectedSpeed, 3),
            ["input_path"] = inputPath,
            ["direction"] = PosArray(direction),
            ["start"] = PosArray(start),
            ["end"] = PosArray(end),
        }));
        EventLog.Line("STEP",
            $"native walking measured actor={actor.Label} input={(directNativeMotor ? "FpcMotor.ReceivedPosition" : "provider DesiredMove")} simulation={elapsed:0.###}s wall={wallElapsed:0.###}s projected={projected:0.###}m path={pathDistance:0.###}m avg={measurement.AverageProjectedSpeed:0.###}m/s");
    }

    private static Vector3 ResolveClearMeasurementDirection(
        ScenarioContext ctx,
        Actor actor,
        Vector3 requested,
        float distance)
    {
        float[] angles = { 0f, 45f, -45f, 90f, -90f, 135f, -135f, 180f };
        List<string> blocked = new();
        foreach (float angle in angles)
        {
            Vector3 candidate = Quaternion.AngleAxis(angle, Vector3.up) * requested;
            candidate.y = 0f;
            candidate.Normalize();
            if (!TryDescribeMeasurementLaneBlock(actor, candidate, distance, out string detail))
            {
                if (Mathf.Abs(angle) > 0.01f)
                {
                    EventLog.Line("PROBE",
                        $"MeasureWalkingSpeed rerouted actor={actor.Label} angle={angle:0.#}deg direction={PlacementProbes.Format(candidate)} because requested lane was blocked");
                    ctx.Emit(new TelemetryEvent("movement_measurement_lane", "rerouted to clear lane",
                        new Dictionary<string, object?>
                        {
                            ["actor"] = actor.Label,
                            ["angle_degrees"] = angle,
                            ["direction"] = PosArray(candidate),
                            ["distance_m"] = distance,
                        }));
                }

                return candidate;
            }

            blocked.Add($"{angle:0.#}deg:{detail}");
        }

        throw new RequireException(
            $"MeasureWalkingSpeed: no clear {distance:0.##}m lane for {actor.Label}; " +
            string.Join(" | ", blocked));
    }

    private static bool TryDescribeMeasurementLaneBlock(
        Actor actor,
        Vector3 direction,
        float distance,
        out string detail)
    {
        Vector3 root = actor.Position;
        float[] heights = { 0.35f, 0.95f, 1.55f };
        foreach (float height in heights)
        {
            Vector3 origin = root + (Vector3.up * height);
            foreach (RaycastHit hit in Physics.RaycastAll(
                         origin,
                         direction,
                         distance,
                         FpcStateProcessor.Mask,
                         QueryTriggerInteraction.Ignore)
                     .OrderBy(candidate => candidate.distance))
            {
                if (hit.collider == null)
                {
                    continue;
                }

                ReferenceHub? hitHub = hit.collider.GetComponentInParent<ReferenceHub>();
                if (hitHub == actor.Hub)
                {
                    continue;
                }

                detail = $"{hit.distance:0.##}m/{hit.collider.name}/h{height:0.##}";
                return true;
            }
        }

        detail = string.Empty;
        return false;
    }

    private static IEnumerator<float> WalkDirectLocal(
        ScenarioContext ctx,
        Actor actor,
        Vector3 target,
        float timeoutSeconds)
    {
        ctx.Movement.Cancel(actor.Hub);
        foreach (float wait in Coroutines.Pump(CombatFulfiller.LookAt(ctx, actor, target)))
        {
            yield return wait;
        }

        double started = ctx.ElapsedSeconds;
        double lastProgress = started;
        double lastGrounded = started;
        float startY = actor.Position.y;
        float best = HorizontalDistance(actor.Position, target);
        try
        {
            while (true)
            {
                Vector3 delta = target - actor.Position;
                delta.y = 0f;
                float remaining = delta.magnitude;
                if (remaining <= 0.6f)
                {
                    ctx.Emit(new TelemetryEvent("order_result", "native local walk arrived", new Dictionary<string, object?>
                    {
                        ["actor"] = actor.Label,
                        ["ok"] = true,
                        ["fallback"] = "straight_fpc",
                        ["pos"] = PosArray(actor.Position),
                        ["room"] = actor.RoomName,
                        ["elapsed_s"] = System.Math.Round(ctx.ElapsedSeconds - started, 2),
                    }));
                    EventLog.Line("STEP",
                        $"native local walk arrived actor={actor.Label} target={PlacementProbes.Format(target)} elapsed={ctx.ElapsedSeconds - started:0.#}s pos={PlacementProbes.Format(actor.Position)}");
                    break;
                }

                if (remaining < best - 0.05f)
                {
                    best = remaining;
                    lastProgress = ctx.ElapsedSeconds;
                }

                if (actor.Hub.IsGrounded())
                {
                    lastGrounded = ctx.ElapsedSeconds;
                }
                else if (ctx.ElapsedSeconds - lastGrounded >= 1.5f)
                {
                    throw new RequireException(
                        $"native local walk lost ground for {ctx.ElapsedSeconds - lastGrounded:0.##}s for {actor.Label} at {PlacementProbes.Format(actor.Position)}");
                }

                if (actor.Position.y < startY - 3f ||
                    !PlacementProbes.TryFindGround(actor.Position, 3f, out _, out _))
                {
                    throw new RequireException(
                        $"native local walk ground probe failed for {actor.Label} at {PlacementProbes.Format(actor.Position)} (startY={startY:0.##})");
                }

                if (ctx.ElapsedSeconds - started >= timeoutSeconds)
                {
                    throw new RequireException(
                        $"native local walk to {PlacementProbes.Format(target)} TIMED OUT for {actor.Label} after {timeoutSeconds:0.#}s; remaining={remaining:0.##}m");
                }

                if (ctx.ElapsedSeconds - lastProgress >= 3f)
                {
                    throw new RequireException(
                        $"native local walk to {PlacementProbes.Format(target)} STALLED for {actor.Label}; no progress for 3s, remaining={remaining:0.##}m");
                }

                if (!ctx.Movement.DriveLocal(actor.Hub, delta))
                {
                    throw new RequireException(
                        $"native local walk input unavailable for provider-controlled actor {actor.Label}");
                }

                yield return Timing.WaitForOneFrame;
            }
        }
        finally
        {
            ctx.Movement.StopLocal(actor.Hub);
        }

        foreach (float wait in Coroutines.Pump(Settle(ctx, actor, -1f, -1f)))
        {
            yield return wait;
        }
    }

    private static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        a.y = 0f;
        b.y = 0f;
        return Vector3.Distance(a, b);
    }

    private static void RequireWalkCapable(ScenarioContext ctx, Actor actor, string what)
    {
        if (!ctx.Movement.Available)
        {
            // Provider absent => scenarios needing walks SKIP with explicit reason, never crash/hang.
            ctx.Skip($"{what} requires a movement provider, but none is available on this port (SCPSLBot.dll not loaded?)");
        }

        if (!ctx.Movement.Controls(actor.Hub))
        {
            throw new RequireException(
                $"{what}: actor {actor.Label} is not provider-controlled (provider bound after spawn?) — respawn the actor with the provider available");
        }
    }

    private static float WalkTimeout(ScenarioContext ctx, Vector3 from, Vector3 to)
    {
        float distance = Vector3.Distance(from, to);
        PlaytestConfig cfg = ctx.Config;
        return Mathf.Clamp(cfg.WalkTimeoutBaseSeconds + distance * cfg.WalkTimeoutPerMeter,
            cfg.WalkTimeoutBaseSeconds, cfg.WalkTimeoutMaxSeconds);
    }

    private static IEnumerator<float> PollWalk(
        ScenarioContext ctx,
        Actor actor,
        string targetLabel,
        float timeoutSeconds,
        Vector3? localFallbackTarget = null)
    {
        double start = ctx.ElapsedSeconds;
        EventLog.Line("STEP", $"walk start actor={actor.Label} target={targetLabel} timeout={timeoutSeconds:0.#}s");
        while (true)
        {
            MovementStatus status = ctx.Movement.Status(actor.Hub);
            double elapsed = ctx.ElapsedSeconds - start;
            switch (status.State)
            {
                case MovementState.Arrived:
                    ctx.Emit(new TelemetryEvent("order_result", "walk arrived", new Dictionary<string, object?>
                    {
                        ["actor"] = actor.Label,
                        ["ok"] = true,
                        ["pos"] = PosArray(actor.Position),
                        ["room"] = actor.RoomName,
                        ["elapsed_s"] = System.Math.Round(elapsed, 2),
                    }));
                    EventLog.Line("STEP", $"walk arrived actor={actor.Label} target={targetLabel} elapsed={elapsed:0.#}s pos={PlacementProbes.Format(actor.Position)} room={actor.RoomName}");
                    yield break;

                case MovementState.Stalled:
                case MovementState.OffMesh:
                case MovementState.None:
                    if (localFallbackTarget.HasValue)
                    {
                        float directDistance = HorizontalDistance(actor.Position, localFallbackTarget.Value);
                        if (directDistance <= 25f)
                        {
                            EventLog.Line("STEP",
                                $"walk provider failed locally state={status.State} reason='{status.Reason}'; using native straight-line FPC fallback to {targetLabel} ({directDistance:0.##}m)");
                            foreach (float wait in Coroutines.Pump(WalkDirectLocal(
                                ctx,
                                actor,
                                localFallbackTarget.Value,
                                WalkTimeout(ctx, actor.Position, localFallbackTarget.Value))))
                            {
                                yield return wait;
                            }

                            yield break;
                        }
                    }

                    EmitWalkFail(ctx, actor, status, elapsed);
                    throw new RequireException(
                        $"walk to {targetLabel} FAILED for {actor.Label}: state={status.State} reason='{status.Reason}' remaining={status.DistanceRemaining:0.##}m elapsed={elapsed:0.#}s");
            }

            if (elapsed >= timeoutSeconds)
            {
                ctx.Movement.Cancel(actor.Hub);
                EmitWalkFail(ctx, actor, status, elapsed);
                throw new RequireException(
                    $"walk to {targetLabel} TIMED OUT for {actor.Label} after {timeoutSeconds:0.#}s: state={status.State} reason='{status.Reason}' remaining={status.DistanceRemaining:0.##}m");
            }

            yield return Timing.WaitForSeconds(0.2f);
        }
    }

    private static void EmitWalkFail(ScenarioContext ctx, Actor actor, MovementStatus status, double elapsed)
    {
        ctx.Emit(new TelemetryEvent("order_result", "walk failed", new Dictionary<string, object?>
        {
            ["actor"] = actor.Label,
            ["ok"] = false,
            ["state"] = status.State.ToString(),
            ["reason"] = status.Reason,
            ["pos"] = PosArray(actor.Position),
            ["room"] = actor.RoomName,
            ["remaining_m"] = System.Math.Round(status.DistanceRemaining, 2),
            ["elapsed_s"] = System.Math.Round(elapsed, 2),
        }));
    }

    // ---- settle / soak ----------------------------------------------------------------------

    /// <summary>
    /// Spectator/non-FPC roles pass native IsGrounded vacuously (decompiled FpcExtensionMethods
    /// returns true for every non-FPC role) and have no spatial truth to assert — Settle/Soak on
    /// them would report success without observing anything (PT-011).
    /// </summary>
    private static void RequireFpc(Actor actor, string verb)
    {
        if (actor.Hub.roleManager?.CurrentRole is not IFpcRole)
        {
            throw new RequireException(
                $"{verb}: actor {actor.Label} has no FPC role ({actor.Role}) — native IsGrounded is vacuously true for non-FPC roles, the check would be meaningless");
        }

        if (actor.Health <= 0f)
        {
            throw new RequireException($"{verb}: actor {actor.Label} is not alive (health={actor.Health:0.##})");
        }
    }

    internal static IEnumerator<float> Settle(ScenarioContext ctx, Actor actor, float maxDrop, float timeoutSeconds)
    {
        RequireFpc(actor, "settle");
        if (maxDrop < 0f)
        {
            maxDrop = ctx.Config.SettleMaxDropMeters;
        }

        if (timeoutSeconds < 0f)
        {
            timeoutSeconds = ctx.Config.SettleTimeoutSeconds;
        }

        float startY = actor.Position.y;
        double deadline = ctx.ElapsedSeconds + timeoutSeconds;
        const float StableGroundedSeconds = 0.75f;
        while (true)
        {
            while (!actor.Hub.IsGrounded())
            {
                RequireFpc(actor, "settle");
                if (ctx.ElapsedSeconds >= deadline)
                {
                    throw new RequireException(
                        $"settle: actor {actor.Label} never stabilized on the ground within {timeoutSeconds:0.#}s (pos={PlacementProbes.Format(actor.Position)}, fell {startY - actor.Position.y:0.##}m)");
                }

                yield return Timing.WaitForOneFrame;
            }

            double stableUntil = ctx.ElapsedSeconds + StableGroundedSeconds;
            while (actor.Hub.IsGrounded() && ctx.ElapsedSeconds < stableUntil)
            {
                RequireFpc(actor, "settle");
                yield return Timing.WaitForOneFrame;
            }

            if (actor.Hub.IsGrounded())
            {
                break;
            }
        }

        float drop = startY - actor.Position.y;
        if (drop > maxDrop)
        {
            throw new RequireException(
                $"settle: actor {actor.Label} fell {drop:0.##}m (max {maxDrop:0.##}m) to {PlacementProbes.Format(actor.Position)} — bad placement (the mid-air-spawn bug class)");
        }

        ProbeResult ground = PlacementProbes.Ground(actor.Position, 3f);
        if (!ground.Ok)
        {
            throw new RequireException($"settle: actor {actor.Label} grounded but ground probe failed: {ground.Detail}");
        }

        ctx.Emit(new TelemetryEvent("settle", null, new Dictionary<string, object?>
        {
            ["actor"] = actor.Label,
            ["drop_m"] = System.Math.Round(drop, 3),
            ["pos"] = PosArray(actor.Position),
            ["room"] = actor.RoomName,
        }));
    }

    internal static IEnumerator<float> Soak(ScenarioContext ctx, Actor actor, float seconds)
    {
        RequireFpc(actor, "soak");
        float initialHealth = actor.Health;
        double end = ctx.ElapsedSeconds + seconds;
        double lastGroundedAt = ctx.ElapsedSeconds;
        float lastGroundedY = actor.Position.y;
        EventLog.Line("STEP", $"soak start actor={actor.Label} seconds={seconds:0.#} pos={PlacementProbes.Format(actor.Position)}");

        while (ctx.ElapsedSeconds < end)
        {
            // A soaked actor must STAY a live FPC role for the whole window (PT-011): dying or a
            // role change to Spectator would otherwise pass every remaining sample vacuously.
            RequireFpc(actor, "soak");
            if (actor.Health <= 0f)
            {
                throw new RequireException(
                    $"soak: actor {actor.Label} DIED during the soak (health {initialHealth:0.#} -> {actor.Health:0.#})");
            }

            Vector3 pos = actor.Position;
            ProbeResult bounds = PlacementProbes.Bounds(pos);
            if (!bounds.Ok)
            {
                throw new RequireException($"soak: actor {actor.Label} left room bounds at {PlacementProbes.Format(pos)}: {bounds.Detail}");
            }

            if (actor.Hub.IsGrounded())
            {
                lastGroundedAt = ctx.ElapsedSeconds;
                lastGroundedY = pos.y;
            }
            else if (ctx.ElapsedSeconds - lastGroundedAt > SoakAirborneLimitSeconds)
            {
                throw new RequireException(
                    $"soak: actor {actor.Label} airborne for more than {SoakAirborneLimitSeconds:0.#}s at {PlacementProbes.Format(pos)} — falling through the world?");
            }

            if (lastGroundedY - pos.y > SoakSinkLimitMeters)
            {
                throw new RequireException(
                    $"soak: actor {actor.Label} sank {lastGroundedY - pos.y:0.##}m below its grounded level to {PlacementProbes.Format(pos)}");
            }

            yield return Timing.WaitForSeconds(Mathf.Max(0.1f, ctx.Config.SoakSampleIntervalSeconds));
        }

        // End-state check (PT-011): a soak must END grounded — short soaks on a stationary
        // airborne actor (< airborne limit) otherwise succeed without the actor ever standing.
        RequireFpc(actor, "soak");
        if (!actor.Hub.IsGrounded())
        {
            throw new RequireException(
                $"soak: actor {actor.Label} ended the soak AIRBORNE at {PlacementProbes.Format(actor.Position)}");
        }

        ctx.Emit(new TelemetryEvent("soak", null, new Dictionary<string, object?>
        {
            ["actor"] = actor.Label,
            ["seconds"] = seconds,
            ["pos"] = PosArray(actor.Position),
            ["room"] = actor.RoomName,
        }));
        EventLog.Line("STEP", $"soak ok actor={actor.Label} seconds={seconds:0.#} pos={PlacementProbes.Format(actor.Position)} room={actor.RoomName}");
    }

    // ---- room resolution ---------------------------------------------------------------------

    private static Vector3 RoomCenter(RoomName room)
    {
        RoomIdentifier? identifier = RoomIdentifier.AllRoomIdentifiers.FirstOrDefault(r => r != null && r.Name == room);
        return identifier != null ? identifier.transform.position : Vector3.zero;
    }

    internal static Vector3 FindSafePointNear(
        ScenarioContext ctx,
        Actor actor,
        Vector3 center,
        float maxRadius)
    {
        if (maxRadius < 1f)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRadius));
        }

        CharacterControllerSettingsPreset settings = actor.CapsuleSettings;
        float[] radii = { 1.5f, 1.85f, 2.2f, maxRadius };
        List<string> failures = new();
        for (int ring = 0; ring < radii.Length; ring++)
        {
            float radius = Mathf.Min(maxRadius, radii[ring]);
            for (int step = 0; step < 12; step++)
            {
                float angle = (step * 30f) + (ring * 15f);
                Vector3 direction = Quaternion.AngleAxis(angle, Vector3.up) * Vector3.forward;
                Vector3 candidate = center + (direction * radius) + Vector3.up;
                bool occupied = ctx.Registry.Actors.Any(other => other != actor &&
                    other.Hub != null && other.Hub.gameObject != null &&
                    HorizontalDistance(other.Position, candidate) < 1f);
                if (occupied)
                {
                    failures.Add($"{PlacementProbes.Format(candidate)}:occupied");
                    continue;
                }

                ProbeResult probe = PlacementProbes.ValidateTeleportTarget(
                    candidate,
                    settings,
                    ctx.Config.FastTravelMaxDropMeters,
                    actor.Hub);
                if (probe.Ok)
                {
                    EventLog.Line("PROBE",
                        $"near-feature safe point center={PlacementProbes.Format(center)} radius={radius:0.##} angle={angle:0.#} -> {PlacementProbes.Format(candidate)} for {actor.Label}");
                    return candidate;
                }

                failures.Add($"{PlacementProbes.Format(candidate)}:{probe.FailedProbe}");
            }
        }

        throw new RequireException(
            $"no probed safe point within {maxRadius:0.##}m of {PlacementProbes.Format(center)} for {actor.Label} ({string.Join("; ", failures.Take(6))})");
    }

    /// <summary>
    /// Resolves a RoomName to a probed safe point inside that room: candidate offsets around the
    /// room origin are validated (ground/bounds/capsule) and the first passing point wins.
    /// </summary>
    internal static Vector3 ResolveSafePointInRoom(ScenarioContext ctx, Actor actor, RoomName room)
    {
        RoomIdentifier? identifier = RoomIdentifier.AllRoomIdentifiers.FirstOrDefault(r => r != null && r.Name == room);
        if (identifier == null)
        {
            throw new RequireException($"room {room} does not exist on this map/seed");
        }

        CharacterControllerSettingsPreset settings = actor.CapsuleSettings;
        Vector3[] localOffsets =
        {
            new(0f, 0f, 0f), new(1.5f, 0f, 0f), new(-1.5f, 0f, 0f), new(0f, 0f, 1.5f), new(0f, 0f, -1.5f),
            new(3f, 0f, 0f), new(-3f, 0f, 0f), new(0f, 0f, 3f), new(0f, 0f, -3f),
            new(2f, 0f, 2f), new(-2f, 0f, 2f), new(2f, 0f, -2f), new(-2f, 0f, -2f),
        };
        List<string> failures = new();
        foreach (Vector3 offset in localOffsets)
        {
            Vector3 candidate = identifier.transform.TransformPoint(offset) + Vector3.up * 1f;

            // Cheap horizontal pre-filter for other owned actors (clearer failure text); the
            // capsule probe below ALSO detects any player collider except the actor's own (PT-009).
            bool occupied = ctx.Registry.Actors.Any(other => other != actor && other.Hub != null &&
                other.Hub.gameObject != null &&
                Vector3.Distance(new Vector3(other.Position.x, 0f, other.Position.z), new Vector3(candidate.x, 0f, candidate.z)) < 1.0f);
            if (occupied)
            {
                failures.Add($"{PlacementProbes.Format(candidate)}: occupied");
                continue;
            }

            ProbeResult result = PlacementProbes.ValidateTeleportTarget(candidate, settings, ignoreHub: actor.Hub);
            if (result.Ok)
            {
                EventLog.Line("PROBE", $"room {room} resolved to safe point {PlacementProbes.Format(candidate)} for {actor.Label}");
                return candidate;
            }

            failures.Add($"{PlacementProbes.Format(candidate)}: {result.FailedProbe}");
        }

        throw new RequireException(
            $"no probed safe point found in room {room} for {actor.Label} ({failures.Count} candidates failed: {string.Join("; ", failures.Take(4))} ...)");
    }

    internal static float[] PosArray(Vector3 v) => new[]
    {
        (float)System.Math.Round(v.x, 2), (float)System.Math.Round(v.y, 2), (float)System.Math.Round(v.z, 2),
    };
}
