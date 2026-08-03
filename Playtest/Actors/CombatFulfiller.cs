using System;
using System.Collections.Generic;
using System.Linq;
using InventorySystem;
using InventorySystem.Items;
using InventorySystem.Items.Firearms;
using InventorySystem.Items.Firearms.Modules;
using InventorySystem.Items.Firearms.Modules.Misc;
using InventorySystem.Items.Usables;
using MEC;
using Mirror;
using NetworkManagerUtils.Dummies;
using PlayerRoles;
using PlayerRoles.FirstPersonControl;
using PlaytestHarness.Core;
using PlaytestHarness.Probes;
using PlaytestHarness.Telemetry;
using UnityEngine;

namespace PlaytestHarness.Actors;

/// <summary>
/// Native action verbs — the BATTERIES-INCLUDED core. Aim alignment (camera pitch/yaw to hitbox via
/// the dummy's FpcMouseLook), weapon ready/reload state, fire-until-hit-confirmed with timeout,
/// SCP melee range approach, and native item lifecycle timing all live HERE, once. Scenario code
/// calls one-liners; any Quaternion/aim math in a scenario file is a defect.
///
/// Native paths used (researched in .references, cited in implementation-notes):
/// - Aim: PlayerRoles.FirstPersonControl.FpcMouseLook.CurrentHorizontal/CurrentVertical setters
///   (the dummy mouse-look state the server replicates; same fields the RA dummy actions poke).
/// - Fire: the dummy-action pipeline — DummyActionCollector.ServerGetActions +
///   Inventory.PopulateDummyActions expose "Shoot-&gt;Click" from DummyKeyEmulator
///   (InventorySystem.Items.Autosync.AutosyncItem), which feeds ActionName.Shoot into
///   SimpleTriggerModule / AutomaticActionModule exactly like a real client trigger pull, running
///   the full server firearm pipeline (backtrack, hitreg, PlayerShootingWeapon events).
/// - Readiness: "Reload-&gt;Click" dummy action drives AnimatorReloaderModuleBase (native reload/rack,
///   native duration); failure to reach a fireable state fails rather than mutating firearm internals.
/// - Use item: UsableItemsController.ServerEmulateMessage(serial, StatusType.Start) — the same
///   entry point UsableItem's own dummy action uses; completion waits the item's real UseTime.
/// - Micro H.I.D.: held "Shoot-&gt;Hold" via DummyKeyEmulator feeds InputSyncModule.Primary; the
///   CycleController then runs its REAL WindingUp → Firing cycle using the equipped item's live
///   firing-controller rates. Phase transitions are observed via CycleSyncModule.GetPhase.
/// </summary>
internal static class CombatFulfiller
{
    private const float MeleeRange = 2.2f;

    // ---- aim -------------------------------------------------------------------------------

    /// <summary>
    /// Aim point for a hub: a REAL hitbox (PT-017). Prefers the victim's Body HitboxIdentity
    /// CenterOfMass (the exact point native hitreg tests), falls back to Limb/Headshot, then to a
    /// geometric estimate only when the role's character model exposes no hitboxes (non-FPC edge).
    /// Live hitbox positions track crouch/animation automatically — no static midpoint guess.
    /// </summary>
    internal static Vector3 AimPoint(ReferenceHub hub)
    {
        if (TryGetHitboxAimPoint(hub, out Vector3 hitboxPoint))
        {
            return hitboxPoint;
        }

        Vector3 bodyBase = hub.transform.position;
        Transform camera = hub.PlayerCameraReference;
        Vector3 head = camera != null ? camera.position : bodyBase + Vector3.up * 1.65f;
        return Vector3.Lerp(bodyBase, head, 0.55f);
    }

    /// <summary>
    /// Live hitbox point plus a short, capped motion lead. Firearms are hitscan, so this is not
    /// projectile ballistics; it compensates the dummy-action/backtrack handoff while avoiding
    /// wild aim on teleports or falls.
    /// </summary>
    private static Vector3 AimPointWithLead(ReferenceHub hub)
    {
        Vector3 point = AimPoint(hub);
        if (hub.roleManager?.CurrentRole is not IFpcRole fpc)
        {
            return point;
        }

        Vector3 lead = fpc.FpcModule.Motor.Velocity * 0.08f;
        lead.y = Mathf.Clamp(lead.y, -0.2f, 0.2f);
        return point + Vector3.ClampMagnitude(lead, 0.75f);
    }

    private static bool TryGetHitboxAimPoint(ReferenceHub hub, out Vector3 point)
    {
        HitboxIdentity? best = null;
        int bestRank = int.MaxValue;
        foreach (HitboxIdentity hitbox in HitboxIdentity.Instances)
        {
            if (hitbox == null || hitbox.TargetHub != hub)
            {
                continue;
            }

            // Body > Limb > Headshot: body is the largest, most reliably-hittable box.
            int rank = hitbox.HitboxType switch
            {
                HitboxType.Body => 0,
                HitboxType.Limb => 1,
                _ => 2,
            };
            if (rank < bestRank)
            {
                best = hitbox;
                bestRank = rank;
            }
        }

        if (best == null)
        {
            point = default;
            return false;
        }

        point = best.CenterOfMass;
        return true;
    }

    /// <summary>
    /// True when a ray from the shooter's camera to the aim point is not blocked by world
    /// geometry (the shooter's/victim's own colliders are ignored). Uses the FPC collision mask —
    /// the same layers native hitreg collides with.
    /// </summary>
    private static bool HasLineOfSight(ReferenceHub shooter, ReferenceHub victim, Vector3 aimPoint) =>
        HasLineOfSightFrom(shooter.PlayerCameraReference.position, shooter, victim, aimPoint);

    private static bool HasLineOfSightFrom(
        Vector3 origin,
        ReferenceHub shooter,
        ReferenceHub victim,
        Vector3 aimPoint)
    {
        Vector3 delta = aimPoint - origin;
        float distance = delta.magnitude;
        if (distance < 0.05f)
        {
            return true;
        }

        foreach (RaycastHit hit in Physics.RaycastAll(origin, delta / distance, distance,
                     PlayerRoles.FirstPersonControl.FpcStateProcessor.Mask, QueryTriggerInteraction.Ignore)
                     .OrderBy(static h => h.distance))
        {
            if (hit.collider == null)
            {
                continue;
            }

            ReferenceHub? hitHub = hit.collider.GetComponentInParent<ReferenceHub>();
            if (hitHub == null)
            {
                HitboxIdentity? hitbox = hit.collider.GetComponentInParent<HitboxIdentity>();
                hitHub = hitbox != null ? hitbox.TargetHub : null;
            }

            if (hitHub == shooter)
            {
                continue;
            }

            // First blocking thing along the ray: the victim => clear shot; anything else => blocked.
            return hitHub == victim;
        }

        return true;
    }

    /// <summary>
    /// Sets the dummy's camera pitch+yaw to face a world point through the native FPC mouse-look
    /// (FpcMouseLook.CurrentHorizontal/CurrentVertical), then waits a frame for the transform sync.
    /// </summary>
    internal static IEnumerator<float> LookAt(ScenarioContext ctx, Actor actor, Vector3 worldPoint)
    {
        float tolerance = ctx.Config.AngularToleranceDegrees;
        ApplyLook(actor, worldPoint);
        yield return Timing.WaitForOneFrame;

        // Confirm alignment; one more application catches roles whose modules smooth rotation.
        if (AimErrorDegrees(actor.Hub, worldPoint) > tolerance)
        {
            ApplyLook(actor, worldPoint);
            yield return Timing.WaitForOneFrame;
        }

        float error = AimErrorDegrees(actor.Hub, worldPoint);
        if (error > tolerance * 2f)
        {
            throw new RequireException(
                $"LookAt: actor {actor.Label} camera is {error:0.#} degrees off target {PlacementProbes.Format(worldPoint)} after mouse-look write");
        }
    }

