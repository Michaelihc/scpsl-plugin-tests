using System.Collections.Generic;
using MapGeneration;
using PlayerRoles;
using PlaytestHarness.Actors;
using PlaytestHarness.Core;

namespace PlaytestHarness.Scenarios;

/// <summary>
/// The EndToEnd proof: native ClassD spawn -> settle -> walk a multi-room LCZ route (ClassD spawn
/// -> LczToilets -> Lcz173, the route proven on seed 791021 by the bot spike) -> soak 30s. At e2e
/// every leg is bot-walked; any teleport of an owned actor is a TeleportMonitor violation. At
/// standard the same source runs with the same walks (travel is in-slice here).
/// </summary>
public sealed class DemoWalkScenario : Scenario
{
    public override string Name => "demo-walk";

    public override string[] Aliases => ["walk"];

    public override string Description => "Native ClassD spawn -> settle -> walk ClassD spawn->LczToilets->Lcz173 -> soak 30s.";

    public override FidelityRange Supported => new(Fidelity.Standard, Fidelity.EndToEnd);

    public override IEnumerator<float> Run(ScenarioContext ctx)
    {
        Actor walker = ctx.SpawnActor("walker", RoleTypeId.ClassD, SpawnSpec.Native());
        yield return walker.WaitReady();
        ctx.Require(walker.Role == RoleTypeId.ClassD, "actor spawned as ClassD at the native spawn point");
        ctx.Info($"native spawn ok in {walker.RoomName}, walking the spike-proven LCZ route");

        // WalkTo(room) verifies arrival internally (in-room or doorway threshold; loud FAIL otherwise).
        yield return walker.WalkTo(RoomName.LczToilets);
        ctx.Info($"leg 1 complete: LczToilets (at {Probes.PlacementProbes.Format(walker.Position)})");

        yield return walker.WalkTo(RoomName.Lcz173);
        ctx.Info($"leg 2 complete: Lcz173 (at {Probes.PlacementProbes.Format(walker.Position)})");

        yield return walker.Soak(30f);
        ctx.Info("soak complete — 30s at destination with bounds+ground monitored");
    }
}
