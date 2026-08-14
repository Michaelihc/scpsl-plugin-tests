using System.Collections.Generic;
using System.Linq;
using CustomPlayerEffects;
using MapGeneration;
using PlayerRoles;
using PlayerRoles.FirstPersonControl;
using PlaytestHarness.Core;
using UnityEngine;

namespace PlaytestHarness.Actors;

/// <summary>
/// A harness-owned dummy with fidelity-adaptive verbs (the plan's orders API). BATTERIES-INCLUDED
/// CONTRACT: all aim/rotation/weapon-state/hit-confirm/movement mechanics live inside these verbs —
/// scenarios call one-liners and NEVER hand-roll Quaternion/raycast/fire logic (a scenario found
/// doing so is a defect; the fix is a new verb here).
///
/// Verb call pattern (same as ctx.WaitUntil): each async verb registers a pending sub-enumerator
/// that the runner pumps inline, so Require failures and timeouts inside verbs surface as proper
/// scenario FAILs. Usage: <c>yield return actor.Settle();</c>
/// </summary>
public sealed class Actor
{
    private readonly ScenarioContext _ctx;

    internal Actor(ScenarioContext ctx, ReferenceHub hub, string label, RoleTypeId role, SpawnSpec spec)
    {
        _ctx = ctx;
        Hub = hub;
        Label = label;
        RequestedRole = role;
        Spec = spec;
    }

    /// <summary>
    /// Raw game object for harness fulfillers/monitors only. External scenario assemblies receive
    /// the capability-limited Actor surface, not a mutable ReferenceHub that can bypass fidelity.
    /// </summary>
    internal ReferenceHub Hub { get; }

    /// <summary>Scenario-facing label (also part of the dummy nickname).</summary>
    public string Label { get; }

    public RoleTypeId RequestedRole { get; }

    internal SpawnSpec Spec { get; }

    /// <summary>Set true once WaitReady completed (role assigned + placement done + settled).</summary>
    public bool IsReady { get; internal set; }

    /// <summary>
    /// Provider-less spawns defer ServerSetRole to WaitReady (+1 frame): a same-frame role set
    /// races PlayerAuthenticationManager.Start's UserId assignment (null-key NRE in keycard loadouts).
    /// </summary>
    internal bool NeedsDeferredRoleSet { get; set; }

    public Vector3 Position => Hub.transform.position;

    public float Health => Hub.playerStats.GetModule<PlayerStatsSystem.HealthStat>().CurValue;

    public float MaxHealth => Player?.MaxHealth ?? 0f;

    public float ArtificialHealth => Player?.ArtificialHealth ?? 0f;

    public float MaxArtificialHealth => Player?.MaxArtificialHealth ?? 0f;

    /// <summary>Immutable snapshot of inventory item types at the moment the property is read.</summary>
    public IReadOnlyList<ItemType> ItemTypes => Hub.inventory.UserInventory.Items.Values
        .Where(item => item != null)
        .Select(item => item.ItemTypeId)
        .ToArray();

    /// <summary>Immutable exact type+serial inventory snapshot at the moment the property is read.</summary>
    public IReadOnlyList<InventoryEntry> Inventory => Hub.inventory.UserInventory.Items.Values
        .Where(item => item != null)
        .Select(item => new InventoryEntry(item.ItemTypeId, item.ItemSerial))
        .ToArray();

    /// <summary>Read-only native player id for plugin-local semantic adapters.</summary>
    public int PlayerId => Hub.PlayerId;

    public int ItemCount => Hub.inventory.UserInventory.Items.Count;

    public ItemType HeldItemType => Hub.inventory.CurItem.TypeId;

    public ushort HeldItemSerial => Hub.inventory.CurItem.SerialNumber;

    private LabApi.Features.Wrappers.Player? Player => LabApi.Features.Wrappers.Player.Get(Hub);

    /// <summary>Read-only native status-effect observation.</summary>
    public bool HasEffect<TEffect>() where TEffect : StatusEffectBase => Player?.HasEffect<TEffect>() == true;