    /// <summary>
    /// Aims the exact authoritative server interaction ray through an authored oriented box. Ordinary
    /// LookAt validates the rendered camera; this verb additionally validates Player.LookRotation, which
    /// is what server-side interaction features read and can differ by pitch sign on headless dummies.
    /// </summary>
    internal static IEnumerator<float> AimAtBox(ScenarioContext ctx, Actor actor, WorldInteractionBox target)
    {
        Vector3 half = target.Size * 0.5f;
        Vector3[] localAimPoints =
        {
            Vector3.zero,
            new(half.x * 0.55f, 0f, 0f),
            new(-half.x * 0.55f, 0f, 0f),
            new(0f, half.y * 0.55f, 0f),
            new(0f, -half.y * 0.55f, 0f),
            new(0f, 0f, half.z * 0.55f),
            new(0f, 0f, -half.z * 0.55f),
        };

        float hitDistance = float.NaN;
        Vector2 finalLook = Vector2.zero;
        foreach (Vector3 local in localAimPoints)
        {
            Vector3 point = target.Center + (target.Rotation * local);
            for (int pitchSign = 0; pitchSign < 2; pitchSign++)
            {
                ApplyLook(actor, point, invertPitch: pitchSign == 1);
                yield return Timing.WaitForOneFrame;
                if (AuthoritativeRayHits(actor, target, out hitDistance, out finalLook))
                {
                    ctx.Emit(new TelemetryEvent("interaction_aim", target.Label, new Dictionary<string, object?>
                    {
                        ["actor"] = actor.Label,
                        ["box_center"] = new[] { target.Center.x, target.Center.y, target.Center.z },
                        ["box_size"] = new[] { target.Size.x, target.Size.y, target.Size.z },
                        ["max_distance_m"] = target.MaxDistance,
                        ["hit_distance_m"] = Math.Round(hitDistance, 3),
                        ["look_pitch"] = Math.Round(finalLook.x, 3),
                        ["look_yaw"] = Math.Round(finalLook.y, 3),
                        ["pitch_inverted"] = pitchSign == 1,
                    }));
                    EventLog.Line("PROBE",
                        $"interaction aim HIT actor={actor.Label} target='{target.Label}' distance={hitDistance:0.###}m look=({finalLook.x:0.##},{finalLook.y:0.##}) inverted={pitchSign == 1}");
                    yield break;
                }
            }
        }

        Vector3 origin = actor.Hub.PlayerCameraReference.position;
        throw new RequireException(
            $"AimAtBox: authoritative server ray missed '{target.Label}' after validated aim attempts; actor={actor.Label} camera={PlacementProbes.Format(origin)} center={PlacementProbes.Format(target.Center)} straight_distance={Vector3.Distance(origin, target.Center):0.##}m max={target.MaxDistance:0.##}m look=({finalLook.x:0.##},{finalLook.y:0.##})");
    }

    private static bool AuthoritativeRayHits(
        Actor actor,
        WorldInteractionBox target,
        out float hitDistance,
        out Vector2 look)
    {
        LabApi.Features.Wrappers.Player player = LabApi.Features.Wrappers.Player.Get(actor.Hub)
            ?? throw new RequireException($"AimAtBox: actor {actor.Label} has no Player wrapper");
        Vector3 origin = player.Camera.position;
        look = player.LookRotation;
        Vector3 direction = Quaternion.Euler(look.x, look.y, 0f) * Vector3.forward;
        Quaternion inverse = Quaternion.Inverse(target.Rotation);
        Vector3 localOrigin = inverse * (origin - target.Center);
        Vector3 localDirection = inverse * direction;
        return RayIntersectsCenteredBox(localOrigin, localDirection, target.Size * 0.5f,
            target.MaxDistance, out hitDistance);
    }

    private static bool RayIntersectsCenteredBox(
        Vector3 origin,
        Vector3 direction,
        Vector3 half,
        float maxDistance,
        out float hitDistance)
    {
        float tMin = 0f;
        float tMax = maxDistance;
        for (int axis = 0; axis < 3; axis++)
        {
            float o = origin[axis];
            float d = direction[axis];
            if (Mathf.Abs(d) < 1e-6f)
            {
                if (o < -half[axis] || o > half[axis])
                {
                    hitDistance = float.NaN;
                    return false;
                }

                continue;
            }

            float t1 = (-half[axis] - o) / d;
            float t2 = (half[axis] - o) / d;
            if (t1 > t2)
            {
                (t1, t2) = (t2, t1);
            }

            tMin = Mathf.Max(tMin, t1);
            tMax = Mathf.Min(tMax, t2);
            if (tMin > tMax)
            {
                hitDistance = float.NaN;
                return false;
            }
        }

        hitDistance = tMin;
        return tMax >= 0f && tMin <= maxDistance;
    }

    private static void ApplyLook(Actor actor, Vector3 worldPoint, bool invertPitch = false)
    {
        IFpcRole fpc = actor.Hub.roleManager.CurrentRole as IFpcRole
            ?? throw new RequireException($"LookAt: actor {actor.Label} has no FPC role ({actor.Role})");
        FpcMouseLook mouseLook = fpc.FpcModule.MouseLook;

        Vector3 origin = actor.Hub.PlayerCameraReference.position;
        Vector3 delta = worldPoint - origin;
        Vector3 horizontal = new(delta.x, 0f, delta.z);
        float yaw = horizontal.sqrMagnitude < 1e-6f
            ? mouseLook.CurrentHorizontal
            : Quaternion.LookRotation(horizontal, Vector3.up).eulerAngles.y;
        float pitch = Mathf.Atan2(delta.y, horizontal.magnitude) * Mathf.Rad2Deg;

        // Native semantics: CurrentHorizontal = body yaw (0..360), CurrentVertical = camera pitch
        // (+up, clamped ±88). The server applies these to hub.transform + PlayerCameraReference in
        // FpcMouseLook.UpdateRotation each frame. Some headless wrapper builds expose LookRotation's
        // pitch with the opposite sign; AimAtBox tries and validates both without leaking that trap.
        mouseLook.CurrentHorizontal = yaw;
        mouseLook.CurrentVertical = invertPitch ? -pitch : pitch;
    }

    private static float AimErrorDegrees(ReferenceHub hub, Vector3 worldPoint)
    {
        Transform camera = hub.PlayerCameraReference;
        Vector3 toTarget = (worldPoint - camera.position).normalized;
        return Vector3.Angle(camera.forward, toTarget);
    }

    // ---- inventory --------------------------------------------------------------------------

    /// <summary>
    /// Native ServerAddItem; firearms also get a reserve of their ammo type. ARRANGEMENT-AWARE
    /// (plan tier table): Quick = unrestricted; Standard = only inside an explicit
    /// ctx.Arrange(reason)/BeginArrangement scope (pre-arranged state, telemetry-logged);
    /// EndToEnd = always rejected — e2e acquires items through real play.
    /// </summary>
    internal static void GiveItem(ScenarioContext ctx, Actor actor, ItemType itemType)
    {
        if (ctx.Fidelity == Fidelity.EndToEnd)
        {
            throw new InvalidOperationException(
                $"GiveItem({itemType}) is forbidden at EndToEnd — items are acquired through real play (pick up from the world).");
        }

        if (ctx.Fidelity == Fidelity.Standard && !ctx.ArrangementActive)
        {
            throw new InvalidOperationException(
                $"GiveItem({itemType}) at Standard requires an explicit pre-arranged-state scope: wrap it in ctx.Arrange(\"reason\", ...) (plan tier table: shortcut #2).");
        }

        Inventory inventory = actor.Hub.inventory;
        ItemBase? item = inventory.ServerAddItem(itemType, ItemAddReason.AdminCommand);
        if (item == null)
        {
            throw new RequireException($"GiveItem: ServerAddItem({itemType}) returned null for {actor.Label} (inventory full?)");
        }

        if (item is Firearm firearm && firearm.TryGetModule(out IPrimaryAmmoContainerModule ammo))
        {
            inventory.ServerAddAmmo(ammo.AmmoType, 60);
        }

        ctx.Emit(new TelemetryEvent("give_item", itemType.ToString(), new Dictionary<string, object?>
        {
            ["actor"] = actor.Label,
            ["serial"] = item.ItemSerial,
            ["arranged"] = ctx.ArrangementActive,
        }));
        EventLog.Line("STEP", $"gave {itemType} (serial {item.ItemSerial}) to {actor.Label}{(ctx.ArrangementActive ? " [arranged]" : string.Empty)}");
    }

