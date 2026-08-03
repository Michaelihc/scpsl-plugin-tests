using System.Linq;
using PlayerRoles.FirstPersonControl;
using UnityEngine;

namespace PlaytestHarness.Probes;

/// <summary>
/// Result of a placement probe. <see cref="Ok"/> false carries the exact failed check in
/// <see cref="FailedProbe"/> + <see cref="Detail"/> so fast-travel failures name their cause.
/// </summary>
public readonly struct ProbeResult
{
    public bool Ok { get; }

    /// <summary>Short probe id: "ground", "bounds", "capsule", "settle".</summary>
    public string? FailedProbe { get; }

    public string? Detail { get; }

    private ProbeResult(bool ok, string? failedProbe, string? detail)
    {
        Ok = ok;
        FailedProbe = failedProbe;
        Detail = detail;
    }

    public static ProbeResult Pass() => new(true, null, null);

    public static ProbeResult Fail(string probe, string detail) => new(false, probe, detail);

    public override string ToString() => Ok ? "ok" : $"probe={FailedProbe} {Detail}";
}

/// <summary>
/// Shared placement probe library — patterns lifted from the proven reinforcements
/// SpawnPositionVerifier (ground raycast on FpcStateProcessor.Mask excluding hub/hitbox colliders,
/// walkable normal.y &gt;= 0.65, capsule overlap from CharacterControllerSettings) and the SCP999
/// lifecycle verifier (settle-and-assert-Y). Rewritten clean per the v2 plan; no code reuse.
/// </summary>
public static class PlacementProbes
{
    /// <summary>Walkable surface slope threshold (normal.y).</summary>
    public const float WalkableNormalY = 0.65f;

    /// <summary>
    /// Raycasts down from slightly above <paramref name="position"/> for walkable ground within
    /// <paramref name="maxDrop"/> meters, on the FPC collision mask, ignoring player capsules and
    /// hitboxes. Returns hit ground Y through <paramref name="groundY"/> when found.
    /// </summary>
    public static bool TryFindGround(Vector3 position, float maxDrop, out float groundY, out string groundName)
    {
        RaycastHit? ground = Physics.RaycastAll(
                position + Vector3.up * 0.3f,
                Vector3.down,
                maxDrop + 0.3f,
                FpcStateProcessor.Mask,
                QueryTriggerInteraction.Ignore)
            .OrderBy(static hit => hit.distance)
            .Cast<RaycastHit?>()
            .FirstOrDefault(hit => hit.HasValue && hit.Value.collider != null &&
                hit.Value.collider.GetComponentInParent<ReferenceHub>() == null &&
                hit.Value.collider.GetComponentInParent<HitboxIdentity>() == null &&
                hit.Value.normal.y >= WalkableNormalY);
        if (!ground.HasValue)
        {
            groundY = float.NaN;
            groundName = "none";
            return false;
        }

        groundY = ground.Value.point.y;
        groundName = ground.Value.collider!.name;
        return true;
    }

    /// <summary>Ground probe as a ProbeResult: walkable ground within maxDrop below position.</summary>
    public static ProbeResult Ground(Vector3 position, float maxDrop = 3f)
    {
        if (!TryFindGround(position, maxDrop, out float groundY, out _))
        {
            return ProbeResult.Fail("ground",
                $"no walkable ground within {maxDrop:0.##}m below {Format(position)}");
        }

        float drop = position.y - groundY;
        return drop > maxDrop
            ? ProbeResult.Fail("ground", $"ground is {drop:0.##}m below target (max {maxDrop:0.##}m) at {Format(position)}")
            : ProbeResult.Pass();
    }

    /// <summary>
    /// Lenient room probe: position resolves to a facility room via RoomUtils.TryGetRoom (grid
    /// coords + up/down raycast fallback). Used by soak/void-fall detection where doorway
    /// thresholds and connector geometry must NOT false-fail.
    /// </summary>
    public static ProbeResult Bounds(Vector3 position)
    {
        return MapGeneration.RoomUtils.TryGetRoom(position, out MapGeneration.RoomIdentifier room) && room != null
            ? ProbeResult.Pass()
            : ProbeResult.Fail("bounds", $"position {Format(position)} does not resolve to any room (TryGetRoom failed)");
    }