    /// <summary>Null-safe: fresh dummies have a null CurrentRole for the first frames.</summary>
    public RoleTypeId Role => Hub.roleManager?.CurrentRole?.RoleTypeId ?? RoleTypeId.None;

    public string RoomName => Probes.PlacementProbes.RoomNameAt(Position);

    internal CharacterControllerSettingsPreset CapsuleSettings
    {
        get
        {
            IFpcRole fpc = Hub.roleManager.CurrentRole as IFpcRole
                ?? throw new RequireException($"actor {Label} has no FPC role ({Role}) — capsule settings unavailable");
            return fpc.FpcModule.CharacterControllerSettings;
        }
    }

    // ---- lifecycle -------------------------------------------------------------------------

    /// <summary>Completes the spawn: role assignment, placement per SpawnSpec + tier, settle.</summary>
    public float WaitReady() => _ctx.RegisterPending(MovementFulfiller.CompleteSpawn(_ctx, this));

    // ---- movement --------------------------------------------------------------------------

    /// <summary>
    /// Travel to an objective. Quick: raw TP. Standard: fast-travel (probe-validated TP + settle;
    /// invalid target = loud FAIL naming the probe). EndToEnd: walked via the movement provider.
    /// </summary>
    public float GoTo(Vector3 target) => _ctx.RegisterPending(MovementFulfiller.GoTo(_ctx, this, target));

    /// <summary>GoTo a room: the room resolves to a probed safe point inside it.</summary>
    public float GoTo(RoomName room) => _ctx.RegisterPending(MovementFulfiller.GoToRoom(_ctx, this, room));

    /// <summary>Finds a ground- and capsule-safe point on concentric rings around a live world feature.</summary>
    public Vector3 FindSafePointNear(Vector3 center, float maxRadius)
        => MovementFulfiller.FindSafePointNear(_ctx, this, center, maxRadius);

    /// <summary>
    /// Local movement AT the objective — walked for real at Standard+ (provider absent =&gt; SKIP);
    /// Quick may TP.
    /// </summary>
    public float WalkTo(Vector3 target) => _ctx.RegisterPending(MovementFulfiller.WalkTo(_ctx, this, target));

    /// <summary>
    /// Walks an ordered local waypoint route with the same native/provider and ground-monitoring rules
    /// as WalkTo. Useful when the direct chord crosses authored walls but the player corridor is open.
    /// </summary>
    public float WalkRoute(IReadOnlyList<Vector3> waypoints)
        => _ctx.RegisterPending(MovementFulfiller.WalkRoute(_ctx, this, waypoints));

    /// <summary>
    /// Walks through a real room-connected door until the actor physically crosses a circular
    /// world boundary. Candidate doors, native pathing, door interaction, and grounding stay in the harness.
    /// </summary>
    public float WalkBeyondBoundary(Vector3 center, float radius, float doorMargin = 2.5f)
        => _ctx.RegisterPending(MovementFulfiller.WalkBeyondBoundary(_ctx, this, center, radius, doorMargin));

    public float WalkTo(RoomName room) => _ctx.RegisterPending(MovementFulfiller.WalkToRoom(_ctx, this, room));

    /// <summary>
    /// Waits until grounded (&lt;= timeout) and asserts the actor did not fall beyond maxDrop.
    /// Negative arguments use the configured defaults (settle_max_drop_meters / settle_timeout_seconds).
    /// </summary>
    public float Settle(float maxDrop = -1f, float timeoutSeconds = -1f)
        => _ctx.RegisterPending(MovementFulfiller.Settle(_ctx, this, maxDrop, timeoutSeconds));

    /// <summary>Leaves the actor alone for N seconds with continuous ground+bounds monitoring.</summary>
    public float Soak(float seconds) => _ctx.RegisterPending(MovementFulfiller.Soak(_ctx, this, seconds));