    internal static void SetHealth(ScenarioContext ctx, Actor actor, float health)
    {
        if (health <= 0f || float.IsNaN(health) || float.IsInfinity(health))
        {
            throw new ArgumentOutOfRangeException(nameof(health), "arranged health must be finite and positive");
        }

        if (ctx.Fidelity == Fidelity.EndToEnd)
        {
            throw new InvalidOperationException("SetHealth is forbidden at EndToEnd — e2e acquires no pre-arranged state.");
        }

        if (ctx.Fidelity == Fidelity.Standard && !ctx.ArrangementActive)
        {
            throw new InvalidOperationException(
                "SetHealth at Standard requires ctx.Arrange(\"reason\", ...) because it is pre-arranged target state.");
        }

        PlayerStatsSystem.HealthStat stat = actor.Hub.playerStats.GetModule<PlayerStatsSystem.HealthStat>();
        stat.MaxValue = health;
        stat.CurValue = health;
        ctx.Emit(new TelemetryEvent("set_health", null, new Dictionary<string, object?>
        {
            ["actor"] = actor.Label,
            ["health"] = health,
            ["arranged"] = ctx.ArrangementActive,
        }));
    }

    /// <summary>Actor.Equip(ItemType): finds the item in the inventory and equips it natively.</summary>
    internal static IEnumerator<float> EquipByType(ScenarioContext ctx, Actor actor, ItemType itemType)
    {
        ItemBase? item = actor.Hub.inventory.UserInventory.Items.Values.FirstOrDefault(i => i.ItemTypeId == itemType);
        if (item == null)
        {
            throw new RequireException($"Equip: actor {actor.Label} has no {itemType} in inventory");
        }

        foreach (float wait in Coroutines.Pump(Equip(ctx, actor, item)))
        {
            yield return wait;
        }
    }

    /// <summary>Actor.EquipSerial: selects one exact serial, avoiding same-native-type ambiguity.</summary>
    internal static IEnumerator<float> EquipBySerial(ScenarioContext ctx, Actor actor, ushort serial)
    {
        if (!actor.Hub.inventory.UserInventory.Items.TryGetValue(serial, out ItemBase item) || item == null)
        {
            throw new RequireException($"EquipSerial: actor {actor.Label} has no inventory serial {serial}");
        }

        foreach (float wait in Coroutines.Pump(Equip(ctx, actor, item)))
        {
            yield return wait;
        }
    }

    /// <summary>
    /// Sends the same native selection request as EquipSerial but deliberately does not wait for the
    /// item to become current. Feature code may cancel the ChangingItem event while still consuming
    /// the exact attempted serial (catalogue previews use this contract).
    /// </summary>
    internal static IEnumerator<float> AttemptSelectSerial(ScenarioContext ctx, Actor actor, ushort serial)
    {
        if (!actor.Hub.inventory.UserInventory.Items.TryGetValue(serial, out ItemBase item) || item == null)
        {
            throw new RequireException($"AttemptSelectSerial: actor {actor.Label} has no inventory serial {serial}");
        }

        ushort heldBefore = actor.HeldItemSerial;
        actor.Hub.inventory.ServerSelectItem(serial);
        yield return Timing.WaitForOneFrame;
        ctx.Emit(new TelemetryEvent("item_select_attempt", item.ItemTypeId.ToString(), new Dictionary<string, object?>
        {
            ["actor"] = actor.Label,
            ["serial"] = serial,
            ["held_before"] = heldBefore,
            ["held_after"] = actor.HeldItemSerial,
        }));
        EventLog.Line("STEP",
            $"native selection attempt actor={actor.Label} type={item.ItemTypeId} serial={serial} held_after={actor.HeldItemSerial}");
    }

    internal static IEnumerator<float> Equip(ScenarioContext ctx, Actor actor, ItemBase item, float timeoutSeconds = 6f)
    {
        if (actor.Hub.inventory.CurInstance == item)
        {
            yield break;
        }

        actor.Hub.inventory.ServerSelectItem(item.ItemSerial);
        double deadline = ctx.ElapsedSeconds + timeoutSeconds;
        while (actor.Hub.inventory.CurInstance != item)
        {
            if (ctx.ElapsedSeconds >= deadline)
            {
                throw new RequireException(
                    $"equip: actor {actor.Label} never equipped {item.ItemTypeId} (serial {item.ItemSerial}) within {timeoutSeconds:0.#}s");
            }

            yield return Timing.WaitForOneFrame;
        }

        // Give the equipper module its native draw time before firing/using.
        yield return Timing.WaitForSeconds(1.0f);
    }

    // ---- usable / chargeable items (adapter seam) --------------------------------------------

    /// <summary>
    /// Uses the currently held item through its native lifecycle and waits for REAL completion.
    /// Dispatch is adapter-based: UsableItem (medkit/SCP-500/adrenaline...) waits its real UseTime
    /// via the usable-items controller; Micro H.I.D. runs its real windup/discharge cycle. Future
    /// item families add one adapter case here — scenario code stays actor.UseHeldItem().
    /// </summary>
    internal static IEnumerator<float> UseHeldItem(ScenarioContext ctx, Actor actor)
    {
        ItemBase? held = actor.Hub.inventory.CurInstance;
        // Dispose-through (PT-016): the runner only owns/disposes THIS enumerator on
        // timeout/abort; without the finally the nested adapter's own finally (static
        // ServerOnUsingCompleted unsubscribe, Micro H.I.D. Shoot->Release) would never run.
        foreach (float wait in Coroutines.Pump(held switch
        {
            UsableItem usable => UseUsableItem(ctx, actor, usable),
            InventorySystem.Items.MicroHID.MicroHIDItem micro => UseMicroHid(ctx, actor, micro),
            null => throw new RequireException($"UseHeldItem: actor {actor.Label} is holding nothing (Equip an item first)"),
            _ => throw new RequireException(
                $"UseHeldItem: no lifecycle adapter for held item {held.ItemTypeId} ({held.GetType().Name}) — add an adapter in CombatFulfiller.UseHeldItem"),
        }))
        {
            yield return wait;
        }
    }

    /// <summary>
    /// Adapter: standard usable items. Emulates the client Start status
    /// (UsableItemsController.ServerEmulateMessage) then waits for ServerOnUsingCompleted — the
    /// controller completes the use after item.UseTime / speed-multiplier seconds, all native.
    /// </summary>
    /// <summary>
    /// Targeted native-use adapter. It owns approach, LOS, live hitbox aim, and effect confirmation;
    /// scenarios never hand-roll range/raycast logic. Only item families with a causal gameplay
    /// oracle are accepted.
    /// </summary>
    internal static IEnumerator<float> UseHeldItemOn(ScenarioContext ctx, Actor actor, Actor target)
    {
        if (actor.Hub.inventory.CurInstance is not InventorySystem.Items.MicroHID.MicroHIDItem micro)
        {
            ItemBase? held = actor.Hub.inventory.CurInstance;
            throw new RequireException(
                $"UseHeldItemOn: no targeted adapter for {(held == null ? "nothing" : held.ItemTypeId + " (" + held.GetType().Name + ")")}");
        }

        const float desiredRange = 4.5f;
        Vector3 aim = AimPoint(target.Hub);
        float currentDistance = Vector3.Distance(actor.Hub.PlayerCameraReference.position, aim);
        if (currentDistance > desiredRange || !HasLineOfSight(actor.Hub, target.Hub, aim))
        {
            Vector3 away = actor.Position - target.Position;
            away.y = 0f;
            if (away.sqrMagnitude < 0.01f)
            {
                away = -target.Hub.PlayerCameraReference.forward;
                away.y = 0f;
            }

            away.Normalize();
            Vector3 cameraOffset = actor.Hub.PlayerCameraReference.position - actor.Position;
            Vector3? clearApproach = null;
            float[] angles = { 0f, 45f, -45f, 90f, -90f, 135f, -135f, 180f };
            float[] radii = { 3f, 4f, 5f };
            foreach (float angle in angles)
            {
                Vector3 direction = Quaternion.AngleAxis(angle, Vector3.up) * away;
                foreach (float radius in radii)
                {
                    Vector3 candidate = target.Position + (direction * radius);
                    ProbeResult probe = PlacementProbes.ValidateTeleportTarget(
                        candidate,
                        actor.CapsuleSettings,
                        ctx.Config.FastTravelMaxDropMeters,
                        actor.Hub);
                    if (!probe.Ok ||
                        !PlacementProbes.TryFindGround(candidate, ctx.Config.FastTravelMaxDropMeters,
                            out float groundY, out _))
                    {
                        continue;
                    }

                    Vector3 standingRoot = new(
                        candidate.x,
                        groundY + PlacementProbes.ExpectedRootHeight(actor.CapsuleSettings),
                        candidate.z);
                    if (HasLineOfSightFrom(standingRoot + cameraOffset, actor.Hub, target.Hub, aim))
                    {
                        clearApproach = candidate;
                        break;
                    }
                }

                if (clearApproach.HasValue)
                {
                    break;
                }
            }

            if (clearApproach.HasValue)
            {
                foreach (float wait in Coroutines.Pump(MovementFulfiller.GoTo(ctx, actor, clearApproach.Value)))
                {
                    yield return wait;
                }
            }
        }

        Vector3 finalAim = AimPoint(target.Hub);
        float finalDistance = Vector3.Distance(actor.Hub.PlayerCameraReference.position, finalAim);
        if (finalDistance > 6f || !HasLineOfSight(actor.Hub, target.Hub, finalAim))
        {
            throw new RequireException(
                $"UseHeldItemOn(MicroHID): could not establish a clear <=6m shot from {actor.Label} to {target.Label} (distance={finalDistance:0.##}m)");
        }

        foreach (float wait in Coroutines.Pump(LookAt(ctx, actor, finalAim)))
        {
            yield return wait;
        }

        foreach (float wait in Coroutines.Pump(UseMicroHid(ctx, actor, micro, target, fireHoldSeconds: 1.5f)))
        {
            yield return wait;
        }
    }

