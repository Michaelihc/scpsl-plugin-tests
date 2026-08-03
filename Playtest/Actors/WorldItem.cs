using System.Linq;
using LabApi.Features.Wrappers;
using UnityEngine;

namespace PlaytestHarness.Actors;

/// <summary>Read-only handle to an exact world pickup serial created by a harness native action.</summary>
public sealed class WorldItem
{
    internal WorldItem(ushort serial, ItemType type)
    {
        Serial = serial;
        Type = type;
    }

    /// <summary>Creates a read-only handle for an already-existing exact world pickup serial.</summary>
    public static WorldItem? Find(ushort serial)
    {
        Pickup? pickup = Pickup.List.FirstOrDefault(candidate =>
            candidate != null && !candidate.IsDestroyed && candidate.Serial == serial);
        return pickup == null ? null : new WorldItem(serial, pickup.Type);
    }

    public ushort Serial { get; }

    public ItemType Type { get; }

    public bool Exists => Resolve() != null;

    public Vector3 Position => Resolve()?.Position ?? Vector3.zero;

    internal Pickup? Resolve() => Pickup.List.FirstOrDefault(pickup =>
        pickup != null && !pickup.IsDestroyed && pickup.Serial == Serial);
}