    /// <summary>
    /// Drives fixed-duration native local movement and records actual position-derived speed. This is
    /// intentionally not a navigation order: slowing effects must be allowed to produce little or no
    /// progress without being mislabeled as a stalled path.
    /// </summary>
    public float MeasureWalkingSpeed(
        Vector3 worldDirection,
        float durationSeconds = 2f,
        bool allowDirectionFallback = true)
        => _ctx.RegisterPending(MovementFulfiller.MeasureWalkingSpeed(
            _ctx,
            this,
            worldDirection,
            durationSeconds,
            allowDirectionFallback));

    /// <summary>The immutable result of the last completed MeasureWalkingSpeed call.</summary>
    public MovementMeasurement? LastMovementMeasurement { get; internal set; }

    /// <summary>
    /// Performs a harness-authorized native role assignment without a role spawn teleport. Standard
    /// requires an arrangement scope; EndToEnd forbids it. Intended for lifecycle cleanup probes.
    /// </summary>
    public float ChangeRole(RoleTypeId role)
        => _ctx.RegisterPending(MovementFulfiller.ChangeRole(_ctx, this, role));

    // ---- native actions --------------------------------------------------------------------

    /// <summary>Aims camera pitch+yaw at a world position via the dummy's native FPC mouse-look.</summary>
    public float LookAt(Vector3 worldPoint) => _ctx.RegisterPending(CombatFulfiller.LookAt(_ctx, this, worldPoint));

    /// <summary>
    /// Aims the authoritative server interaction ray through an authored oriented box and proves the
    /// hit before returning. This handles headless-dummy camera/look-rotation synchronization traps.
    /// </summary>
    public float AimAt(WorldInteractionBox target)
        => _ctx.RegisterPending(CombatFulfiller.AimAtBox(_ctx, this, target));

    /// <summary>Aims at another actor's centre-mass.</summary>
    public float LookAt(Actor target) => _ctx.RegisterPending(CombatFulfiller.LookAt(_ctx, this, CombatFulfiller.AimPoint(target.Hub)));

    /// <summary>
    /// Fires a real native firearm shot at another actor and requires the final Hurting decision to
    /// cancel it with no health loss. This is the blocked counterpart to <see cref="Attack"/>.
    /// </summary>
    public float AttackExpectingBlocked(Actor victim)
        => _ctx.RegisterPending(CombatFulfiller.AttackExpectingBlocked(_ctx, this, victim));

    /// <summary>
    /// Fires through the native automatic-firearm server action while explicitly identifying the
    /// intended primary target. Use only for same-faction headless controls whose dummy backtrack
    /// request cannot reliably select a friendly hitbox.
    /// </summary>
    public float AttackTargeted(Actor victim)
        => _ctx.RegisterPending(CombatFulfiller.AttackTargeted(_ctx, this, victim));

    /// <summary>
    /// Adds an item to the actor's inventory (native ServerAddItem; ammo topped up for firearms).
    /// ARRANGEMENT-AWARE: Quick unrestricted; Standard only inside ctx.Arrange(reason, ...) —
    /// the pre-arranged-state shortcut, telemetry-logged; EndToEnd always rejected.
    /// </summary>
    public void GiveItem(ItemType itemType) => CombatFulfiller.GiveItem(_ctx, this, itemType);

    /// <summary>
    /// Pre-arranges a durable target health pool. Quick is unrestricted; Standard requires
    /// ctx.Arrange; EndToEnd rejects it, matching every other pre-arranged-state mutation.
    /// </summary>
    public void SetHealth(float health) => CombatFulfiller.SetHealth(_ctx, this, health);

    /// <summary>Equips an inventory item of the given type through the native selection flow (waits the draw).</summary>
    public float Equip(ItemType itemType) => _ctx.RegisterPending(CombatFulfiller.EquipByType(_ctx, this, itemType));

    /// <summary>Equips one exact inventory serial through the native selection flow (waits the draw).</summary>
    public float EquipSerial(ushort serial) => _ctx.RegisterPending(CombatFulfiller.EquipBySerial(_ctx, this, serial));