    private static IEnumerator<float> UseUsableItem(ScenarioContext ctx, Actor actor, UsableItem usable)
    {
        ushort serial = usable.ItemSerial;
        bool completed = false;
        void OnUsed(ReferenceHub hub, UsableItem item)
        {
            if (hub == actor.Hub && item.ItemSerial == serial)
            {
                completed = true;
            }
        }

        UsableItemsController.ServerOnUsingCompleted += OnUsed;
        try
        {
            double startAt = ctx.ElapsedSeconds;
            UsableItemsController.ServerEmulateMessage(serial, StatusMessage.StatusType.Start);
            yield return Timing.WaitForSeconds(0.25f);

            PlayerHandler handler = UsableItemsController.GetHandler(actor.Hub);
            if (handler.CurrentUsable.ItemSerial != serial && !completed)
            {
                throw new RequireException(
                    $"UseHeldItem: native use of {usable.ItemTypeId} never STARTED for {actor.Label} (cooldown active or start rejected)");
            }

            // Wait the item's real use time (+ margin). Cooldowns/charge times are real by design.
            float timeout = usable.UseTime + 5f;
            double deadline = ctx.ElapsedSeconds + timeout;
            while (!completed)
            {
                if (ctx.ElapsedSeconds >= deadline)
                {
                    throw new RequireException(
                        $"UseHeldItem: {usable.ItemTypeId} use by {actor.Label} did not complete within {timeout:0.#}s (UseTime={usable.UseTime:0.#}s)");
                }

                yield return Timing.WaitForOneFrame;
            }

            double took = ctx.ElapsedSeconds - startAt;
            ctx.Emit(new TelemetryEvent("used_item", usable.ItemTypeId.ToString(), new Dictionary<string, object?>
            {
                ["actor"] = actor.Label,
                ["use_time_s"] = usable.UseTime,
                ["elapsed_s"] = Math.Round(took, 2),
            }));
            EventLog.Line("STEP", $"actor {actor.Label} used {usable.ItemTypeId} natively in {took:0.##}s (UseTime={usable.UseTime:0.##}s)");
        }
        finally
        {
            UsableItemsController.ServerOnUsingCompleted -= OnUsed;
        }
    }

    internal static IEnumerator<float> AttemptDisabledMicroHidSecondary(
        ScenarioContext ctx,
        Actor actor,
        float holdSeconds)
    {
        if (holdSeconds <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(holdSeconds));
        }

        if (actor.Hub.inventory.CurInstance is not InventorySystem.Items.MicroHID.MicroHIDItem micro)
        {
            throw new RequireException(
                $"AttemptDisabledMicroHidSecondary: {actor.Label} is not holding a Micro H.I.D.");
        }

        ushort serial = micro.ItemSerial;
        InventorySystem.Items.MicroHID.Modules.MicroHidPhase before =
            InventorySystem.Items.MicroHID.Modules.CycleSyncModule.GetPhase(serial);
        if (before != InventorySystem.Items.MicroHID.Modules.MicroHidPhase.Standby)
        {
            throw new RequireException(
                $"AttemptDisabledMicroHidSecondary: {actor.Label}'s HID starts in {before}, expected Standby");
        }

        bool leftStandby = false;
        void OnPhaseChanged(ushort changedSerial, InventorySystem.Items.MicroHID.Modules.MicroHidPhase phase)
        {
            if (changedSerial == serial && phase != InventorySystem.Items.MicroHID.Modules.MicroHidPhase.Standby)
            {
                leftStandby = true;
            }
        }

        string category = $"{micro.ItemTypeId} (#{serial})";
        InventorySystem.Items.MicroHID.Modules.CycleController.OnPhaseChanged += OnPhaseChanged;
        try
        {
            if (!TryInvokeDummyAction(actor.Hub, $"{category}:Zoom->Hold"))
            {
                throw new RequireException(
                    $"AttemptDisabledMicroHidSecondary: no exact-serial native Zoom hold action for {actor.Label}'s HID {serial}");
            }

            try
            {
                yield return Timing.WaitForSeconds(holdSeconds);
            }
            finally
            {
                ReleaseDummyActionOrWarn(actor.Hub, actor.Label, $"{category}:Zoom->Release");
            }

            yield return Timing.WaitForOneFrame;
        }
        finally
        {
            InventorySystem.Items.MicroHID.Modules.CycleController.OnPhaseChanged -= OnPhaseChanged;
        }

        InventorySystem.Items.MicroHID.Modules.MicroHidPhase after =
            InventorySystem.Items.MicroHID.Modules.CycleSyncModule.GetPhase(serial);
        if (leftStandby || after != InventorySystem.Items.MicroHID.Modules.MicroHidPhase.Standby)
        {
            throw new RequireException(
                $"AttemptDisabledMicroHidSecondary: secondary input changed {actor.Label}'s HID phase (leftStandby={leftStandby}, final={after})");
        }