    /// <summary>
    /// STRICT room-volume probe for teleport validation: the position must lie inside some room's
    /// actual WorldspaceBounds AABB (native TryGetCurrentRoomByBounds). TryGetRoom alone is NOT
    /// sufficient here — its 15x100x15 grid rounding + raycast fallback can resolve a room tens of
    /// meters away vertically or several meters beyond its AABB (decompiled RoomUtils.TryGetRoom),
    /// which would certify fast-travel targets outside the real room volume.
    /// </summary>
    public static ProbeResult BoundsStrict(Vector3 position)
    {
        return MapGeneration.RoomUtils.TryGetCurrentRoomByBounds(position, out MapGeneration.RoomIdentifier room) && room != null
            ? ProbeResult.Pass()
            : ProbeResult.Fail("bounds", $"position {Format(position)} is outside every room's WorldspaceBounds");
    }

    /// <summary>Room name at position, or "none".</summary>
    public static string RoomNameAt(Vector3 position)
    {
        return MapGeneration.RoomUtils.TryGetRoom(position, out MapGeneration.RoomIdentifier room) && room != null
            ? room.Name.ToString()
            : "none";
    }

    /// <summary>
    /// Expected FPC root (player Position) height above the ground for a settled capsule —
    /// same formula the reinforcements verifier proved in-game.
    /// </summary>
    public static float ExpectedRootHeight(CharacterControllerSettingsPreset settings)
        => settings.Height * 0.5f - settings.Center.y + settings.SkinWidth + 0.04f;

    /// <summary>
    /// Capsule-clearance probe: a standing capsule from the role's CharacterControllerSettings with
    /// its FPC root (player Position semantics — mid-capsule) at <paramref name="rootPosition"/> must
    /// not overlap world geometry OR another player. Only the actor being placed
    /// (<paramref name="ignoreHub"/>) and its own hitboxes are excluded (PT-009: excluding EVERY
    /// hub certified occupied targets as safe and teleported actors into each other).
    /// </summary>
    public static ProbeResult Capsule(Vector3 rootPosition, CharacterControllerSettingsPreset settings, ReferenceHub? ignoreHub = null)
    {
        Vector3 capsuleCentre = rootPosition + settings.Center;
        float radius = Mathf.Max(0.2f, settings.Radius - Mathf.Min(0.04f, settings.SkinWidth * 0.5f));
        float segment = Mathf.Max(0f, settings.Height * 0.5f - settings.Radius);
        Collider obstruction = Physics.OverlapCapsule(
                capsuleCentre - Vector3.up * segment,
                capsuleCentre + Vector3.up * segment,
                radius,
                FpcStateProcessor.Mask,
                QueryTriggerInteraction.Ignore)
            .FirstOrDefault(collider =>
            {
                if (collider == null)
                {
                    return false;
                }

                ReferenceHub? hub = collider.GetComponentInParent<ReferenceHub>();
                if (hub == null)
                {
                    HitboxIdentity? hitbox = collider.GetComponentInParent<HitboxIdentity>();
                    hub = hitbox != null ? hitbox.TargetHub : null;
                }

                // World geometry (no hub anywhere in the parents) obstructs; the placed actor's
                // own capsule/hitboxes do not; ANY OTHER player's do.
                return hub == null || hub != ignoreHub;
            });
        return obstruction == null
            ? ProbeResult.Pass()
            : ProbeResult.Fail("capsule", $"standing capsule at {Format(rootPosition)} overlaps '{obstruction.gameObject.name}'");
    }

    /// <summary>
    /// Full fast-travel pre-validation: STRICT in-bounds (real WorldspaceBounds AABB, PT-010) AND
    /// ground within drop AND capsule clearance. The capsule is checked at the ground-snapped
    /// position (where the actor will actually stand), and the STANDING point is bounds-checked
    /// too (a target above the room whose floor is inside must not sneak through on the raw Y).
    /// </summary>
    public static ProbeResult ValidateTeleportTarget(Vector3 target, CharacterControllerSettingsPreset settings, float maxDrop = 3f, ReferenceHub? ignoreHub = null)
    {
        ProbeResult bounds = BoundsStrict(target);
        if (!bounds.Ok)
        {
            return bounds;
        }

        if (!TryFindGround(target, maxDrop, out float groundY, out _))
        {
            return ProbeResult.Fail("ground", $"no walkable ground within {maxDrop:0.##}m below {Format(target)}");
        }

        Vector3 standingRoot = new(target.x, groundY + ExpectedRootHeight(settings), target.z);
        ProbeResult standingBounds = BoundsStrict(standingRoot);
        if (!standingBounds.Ok)
        {
            return standingBounds;
        }

        return Capsule(standingRoot, settings, ignoreHub);
    }

    public static string Format(Vector3 v) => $"({v.x:0.##},{v.y:0.##},{v.z:0.##})";
}