    /// <summary>
    /// Dispatches one native exact-serial selection request and completes after the event route ran,
    /// even when a feature intentionally cancels the equip (for example an inert catalogue preview).
    /// </summary>
    public float AttemptSelectSerial(ushort serial)
        => _ctx.RegisterPending(CombatFulfiller.AttemptSelectSerial(_ctx, this, serial));

    /// <summary>
    /// Uses the currently held usable item through the native usable-item flow — waits the item's
    /// REAL use time (cooldowns/charge real). Fails loudly if the use never completes.
    /// </summary>
    public float UseHeldItem() => _ctx.RegisterPending(CombatFulfiller.UseHeldItem(_ctx, this));

    /// <summary>
    /// Throws the currently held throwable through its native arm-animation and server confirmation
    /// lifecycle. Aim, release pose, exact-serial event confirmation, and cleanup tracking stay inside
    /// the harness rather than being reimplemented by scenarios.
    /// </summary>
    public float ThrowHeldItemAt(Vector3 target, bool fullForce = true)
        => _ctx.RegisterPending(InventoryFulfiller.ThrowHeldItemAt(_ctx, this, target, fullForce));

    /// <summary>Throws the currently held throwable at another actor's live centre-mass.</summary>
    public float ThrowHeldItemAt(Actor target, bool fullForce = true)
        => _ctx.RegisterPending(InventoryFulfiller.ThrowHeldItemAt(_ctx, this, target, fullForce));

    /// <summary>
    /// Drops the currently selected item through the native RA-dummy Inventory "Drop" action and
    /// returns a read-only handle to the exact serial once the world pickup exists.
    /// </summary>
    public float DropHeldItem() => _ctx.RegisterPending(InventoryFulfiller.DropHeldItem(_ctx, this));

    /// <summary>The exact pickup created by the last successful DropHeldItem call, or null.</summary>
    public WorldItem? LastDroppedItem { get; internal set; }

    /// <summary>
    /// Picks up an exact world item through the native search-completion lifecycle. The actor must
    /// physically be within the game's search range; the verb aims at the pickup and waits real search time.
    /// </summary>
    public float PickUp(WorldItem item) => _ctx.RegisterPending(InventoryFulfiller.PickUp(_ctx, this, item));

    /// <summary>
    /// Drives a Server-Specific-Settings keybind through ServerOnSettingValueReceived (press + release),
    /// the same rising/falling-edge route a live client uses. Hold duration is real wall-clock time.
    /// </summary>
    public float PressServerSetting(int settingId, float holdSeconds = 0.1f)
        => _ctx.RegisterPending(InputFulfiller.PressServerSetting(_ctx, this, settingId, holdSeconds));

    /// <summary>
    /// Config-tunable watch window that continuously asserts this actor remains live, grounded, and
    /// in bounds. Intended for spectatable scenario beats before the cleanup barrier runs.
    /// </summary>
    public float HoldForViewing() => _ctx.RegisterPending(MovementFulfiller.Soak(_ctx, this, _ctx.Config.ViewingHoldSeconds));

    /// <summary>
    /// Uses the held item's native targeted lifecycle. The adapter handles range/LOS/aim and must
    /// observe an attributable gameplay effect on <paramref name="target"/> before succeeding.
    /// Currently supported for the Micro H.I.D.; other targeted item families fail loudly until an
    /// adapter is added.
    /// </summary>
    public float UseHeldItemOn(Actor target) => _ctx.RegisterPending(CombatFulfiller.UseHeldItemOn(_ctx, this, target));

    /// <summary>Holds the native Micro H.I.D. secondary input and requires the cycle to remain Standby.</summary>
    public float AttemptDisabledMicroHidSecondary(float holdSeconds = 0.5f)
        => _ctx.RegisterPending(CombatFulfiller.AttemptDisabledMicroHidSecondary(_ctx, this, holdSeconds));

    /// <summary>
    /// Starts the native Micro H.I.D. wind-up, then selects another inventory item while the key is
    /// still held. Success proves the equipped HID is holsterable outside Standby.
    /// </summary>
    public float InterruptMicroHidWithEquip(ItemType switchTo)
        => _ctx.RegisterPending(CombatFulfiller.InterruptMicroHidWithEquip(_ctx, this, switchTo));

