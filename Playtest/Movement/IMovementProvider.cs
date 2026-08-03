using UnityEngine;

namespace PlaytestHarness.Movement;

/// <summary>High-level walk-order state reported by a movement provider.</summary>
public enum MovementState
{
    /// <summary>No order known for this hub.</summary>
    None,
    Moving,
    Arrived,
    Stalled,
    OffMesh,
}

/// <summary>Structured status snapshot for a walk order.</summary>
public readonly struct MovementStatus
{
    public MovementStatus(MovementState state, string reason, Vector3 position, float distanceRemaining)
    {
        State = state;
        Reason = reason;
        Position = position;
        DistanceRemaining = distanceRemaining;
    }

    public MovementState State { get; }

    /// <summary>Provider's structured failure/progress reason (empty when moving normally).</summary>
    public string Reason { get; }

    public Vector3 Position { get; }

    public float DistanceRemaining { get; }
}

/// <summary>
/// The harness-owned movement seam (plan §"Movement provider"). The current spike BotOrders binds
/// behind <see cref="BotOrdersProvider"/> via reflection; the planned bot-plugin rewrite only has to
/// ship a new provider. Harness code and scenarios never reference a bot plugin directly.
/// </summary>
public interface IMovementProvider
{
    /// <summary>False when the underlying movement plugin is not loaded on this port.</summary>
    bool Available { get; }

    /// <summary>Short provider label for logs/SKIP messages.</summary>
    string Name { get; }

    /// <summary>
    /// Spawns a provider-controlled dummy (the spike's BotOrders can only drive hubs it spawned, so
    /// walk-capable actors are created through the provider). Implementations must leave the dummy
    /// HOLDING still until an order arrives. Returns false when spawning failed.
    /// </summary>
    bool TrySpawnActor(string nickname, PlayerRoles.RoleTypeId role, out ReferenceHub? hub);

    /// <summary>True when this hub is under provider control (i.e. walk orders will be accepted).</summary>
    bool Controls(ReferenceHub hub);

    /// <summary>
    /// Orders a native walk to the target. Returns false when the order could not be issued —
    /// the caller reads <see cref="Status"/> for the structured reason (e.g. OffMesh).
    /// </summary>
    bool OrderWalk(ReferenceHub hub, Vector3 target);

    /// <summary>
    /// Drives one frame of native local FPC movement toward a world direction without pathfinding.
    /// Used only as a short, straight-line fallback when a physically valid local objective lies just
    /// outside the provider's baked navmesh. Returns false when the provider cannot expose native input.
    /// </summary>
    bool DriveLocal(ReferenceHub hub, Vector3 worldDirection);

    /// <summary>Stops local-input fallback movement immediately.</summary>
    void StopLocal(ReferenceHub hub);

    /// <summary>Orders a walk to a room (provider resolves an on-mesh goal inside it).</summary>
    bool OrderWalkToRoom(ReferenceHub hub, MapGeneration.RoomName room);

    MovementStatus Status(ReferenceHub hub);

    /// <summary>Cancels any active order and leaves the hub holding still.</summary>
    void Cancel(ReferenceHub hub);

    /// <summary>
    /// Releases provider bookkeeping for a hub the harness is about to destroy (or has destroyed).
    /// Must be safe to call for unknown/already-dead hubs. Prevents dead-hub leaks in the provider.
    /// </summary>
    void Release(ReferenceHub hub);
}
