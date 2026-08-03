using System.Collections.Generic;
using PlayerRoles;
using PlaytestHarness.Actors;
using PlaytestHarness.Core;
using UnityEngine;

namespace PlaytestHarness.Scenarios;

/// <summary>
/// Exit-gate B1 workhorse: native spawn -> settle -> place a second actor At() an offset of the
/// first (exercises the SpawnSpec.At placement path: raw at quick, fast-travel-validated at
/// standard) -> GoTo a nearby point -> soak with continuous ground+bounds monitoring.
/// </summary>
public sealed class PlacementSoakScenario : Scenario
{
    public override string Name => "placement-soak";

    public override string[] Aliases => ["soak"];

    public override string Description => "Native spawn + At() placement + GoTo + 10s soak; probes ground/bounds/capsule at standard.";

    public override FidelityRange Supported => new(Fidelity.Quick, Fidelity.Standard);

    public override IEnumerator<float> Run(ScenarioContext ctx)
    {
        Actor anchor = ctx.SpawnActor("anchor", RoleTypeId.ClassD, SpawnSpec.Native());
        yield return anchor.WaitReady();
        Vector3 vacatedSpawn = anchor.Position;
        ctx.Info($"anchor ready at {Probes.PlacementProbes.Format(vacatedSpawn)} room={anchor.RoomName}");

        // Vacate the known-safe native spawn before placing the second actor there. The previous
        // overlap-at-anchor version was precisely the occupied-target bug PT-009 caught.
        yield return anchor.GoTo(MapGeneration.RoomName.LczToilets);
        Actor guest = ctx.SpawnActor("guest", RoleTypeId.ClassD, SpawnSpec.At(vacatedSpawn + new Vector3(0f, 0.25f, 0f)));
        yield return guest.WaitReady();
        ctx.Require(guest.RoomName != "none", "guest placed inside a room");
        ctx.Info($"guest placed at {Probes.PlacementProbes.Format(guest.Position)} room={guest.RoomName}");

        // GoTo a probed safe point resolved inside the spawn room (fast-travel at standard).
        yield return guest.GoTo(MapGeneration.RoomName.LczClassDSpawn);
        ctx.Require(guest.RoomName == nameof(MapGeneration.RoomName.LczClassDSpawn), "guest fast-traveled within LczClassDSpawn");
        ctx.Info("guest GoTo probed room point ok");

        // Concurrent verbs: both actors are soaked/settled at the same time (round-robin pump) —
        // registering several verbs before one yield runs them in parallel.
        anchor.Soak(10f);
        guest.Settle();
        yield return ctx.WaitOneFrame();
        ctx.Info("placement soak complete (anchor soak + guest settle ran concurrently)");

        // PT-003 regression: this final MoveNext registers a verb and then returns false. The runner
        // must drain the verb before recording PASS even though there is deliberately no trailing yield.
        guest.Settle();
    }
}