    /// <summary>
    /// Attacks a victim with real ballistics: equips/readies a firearm, aims at the victim's
    /// hitbox, fires via the native firearm path, and confirms the hit (health delta / Hurt event)
    /// with retry-until-timeout. SCP roles use their native melee ability after approaching into range.
    /// </summary>
    public float Attack(Actor victim) => _ctx.RegisterPending(CombatFulfiller.Attack(_ctx, this, victim));

    /// <summary>
    /// Attacks a damageable world feature using native firearm hit registration or this SCP role's
    /// native melee input, and requires an attributable damage confirmation.
    /// </summary>
    public float Attack(WorldDamageTarget target)
        => _ctx.RegisterPending(WorldCombatFulfiller.Attack(_ctx, this, target));

    /// <summary>
    /// Sends SCP-173's native Breakneck input and proves either the completed transition or an actual
    /// cancellable-event rejection.
    /// </summary>
    public float AttemptBreakneck(AbilityExpectedOutcome expected)
        => _ctx.RegisterPending(ScpAbilityFulfiller.AttemptBreakneck(_ctx, this, expected));

    /// <summary>
    /// Sends SCP-106's native Hunter's Atlas server payload and proves either the completed teleport or
    /// an actual cancellable-event rejection.
    /// </summary>
    public float AttemptHuntersAtlas(Vector3 mapSelection, AbilityExpectedOutcome expected)
        => _ctx.RegisterPending(ScpAbilityFulfiller.AttemptHuntersAtlas(_ctx, this, mapSelection, expected));

    /// <summary>
    /// Direct stat damage — Quick-ONLY plumbing. Throws at Standard+ (damage happens by shooting
    /// dummies, nothing else — plan tier table).
    /// </summary>
    public void Damage(Actor victim, float amount)
    {
        if (_ctx.Fidelity != Fidelity.Quick)
        {
            throw new System.InvalidOperationException(
                $"Actor.Damage is Quick-only plumbing; at {_ctx.Fidelity} damage must come from real shots (use Attack).");
        }

        CombatFulfiller.DirectDamage(_ctx, this, victim, amount);
    }

    // ---- probes (assert-style, usable from scenarios as one-liners) -------------------------

    /// <summary>
    /// Standard+: asserts that fast-travel validation REJECTS the target (tier-contrast demo — the
    /// mid-air-spawn bug class caught by design). Logs the failing probe. The actor does not move.
    /// </summary>
    public void AssertGoToRejected(Vector3 target)
    {
        if (_ctx.Fidelity == Fidelity.Quick)
        {
            throw new System.InvalidOperationException("AssertGoToRejected is meaningful at Standard+ only (quick does not probe).");
        }

        Probes.ProbeResult result = Probes.PlacementProbes.ValidateTeleportTarget(target, CapsuleSettings, _ctx.Config.FastTravelMaxDropMeters, Hub);
        if (result.Ok)
        {
            throw new RequireException(
                $"fast-travel validation ACCEPTED deliberately-bad target {Probes.PlacementProbes.Format(target)} — probes failed to catch it");
        }

        Telemetry.EventLog.Line("PROBE", $"actor={Label} fast-travel target REJECTED as designed: {result}", error: true);
        _ctx.Emit(new Telemetry.TelemetryEvent("goto_rejected", result.ToString(), new Dictionary<string, object?>
        {
            ["actor"] = Label,
            ["probe"] = result.FailedProbe,
        }));
    }
}