        ctx.Emit(new TelemetryEvent("micro_hid_secondary_blocked", null, new Dictionary<string, object?>
        {
            ["actor"] = actor.Label,
            ["serial"] = serial,
            ["hold_s"] = holdSeconds,
            ["phase_before"] = before.ToString(),
            ["phase_after"] = after.ToString(),
        }));
        EventLog.Line("STEP",
            $"Micro H.I.D. secondary remained disabled actor={actor.Label} hold={holdSeconds:0.###}s phase={after}");
    }

    internal static IEnumerator<float> InterruptMicroHidWithEquip(
        ScenarioContext ctx,
        Actor actor,
        ItemType switchTo)
    {
        if (actor.Hub.inventory.CurInstance is not InventorySystem.Items.MicroHID.MicroHIDItem micro)
        {
            throw new RequireException(
                $"InterruptMicroHidWithEquip: {actor.Label} is not holding a Micro H.I.D.");
        }

        ItemBase? replacement = actor.Hub.inventory.UserInventory.Items.Values
            .FirstOrDefault(item => item != null && item.ItemTypeId == switchTo && item.ItemSerial != micro.ItemSerial);
        if (replacement == null)
        {
            throw new RequireException(
                $"InterruptMicroHidWithEquip: {actor.Label} has no replacement item {switchTo}");
        }

        ushort serial = micro.ItemSerial;
        string category = $"{micro.ItemTypeId} (#{serial})";
        if (!TryInvokeDummyAction(actor.Hub, $"{category}:Shoot->Hold"))
        {
            throw new RequireException(
                $"InterruptMicroHidWithEquip: no exact-serial native Shoot hold action for {actor.Label}'s HID {serial}");
        }

        try
        {
            foreach (float wait in Coroutines.Pump(WaitForPhase(
                ctx,
                actor,
                serial,
                InventorySystem.Items.MicroHID.Modules.MicroHidPhase.WindingUp,
                8f)))
            {
                yield return wait;
            }

            foreach (float wait in Coroutines.Pump(Equip(ctx, actor, replacement)))
            {
                yield return wait;
            }

            if (actor.HeldItemSerial != replacement.ItemSerial)
            {
                throw new RequireException(
                    $"InterruptMicroHidWithEquip: {actor.Label} did not switch from HID {serial} to {replacement.ItemSerial}");
            }
        }
        finally
        {
            ReleaseDummyActionOrWarn(actor.Hub, actor.Label, $"{micro.ItemTypeId} (#{serial}):Shoot->Release");
        }

        ctx.Emit(new TelemetryEvent("micro_hid_holster_interrupt", null, new Dictionary<string, object?>
        {
            ["actor"] = actor.Label,
            ["hid_serial"] = serial,
            ["replacement_serial"] = replacement.ItemSerial,
            ["replacement_type"] = replacement.ItemTypeId.ToString(),
        }));
        EventLog.Line("STEP",
            $"Micro H.I.D. holstered during wind-up actor={actor.Label} hid={serial} replacement={replacement.ItemTypeId}/{replacement.ItemSerial}");
    }

    /// <summary>
    /// Adapter: Micro H.I.D. Holds the native Shoot key through the DummyKeyEmulator
    /// ("Shoot-&gt;Hold" dummy action), which feeds InputSyncModule.Primary; the item's
    /// CycleController then runs its real phase machine using the equipped firing mode's live
    /// wind-up and wind-down rates. The verb waits each transition with bounded timeouts and
    /// reports the measured durations. It never substitutes sleeps for native phase observation.
    /// </summary>
    private static IEnumerator<float> UseMicroHid(ScenarioContext ctx, Actor actor,
        InventorySystem.Items.MicroHID.MicroHIDItem micro, Actor? target = null, float fireHoldSeconds = 1.5f)
    {
        ushort serial = micro.ItemSerial;
        double startAt = ctx.ElapsedSeconds;
        float energyBefore = micro.EnergyManager.Energy;
        float targetHealthBefore = target?.Health ?? 0f;
        bool targetEffectSeen = false;
        void OnHurt(LabApi.Events.Arguments.PlayerEvents.PlayerHurtEventArgs ev)
        {
            if (target == null || ev.Player?.ReferenceHub != target.Hub ||
                ev.DamageHandler is not PlayerStatsSystem.MicroHidDamageHandler hid ||
                hid.Attacker.Hub != actor.Hub || hid.Damage <= 0f)
            {
                return;
            }

            targetEffectSeen = true;
        }

        if (target != null)
        {
            LabApi.Events.Handlers.PlayerEvents.Hurt += OnHurt;
        }

        try
        {
            // 1) Hold primary fire via the native dummy key emulation (byte-identical to the RA panel).
            if (!TryInvokeDummyAction(actor.Hub, $"{micro.ItemTypeId} (#{serial}):Shoot->Hold"))
        {
            throw new RequireException(
                $"UseHeldItem(MicroHID): no exact-serial 'Shoot->Hold' action for {actor.Label}'s HID {serial}");
        }

        double chargeTook;
        float windDownRate = 1f;
        try
        {
            // 2) Standby -> WindingUp (CycleController.UpdateStandby needs its 1.5 s idle timer
            //    plus ValidateStart => real, small, native delay).
            foreach (float wait in Coroutines.Pump(WaitForPhase(ctx, actor, serial, InventorySystem.Items.MicroHID.Modules.MicroHidPhase.WindingUp, 8f, target)))
            {
                yield return wait;
            }

            double windStart = ctx.ElapsedSeconds;
            EventLog.Line("STEP", $"micro-hid winding up (actor={actor.Label}, real native charge begins)");

            // 3) WindingUp -> Firing. The phase machine advances only by
            //    ServerWindUpProgress += WindUpRate * dt. Generous timeout.
            foreach (float wait in Coroutines.Pump(WaitForPhase(ctx, actor, serial, InventorySystem.Items.MicroHID.Modules.MicroHidPhase.Firing, 30f, target)))
            {
                yield return wait;
            }

            chargeTook = ctx.ElapsedSeconds - windStart;

            // PT-013: assert the observed wind-up matches the equipped firing controller's live
            // rate. This accepts deliberate weapon tuning while still rejecting skipped native time.
            float windUpRate = 1f / 3f;
            if (InventorySystem.Items.MicroHID.Modules.CycleSyncModule.GetCycleController(serial)
                    .TryGetLastFiringController(out InventorySystem.Items.MicroHID.Modules.FiringModeControllerModule firingController))
            {
                if (firingController.WindUpRate > 0f)
                {
                    windUpRate = firingController.WindUpRate;
                }

                if (firingController.WindDownRate > 0f)
                {
                    windDownRate = firingController.WindDownRate;
                }
            }

            float expectedWindUp = 1f / windUpRate;
            if (chargeTook < expectedWindUp * 0.85f)
            {
                throw new RequireException(
                    $"UseHeldItem(MicroHID): wind-up completed in {chargeTook:0.##}s but the native rate ({windUpRate:0.###}/s) requires ~{expectedWindUp:0.##}s — charge time was skipped");
            }

            EventLog.Line("STEP", $"micro-hid FIRING after {chargeTook:0.##}s real wind-up (actor={actor.Label}, native minimum {expectedWindUp:0.##}s)");

            // 4) Hold the beam for the requested duration (real firing time, real energy drain).
            double fireStart = ctx.ElapsedSeconds;
            if (target == null)
            {
                yield return Timing.WaitForSeconds(fireHoldSeconds);
            }
            else
            {
                double fireEnd = fireStart + fireHoldSeconds;
                while (ctx.ElapsedSeconds < fireEnd)
                {
                    ApplyLook(actor, AimPoint(target.Hub));
                    yield return Timing.WaitForOneFrame;
                }
            }

            double fireTook = ctx.ElapsedSeconds - fireStart;
            if (fireTook < fireHoldSeconds * 0.9f)
            {
                throw new RequireException(
                    $"UseHeldItem(MicroHID): firing hold lasted {fireTook:0.##}s (requested {fireHoldSeconds:0.##}s) — native beam time was skipped");
            }
        }
        finally
        {
            // 5) Release the key (native release path) — also on failure, never leave it held.
            ReleaseDummyActionOrWarn(actor.Hub, actor.Label, $"{micro.ItemTypeId} (#{serial}):Shoot->Release");
        }

        // 6) Wind down through the native phase machine using the live controller rate.
        double windDownStart = ctx.ElapsedSeconds;
        foreach (float wait in Coroutines.Pump(WaitForPhase(ctx, actor, serial, InventorySystem.Items.MicroHID.Modules.MicroHidPhase.Standby, 20f, target)))
        {
            yield return wait;
        }

        double windDownTook = ctx.ElapsedSeconds - windDownStart;
        float expectedWindDown = 1f / windDownRate;
        if (windDownTook < expectedWindDown * 0.8f)
        {
            throw new RequireException(
                $"UseHeldItem(MicroHID): wind-down completed in {windDownTook:0.##}s but the live rate ({windDownRate:0.###}/s) requires ~{expectedWindDown:0.##}s — cooldown time was skipped");
        }

        double total = ctx.ElapsedSeconds - startAt;
        float energyAfter = micro.EnergyManager.Energy;

            // Assert BEFORE emitting the success-shaped record: a failed cycle must never write a
            // completed 'used_item' event that downstream telemetry consumers read as native success.
            if (energyAfter >= energyBefore)
            {
                throw new RequireException(
                    $"UseHeldItem(MicroHID): energy did not drain ({energyBefore:0.###} -> {energyAfter:0.###}) — the firing phase did not actually run");
            }

            if (target != null && !targetEffectSeen)
            {
                throw new RequireException(
                    $"UseHeldItemOn(MicroHID): native cycle completed but no attributable MicroHID damage hit {target.Label} " +
                    $"(health {targetHealthBefore:0.##} -> {target.Health:0.##})");
            }

            ctx.Emit(new TelemetryEvent("used_item", "MicroHID", new Dictionary<string, object?>
            {
                ["actor"] = actor.Label,
                ["elapsed_s"] = Math.Round(total, 2),
                ["fire_hold_s"] = fireHoldSeconds,
                ["energy_before"] = Math.Round(energyBefore, 3),
                ["energy_after"] = Math.Round(energyAfter, 3),
                ["target"] = target?.Label,
                ["target_effect_seen"] = targetEffectSeen,
                ["target_health_before"] = target == null ? null : Math.Round(targetHealthBefore, 2),
                ["target_health_after"] = target == null ? null : Math.Round(target.Health, 2),
            }));
            EventLog.Line("STEP",
                $"actor {actor.Label} completed a full native Micro H.I.D. cycle in {total:0.##}s (energy {energyBefore:0.###} -> {energyAfter:0.###})");
        }
        finally
        {
            if (target != null)
            {
                LabApi.Events.Handlers.PlayerEvents.Hurt -= OnHurt;
            }
        }
    }

    private static IEnumerator<float> WaitForPhase(ScenarioContext ctx, Actor actor, ushort serial,
        InventorySystem.Items.MicroHID.Modules.MicroHidPhase wanted, float timeoutSeconds, Actor? aimTarget = null)
    {
        double deadline = ctx.ElapsedSeconds + timeoutSeconds;
        while (true)
        {
            if (aimTarget != null)
            {
                // Holding/charging is a long native action; keep tracking the live hitbox so bot AI,
                // animation, crouch, or target motion cannot stale a one-frame aim write.
                ApplyLook(actor, AimPoint(aimTarget.Hub));
            }

            InventorySystem.Items.MicroHID.Modules.MicroHidPhase phase =
                InventorySystem.Items.MicroHID.Modules.CycleSyncModule.GetPhase(serial);
            if (phase == wanted)
            {
                yield break;
            }

            if (ctx.ElapsedSeconds >= deadline)
            {
                throw new RequireException(
                    $"UseHeldItem(MicroHID): actor {actor.Label} item never reached phase {wanted} within {timeoutSeconds:0.#}s (stuck at {phase}) — native cycle did not progress");
            }

            yield return Timing.WaitForOneFrame;
        }
    }

    // ---- attack ------------------------------------------------------------------------------

    /// <summary>
    /// Real attack with hit confirmation. Human roles: equip/ready a firearm, aim at the victim's
    /// REAL hitbox, fire through the native dummy-action trigger path, retry (with LOS
    /// repositioning) until the hit is confirmed or timeout. SCP roles: approach into melee range
    /// (walk at standard+/TP at quick), aim, use the native SCP attack ability dummy action.
    /// CONFIRMATION IS CAUSAL (PT-005): only a native Hurt event whose DamageHandler is an
    /// AttackerDamageHandler attributing THIS attacker (weapon-typed for firearm attacks, positive
    /// damage output) counts — an unrelated health drop or a plugin's stat write does not.
    /// </summary>
    internal static IEnumerator<float> Attack(ScenarioContext ctx, Actor attacker, Actor victim)
    {
        float initialHealth = victim.Health;
        bool isMelee = IsScpMelee(attacker.Role);
        bool hurtSeen = false;
        bool confirmationWindowOpen = false;
        ItemType expectedFirearm = ItemType.None;
        double confirmationDeadline = 0d;
        string? confirmedBy = null;
        void ArmFirearmConfirmation(ItemType weapon)
        {
            expectedFirearm = weapon;
            confirmationDeadline = ctx.ElapsedSeconds + 1.5d;
            confirmationWindowOpen = true;
        }

        void ArmMeleeConfirmation()
        {
            confirmationDeadline = ctx.ElapsedSeconds + 1.5d;
            confirmationWindowOpen = true;
        }

        void CloseConfirmationWindow() => confirmationWindowOpen = false;

        void OnHurt(LabApi.Events.Arguments.PlayerEvents.PlayerHurtEventArgs ev)
        {
            if (hurtSeen || !confirmationWindowOpen || ctx.ElapsedSeconds > confirmationDeadline ||
                ev.Player?.ReferenceHub != victim.Hub)
            {
                return;
            }

            // Attacker correlation via the DAMAGE HANDLER, not the wrapper args: the handler is
            // what native hitreg produced. Requires (a) an attacker handler pinned to this
            // attacker, (b) positive damage actually dealt (Hurt fires before the Nothing check in
            // the decompiled DealDamage), and (c) for firearm attacks a FirearmDamageHandler of
            // the weapon actually fired — a plugin-originated non-firearm hit credited to the
            // shooter no longer confirms the shot.
            if (ev.DamageHandler is not PlayerStatsSystem.AttackerDamageHandler attackerHandler ||
                attackerHandler.Attacker.Hub != attacker.Hub)
            {
                return;
            }

            if (attackerHandler is PlayerStatsSystem.StandardDamageHandler standard && standard.TotalDamageDealt <= 0f)
            {
                return;
            }

            if (!isMelee)
            {
                if (attackerHandler is not PlayerStatsSystem.FirearmDamageHandler firearmHandler)
                {
                    return;
                }

                if (firearmHandler.WeaponType != expectedFirearm)
                {
                    return;
                }

                confirmedBy = $"FirearmDamageHandler({firearmHandler.WeaponType}) in armed shot window";
            }
            else
            {
                confirmedBy = attackerHandler.GetType().Name;
            }

            hurtSeen = true;
            confirmationWindowOpen = false;
        }

        IEnumerator<float> body = isMelee
            ? MeleeAttackBody(ctx, attacker, victim, () => hurtSeen, ArmMeleeConfirmation, CloseConfirmationWindow)
            : FirearmAttackBody(ctx, attacker, victim, () => hurtSeen, ArmFirearmConfirmation, CloseConfirmationWindow);
        LabApi.Events.Handlers.PlayerEvents.Hurt += OnHurt;
        try
        {
            while (body.MoveNext())
            {
                yield return body.Current;
            }

            float damage = initialHealth - victim.Health;
            ctx.Emit(new TelemetryEvent("attack_confirmed", null, new Dictionary<string, object?>
            {
                ["attacker"] = attacker.Label,
                ["victim"] = victim.Label,
                ["hurt_event"] = hurtSeen,
                ["confirmed_by"] = confirmedBy,
                ["health_before"] = Math.Round(initialHealth, 2),
                ["health_after"] = Math.Round(victim.Health, 2),
                ["damage"] = Math.Round(damage, 2),
            }));
            EventLog.Line("STEP",
                $"attack confirmed attacker={attacker.Label} victim={victim.Label} handler={confirmedBy} health {initialHealth:0.#} -> {victim.Health:0.#} (-{damage:0.#})");
        }
        finally
        {
            LabApi.Events.Handlers.PlayerEvents.Hurt -= OnHurt;
            body.Dispose();
        }
    }

    internal static bool IsScpMelee(RoleTypeId role) => role is RoleTypeId.Scp049 or RoleTypeId.Scp0492
        or RoleTypeId.Scp096 or RoleTypeId.Scp106 or RoleTypeId.Scp173 or RoleTypeId.Scp939 or RoleTypeId.Scp3114;

    /// <summary>
    /// Exact per-role "category:action" for the native melee subroutine. Categories are the
    /// subroutine class names (SubroutineBase.PopulateDummyActions emits GetType().Name), so an
    /// UNcategorized "Shoot-&gt;Click" would resolve to the FIRST Shoot binding in ANY category —
    /// on roles with several Shoot-bound subroutines that silently invokes the wrong ability.
    /// Every IsScpMelee role must therefore be mapped here; an unmapped role fails loudly.
    /// </summary>
    internal static string MeleeActionName(Actor attacker) => attacker.Role switch
    {
        RoleTypeId.Scp049 => "Scp049AttackAbility:Shoot->Click",
        RoleTypeId.Scp0492 => "ZombieAttackAbility:Shoot->Click",
        RoleTypeId.Scp096 => "Scp096AttackAbility:Shoot->Click",
        RoleTypeId.Scp106 => "Scp106Attack:Shoot->Click",
        RoleTypeId.Scp173 => "Scp173SnapAbility:Shoot->Click",
        RoleTypeId.Scp939 => "Scp939ClawAbility:Shoot->Click",
        RoleTypeId.Scp3114 => "Scp3114Slap:Shoot->Click",
        _ => throw new RequireException(
            $"Attack(melee): no exact melee subroutine mapping for {attacker.Label} ({attacker.Role}) — add it to CombatFulfiller.MeleeActionName"),
    };

    private static IEnumerator<float> FirearmAttackBody(ScenarioContext ctx, Actor attacker, Actor victim,
        Func<bool> hitConfirmed, Action<ItemType> armConfirmation, Action closeConfirmation)
    {
        float attackTimeout = ctx.Config.AttackTimeoutSeconds;

        // 1) Ensure a firearm is in hand (equip if needed; the scenario should have given one —
        //    if none exists we fail loudly rather than conjuring a weapon at standard+).
        Inventory inventory = attacker.Hub.inventory;
        Firearm? firearm = inventory.CurInstance as Firearm
            ?? inventory.UserInventory.Items.Values.OfType<Firearm>().FirstOrDefault();
        if (firearm == null)
        {
            throw new RequireException($"Attack: actor {attacker.Label} has no firearm (GiveItem one first)");
        }

        foreach (float wait in Coroutines.Pump(Equip(ctx, attacker, firearm)))
        {
            yield return wait;
        }

        // 2) Readiness: prefer the fully-native reload flow (the "Reload->Click" dummy action feeds
        //    ActionName.Reload into AnimatorReloaderModuleBase — real animation, real duration).
        foreach (float wait in Coroutines.Pump(EnsureFirearmReady(ctx, attacker, firearm)))
        {
            yield return wait;
        }

        // 3) Aim-and-fire loop with retry until hit confirmed or timeout: fresh hitbox aim every
        //    shot (live position — moving/crouching victims are tracked), LOS pre-check with
        //    walk-based repositioning when blocked (PT-017), and a victim-death terminal branch.
        double deadline = ctx.ElapsedSeconds + attackTimeout;
        int shots = 0;
        int losRepositions = 0;
        while (!hitConfirmed())
        {
            if (ctx.ElapsedSeconds >= deadline)
            {
                throw new RequireException(
                    $"Attack: {attacker.Label} fired {shots} shot(s) at {victim.Label} but no attributable hit was confirmed within {attackTimeout:0.#}s (victim health {victim.Health:0.#}, LOS repositions {losRepositions})");
            }

            if (victim.Role == RoleTypeId.Spectator || victim.Role == RoleTypeId.None || victim.Health <= 0f)
            {
                throw new RequireException(
                    $"Attack: victim {victim.Label} died/despawned ({victim.Role}) before a hit by {attacker.Label} was confirmed — another source killed it first");
            }

            // Fresh hitbox point every iteration: live CenterOfMass follows the victim's model.
            Vector3 aim = AimPointWithLead(victim.Hub);
            if (!HasLineOfSight(attacker.Hub, victim.Hub, aim))
            {
                // Blocked: move toward the victim (tier-correct — WalkTo walks at standard+,
                // TPs at quick) to clear the obstruction instead of blind-firing into a wall.
                losRepositions++;
                Vector3 midpoint = Vector3.Lerp(attacker.Position, victim.Position, 0.5f);
                EventLog.Line("STEP",
                    $"attack: no line of sight from {attacker.Label} to {victim.Label}; repositioning toward {PlacementProbes.Format(midpoint)} (attempt {losRepositions})");
                foreach (float wait in Coroutines.Pump(MovementFulfiller.WalkTo(ctx, attacker, midpoint)))
                {
                    yield return wait;
                }

                continue;
            }

            foreach (float wait in Coroutines.Pump(LookAt(ctx, attacker, aim)))
            {
                yield return wait;
            }

            // Arm immediately before the native trigger: synchronous or queued Hurt events from
            // readiness/repositioning cannot satisfy this shot, and the window closes before retry.
            int ammoBeforeShot = FirearmAmmoUnits(firearm);
            armConfirmation(firearm.ItemTypeId);
            foreach (float wait in Coroutines.Pump(TriggerFirearm(attacker, firearm)))
            {
                yield return wait;
            }

            shots++;
            ctx.Emit(new TelemetryEvent("shot_fired", null, new Dictionary<string, object?>
            {
                ["attacker"] = attacker.Label,
                ["victim"] = victim.Label,
                ["weapon"] = firearm.ItemTypeId.ToString(),
                ["shot_n"] = shots,
            }));

            // Native fire is asynchronous (client-emulated trigger -> queued shot -> server
            // backtrack). Give it a few frames before checking/retrying. Re-ready between shots
            // covers dry-fire de-cocking (double-action revolver semantics).
            yield return Timing.WaitForSeconds(0.5f);
            int ammoAfterShot = FirearmAmmoUnits(firearm);
            if (hitConfirmed() && ammoBeforeShot >= 0 && ammoAfterShot >= ammoBeforeShot)
            {
                throw new RequireException(
                    $"Attack: attributable firearm Hurt arrived but {firearm.ItemTypeId} consumed no ammo ({ammoBeforeShot} -> {ammoAfterShot}); refusing a non-native confirmation");
            }

            if (!hitConfirmed())
            {
                closeConfirmation();
                foreach (float wait in Coroutines.Pump(EnsureFirearmReady(ctx, attacker, firearm)))
                {
                    yield return wait;
                }
            }
        }

        EventLog.Line("STEP", $"attack: {attacker.Label} hit {victim.Label} with {firearm.ItemTypeId} after {shots} native shot(s)");
    }

    internal static int FirearmAmmoUnits(Firearm firearm)
    {
        int total = 0;
        bool found = false;
        if (firearm.TryGetModule(out IPrimaryAmmoContainerModule magazine))
        {
            total += magazine.AmmoStored;
            found = true;
        }

        if (firearm.TryGetModule(out AutomaticActionModule action))
        {
            total += action.AmmoStored;
            found = true;
        }
        else if (firearm.TryGetModule(out PumpActionModule pump))
        {
            total += pump.AmmoStored;
            found = true;
        }

        return found ? total : -1;
    }

    internal static IEnumerator<float> TriggerFirearm(Actor actor, Firearm firearm)
    {
        if (firearm.TryGetModule(out PumpActionModule pump))
        {
            // PumpActionModule gates its client shot queue on IsLocalPlayer rather than
            // AutosyncItem.IsControllable, so native DummyKeyEmulator input cannot reach it on a
            // server dummy. Feed the exact client command payload through the public server command
            // boundary; UpdateServer still owns events, ammo, backtracking, and hit registration.
            using NetworkWriterPooled writer = NetworkWriterPool.Get();
            new ShotBacktrackData(firearm).WriteSelf(writer);
            using NetworkReaderPooled reader = NetworkReaderPool.Get(writer.ToArraySegment());
            pump.ServerProcessCmd(reader);
            yield return Timing.WaitForOneFrame;
            yield break;
        }

        if (!TryInvokeDummyActionForSerial(
                actor.Hub, firearm.ItemSerial, "Shoot->Click", out _))
        {
            throw new RequireException(
                $"Attack: no exact item-serial #{firearm.ItemSerial} 'Shoot->Click' action for {actor.Label}'s {firearm.ItemTypeId}");
        }
    }

    /// <summary>
    /// Batteries-included firearm readiness. Preferred path: the NATIVE reload dummy action
    /// ("Reload-&gt;Click" → AnimatorReloaderModuleBase → magazine + bolt, real animation time),
    /// waited via IReloaderModule.IsReloading. If the firearm is still not ready after that client-
    /// equivalent input, this verb FAILS LOUDLY. It never calls ServerCycleAction directly and never
    /// writes Cocked/BoltLocked: those are animation/firearm-internal state transitions, not a player
    /// action the harness may fabricate (PT-004).
    /// </summary>
    internal static IEnumerator<float> EnsureFirearmReady(ScenarioContext ctx, Actor actor, Firearm firearm)
    {
        if (firearm.TryGetModule(out ITriggerControllerModule trigger))
        {
            // DummyKeyEmulator registers action listeners lazily when a module first queries them.
            // Touch the real held trigger controller, then let the action registry refresh; this is
            // required by pump-action weapons whose Shoot action may not exist immediately on equip.
            _ = trigger.TriggerHeld;
            yield return Timing.WaitForOneFrame;
        }

        if (firearm.TryGetModule(out PumpActionModule pump))
        {
            if (pump.IsLoaded && pump.SyncCocked > 0)
            {
                yield break;
            }

            if (!TryInvokeDummyActionForSerial(
                    actor.Hub, firearm.ItemSerial, "Reload->Click", out _))
            {
                throw new RequireException(
                    $"Attack: no exact item-serial #{firearm.ItemSerial} native Reload action for {actor.Label}'s {firearm.ItemTypeId}");
            }

            double reloadDeadline = ctx.ElapsedSeconds + 12f;
            bool sawReload = false;
            while (ctx.ElapsedSeconds < reloadDeadline)
            {
                bool reloading = firearm.TryGetModule(out IReloaderModule reloader) && reloader.IsReloading;
                sawReload |= reloading;
                if (sawReload && !reloading)
                {
                    break;
                }

                yield return Timing.WaitForSeconds(0.1f);
            }

            if (!pump.IsLoaded || pump.SyncCocked <= 0)
            {
                int magAmmo = firearm.TryGetModule(out IPrimaryAmmoContainerModule pumpMagazine)
                    ? pumpMagazine.AmmoStored
                    : -1;
                throw new RequireException(
                    $"Attack: {firearm.ItemTypeId} held by {actor.Label} is NOT ready after native Reload input " +
                    $"(loaded={pump.IsLoaded} cocked={pump.SyncCocked} chambered={pump.AmmoStored} magAmmo={magAmmo}) — " +
                    "refusing to fabricate pump-action state");
            }

            yield return Timing.WaitForSeconds(0.2f);
            yield break;
        }

        if (!firearm.TryGetModule(out AutomaticActionModule action))
        {
            // Revolvers and disruptors fire from their own module state; the registered Shoot click
            // handles them.
            yield break;
        }

        if (action.IsLoaded && action.Cocked && !action.BoltLocked)
        {
            yield break;
        }

        // Native reload/rack input: the real reloader decides whether and how to fill/cycle.
        if (TryInvokeDummyActionForSerial(
                actor.Hub, firearm.ItemSerial, "Reload->Click", out _))
        {
            double reloadDeadline = ctx.ElapsedSeconds + 12f;
            bool sawReload = false;
            while (ctx.ElapsedSeconds < reloadDeadline)
            {
                bool reloading = firearm.TryGetModule(out IReloaderModule reloader) && reloader.IsReloading;
                sawReload |= reloading;
                if (sawReload && !reloading)
                {
                    break;
                }

                yield return Timing.WaitForSeconds(0.1f);
            }
        }

        // NEVER fabricate readiness (PT-004): if the native reload input did not produce a ready
        // firearm, fail with the exact stuck state instead of calling server state-transition
        // methods or writing their backing state.
        if (!action.IsLoaded || !action.Cocked || action.BoltLocked)
        {
            int magAmmo = firearm.TryGetModule(out IPrimaryAmmoContainerModule magCheck) ? magCheck.AmmoStored : -1;
            throw new RequireException(
                $"Attack: {firearm.ItemTypeId} held by {actor.Label} is NOT ready after native Reload input " +
                $"(loaded={action.IsLoaded} openBolt={action.OpenBolt} cocked={action.Cocked} boltLocked={action.BoltLocked} chambered={action.AmmoStored} magAmmo={magAmmo}) — " +
                "refusing to fabricate firearm state; fix the reload path or the scenario's ammo arrangement");
        }

        yield return Timing.WaitForSeconds(0.2f);
    }

    private static IEnumerator<float> MeleeAttackBody(ScenarioContext ctx, Actor attacker, Actor victim,
        Func<bool> hitConfirmed, Action armConfirmation, Action closeConfirmation)
    {
        float attackTimeout = ctx.Config.AttackTimeoutSeconds;
        double deadline = ctx.ElapsedSeconds + attackTimeout;
        int attempts = 0;
        string actionName = MeleeActionName(attacker);

        while (!hitConfirmed())
        {
            if (ctx.ElapsedSeconds >= deadline)
            {
                throw new RequireException(
                    $"Attack(melee): {attacker.Label} made {attempts} attempt(s) on {victim.Label} without a confirmed hit within {attackTimeout:0.#}s");
            }

            if (victim.Role == RoleTypeId.Spectator || victim.Role == RoleTypeId.None || victim.Health <= 0f)
            {
                throw new RequireException(
                    $"Attack(melee): victim {victim.Label} died/despawned ({victim.Role}) before a hit by {attacker.Label} was confirmed — another source killed it first");
            }

            // Approach into melee range first (tier-correct movement).
            if (Vector3.Distance(attacker.Position, victim.Position) > MeleeRange)
            {
                // WalkTo carries the tier semantics itself (quick TPs, standard+ walks natively).
                Vector3 approach = victim.Position + (attacker.Position - victim.Position).normalized * (MeleeRange * 0.6f);
                foreach (float wait in Coroutines.Pump(MovementFulfiller.WalkTo(ctx, attacker, approach)))
                {
                    yield return wait;
                }
            }

            foreach (float wait in Coroutines.Pump(LookAt(ctx, attacker, AimPoint(victim.Hub))))
            {
                yield return wait;
            }

            armConfirmation();
            if (!TryInvokeDummyAction(attacker.Hub, actionName))
            {
                closeConfirmation();
                throw new RequireException(
                    $"Attack(melee): dummy action '{actionName}' unavailable for {attacker.Label} ({attacker.Role})");
            }

            attempts++;
            yield return Timing.WaitForSeconds(1.0f);
            if (!hitConfirmed())
            {
                closeConfirmation();
            }
        }
    }

    // ---- dummy-action helper ------------------------------------------------------------------

    internal static bool TryInvokeDummyActionForSerial(
        ReferenceHub hub,
        ushort serial,
        string actionName,
        out string categoryName)
    {
        List<DummyAction> actions = DummyActionCollector.ServerGetActions(hub);
        string serialMarker = $"(#{serial})";
        bool inSerialCategory = false;
        categoryName = string.Empty;
        foreach (DummyAction action in actions)
        {
            if (action.Action == null)
            {
                inSerialCategory = action.Name.IndexOf(serialMarker, StringComparison.OrdinalIgnoreCase) >= 0;
                if (inSerialCategory)
                {
                    categoryName = action.Name;
                }

                continue;
            }

            if (inSerialCategory && string.Equals(action.Name, actionName, StringComparison.OrdinalIgnoreCase))
            {
                action.Action.Invoke();
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Invokes a native dummy action by name (optionally "Category:Action"). Actions come from
    /// DummyActionCollector.ServerGetActions — the same registry the RA dummy panel uses, so
    /// invoking one is byte-identical to a human clicking it.
    /// </summary>
    internal static bool TryInvokeDummyAction(ReferenceHub hub, string actionName)
    {
        List<DummyAction> actions = DummyActionCollector.ServerGetActions(hub);
        string category = string.Empty;
        string name = actionName;
        int sep = actionName.IndexOf(':');
        if (sep > 0)
        {
            category = actionName.Substring(0, sep);
            name = actionName.Substring(sep + 1);
        }

        bool inCategory = category.Length == 0;
        foreach (DummyAction action in actions)
        {
            if (action.Action == null)
            {
                // Category header row.
                inCategory = category.Length == 0 || action.Name.IndexOf(category, StringComparison.OrdinalIgnoreCase) >= 0;
                continue;
            }

            if (inCategory && string.Equals(action.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                action.Action.Invoke();
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Key-RELEASE dispatch for cleanup paths. A failed release latches the emulated key (a reused
    /// dummy re-equipping the item spontaneously resumes the held input), so unlike the best-effort
    /// call sites this logs an error line instead of silently ignoring the result.
    /// </summary>
    internal static void ReleaseDummyActionOrWarn(ReferenceHub hub, string actorLabel, string actionName)
    {
        bool released;
        try
        {
            released = TryInvokeDummyAction(hub, actionName);
        }
        catch (Exception e)
        {
            EventLog.Line("STEP",
                $"actor {actorLabel}: key-release '{actionName}' threw {e.GetType().Name}: {e.Message} — emulated key may remain HELD",
                error: true);
            return;
        }

        if (!released)
        {
            EventLog.Line("STEP",
                $"actor {actorLabel}: key-release '{actionName}' found no matching dummy action — emulated key may remain HELD",
                error: true);
        }
    }

    // ---- quick-only plumbing -------------------------------------------------------------------

    internal static void DirectDamage(ScenarioContext ctx, Actor attacker, Actor victim, float amount)
    {
        LabApi.Features.Wrappers.Player? victimPlayer = LabApi.Features.Wrappers.Player.Get(victim.Hub);
        LabApi.Features.Wrappers.Player? attackerPlayer = LabApi.Features.Wrappers.Player.Get(attacker.Hub);
        if (victimPlayer == null)
        {
            throw new RequireException($"Damage: victim {victim.Label} has no Player wrapper");
        }

        // armorPenetration:100 — the armorPen=0 overload silently deals ZERO damage (repo memory).
        victimPlayer.Damage(amount, attackerPlayer!, default, 100);
        ctx.Emit(new TelemetryEvent("direct_damage", null, new Dictionary<string, object?>
        {
            ["attacker"] = attacker.Label,
            ["victim"] = victim.Label,
            ["amount"] = amount,
        }));
    }
}
