using PlaytestHarness.Core;
using UnityEngine;

namespace PlaytestHarness.Actors;

/// <summary>
/// Where an actor spawns. <see cref="Native"/> uses the game's own role spawnpoint
/// (RoleSpawnFlags.All). <see cref="At"/> places the actor at a position with tier semantics:
/// quick = raw TP, standard = fast-travel (probe-validated TP + settle), EndToEnd = FORBIDDEN
/// (throws — e2e is native spawns only). <see cref="AtUnvalidated"/> is the explicit, logged
/// opt-out for intentionally weird placements — QUICK-ONLY: Standard's only placement shortcut is
/// VALIDATED fast-travel (plan tier table), so an unvalidated position mutation throws there too.
/// </summary>
public sealed class SpawnSpec
{
    public enum SpecKind
    {
        NativeRole,
        AtPosition,
        NonSpatialCandidate,
    }

    public SpecKind Kind { get; }

    public Vector3 Position { get; }

    /// <summary>True when the scenario explicitly opted out of placement validation (logged).</summary>
    public bool Unvalidated { get; }

    private SpawnSpec(SpecKind kind, Vector3 position, bool unvalidated)
    {
        Kind = kind;
        Position = position;
        Unvalidated = unvalidated;
    }

    /// <summary>Native role spawnpoint — the only spatial spec legal at EndToEnd.</summary>
    public static SpawnSpec Native() => new(SpecKind.NativeRole, Vector3.zero, false);

    /// <summary>
    /// A deliberately non-spatial actor used as an exact selectable pool member for a feature-owned
    /// spawn transition. WaitReady verifies the role but does not pretend Spectator/Overwatch has a
    /// capsule, ground, or room. The feature must transition it to a live FPC role before spatial verbs.
    /// </summary>
    public static SpawnSpec Candidate() => new(SpecKind.NonSpatialCandidate, Vector3.zero, false);

    public static SpawnSpec At(Vector3 position) => new(SpecKind.AtPosition, position, false);

    public static SpawnSpec AtUnvalidated(Vector3 position) => new(SpecKind.AtPosition, position, true);

    /// <summary>
    /// Tier rules: SpawnSpec.At throws at EndToEnd (e2e = native role spawns only);
    /// SpawnSpec.AtUnvalidated additionally throws at Standard — Standard's only placement
    /// shortcut is VALIDATED fast-travel, an unvalidated raw TP would break the two-shortcut
    /// contract (PT-002).
    /// </summary>
    internal void EnforceTier(Fidelity level)
    {
        if (Kind == SpecKind.AtPosition && level == Fidelity.EndToEnd)
        {
            throw new System.InvalidOperationException(
                "SpawnSpec.At is forbidden at EndToEnd — e2e uses native role spawns only (plan tier table).");
        }

        if (Unvalidated && level != Fidelity.Quick)
        {
            throw new System.InvalidOperationException(
                "SpawnSpec.AtUnvalidated is Quick-only — Standard permits only VALIDATED fast-travel placement (plan tier table: shortcut #1).");
        }
    }

    public override string ToString() => Kind switch
    {
        SpecKind.NativeRole => "native",
        SpecKind.NonSpatialCandidate => "candidate(non-spatial)",
        _ => $"at{(Unvalidated ? "-UNVALIDATED" : "")}({Position.x:0.##},{Position.y:0.##},{Position.z:0.##})",
    };
}