/// <summary>
/// Immutable authored world interaction volume. The harness owns aim math and proves the same
/// LookRotation-based ray used by server-side features intersects this exact oriented box.
/// Construct via the factories only — the batteries-included contract forbids scenario-side
/// Quaternion math, so orientation is expressed as yaw degrees or derived from live geometry here.
/// </summary>
public sealed class WorldInteractionBox
{
    private WorldInteractionBox(string label, Vector3 center, Quaternion rotation, Vector3 size, float maxDistance)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            throw new System.ArgumentException("Interaction box label is required.", nameof(label));
        }

        if (size.x <= 0f || size.y <= 0f || size.z <= 0f || maxDistance <= 0f)
        {
            throw new System.ArgumentOutOfRangeException(nameof(size), "Interaction box size and range must be positive.");
        }

        Label = label;
        Center = center;
        Rotation = rotation;
        Size = size;
        MaxDistance = maxDistance;
    }

    /// <summary>A world-axis-aligned box (no orientation input needed).</summary>
    public static WorldInteractionBox AxisAligned(string label, Vector3 center, Vector3 size, float maxDistance)
        => new(label, center, Quaternion.identity, size, maxDistance);

    /// <summary>A box rotated around world Y only; scenarios pass degrees, never quaternions.</summary>
    public static WorldInteractionBox Yawed(string label, Vector3 center, float yawDegrees, Vector3 size, float maxDistance)
        => new(label, center, Quaternion.Euler(0f, yawDegrees, 0f), size, maxDistance);

    /// <summary>
    /// A box whose local +Z faces along a world direction (up stays world Y) — for volumes aligned
    /// to a feature's facing vector without scenario-side quaternion construction.
    /// </summary>
    public static WorldInteractionBox Facing(string label, Vector3 center, Vector3 worldForward, Vector3 size, float maxDistance)
    {
        Vector3 flat = new(worldForward.x, 0f, worldForward.z);
        if (flat.sqrMagnitude < 1e-6f)
        {
            throw new System.ArgumentException("Facing requires a non-vertical world direction.", nameof(worldForward));
        }

        return new WorldInteractionBox(label, center, Quaternion.LookRotation(flat.normalized, Vector3.up), size, maxDistance);
    }

    /// <summary>
    /// Derives center/orientation/world-scaled size from a live BoxCollider so feature adapters
    /// hand over real geometry instead of reimplementing transform math.
    /// </summary>
    public static WorldInteractionBox FromBoxCollider(string label, BoxCollider collider, float maxDistance)
    {
        if (collider == null)
        {
            throw new System.ArgumentNullException(nameof(collider));
        }

        Vector3 lossyScale = collider.transform.lossyScale;
        Vector3 size = Vector3.Scale(
            collider.size,
            new Vector3(Mathf.Abs(lossyScale.x), Mathf.Abs(lossyScale.y), Mathf.Abs(lossyScale.z)));
        return new WorldInteractionBox(
            label,
            collider.transform.TransformPoint(collider.center),
            collider.transform.rotation,
            size,
            maxDistance);
    }

    public string Label { get; }
    public Vector3 Center { get; }
    public Quaternion Rotation { get; }
    public Vector3 Size { get; }
    public float MaxDistance { get; }
}

/// <summary>Immutable actual-displacement measurement from one native local-input walk.</summary>
public sealed class MovementMeasurement
{
    internal MovementMeasurement(
        Vector3 start,
        Vector3 end,
        Vector3 direction,
        float elapsedSeconds,
        float projectedDistance,
        float pathDistance,
        string inputPath)
    {
        Start = start;
        End = end;
        Direction = direction;
        ElapsedSeconds = elapsedSeconds;
        ProjectedDistance = projectedDistance;
        PathDistance = pathDistance;
        InputPath = inputPath;
    }

    public Vector3 Start { get; }

    public Vector3 End { get; }

    public Vector3 Direction { get; }

    public float ElapsedSeconds { get; }

    public float ProjectedDistance { get; }

    public float PathDistance { get; }

    /// <summary>The native movement input route used for this measurement.</summary>
    public string InputPath { get; }

    public float AverageProjectedSpeed => ElapsedSeconds > 0f ? ProjectedDistance / ElapsedSeconds : 0f;
}

/// <summary>Read-only exact inventory identity captured from one actor snapshot.</summary>
public sealed class InventoryEntry
{
    internal InventoryEntry(ItemType type, ushort serial)
    {
        Type = type;
        Serial = serial;
    }

    public ItemType Type { get; }

    public ushort Serial { get; }
}
