using System.Collections.Generic;
using MapGeneration;
using PlayerRoles;
using PlaytestHarness.Actors;
using PlaytestHarness.Core;

namespace PlaytestHarness.Scenarios;

/// <summary>
/// The standard-tier playtest pattern demo: native ClassD spawn -> settle -> fast-travel
/// (probe-validated TP) to another LCZ room -> walk a short local leg -> soak. Scenario code is
/// plain verb calls per the batteries-included contract.
/// </summary>
public sealed class DemoFastTravelScenario : Scenario
{
    public override string Name => "demo-fast-travel";

    public override string[] Aliases => ["fasttravel"];

    public override string Description => "Native ClassD spawn -> settle -> fast-travel to an LCZ room -> WalkTo nearby -> soak 10s.";

    public override FidelityRange Supported => FidelityRange.Only(Fidelity.Standard);

    public override IEnumerator<float> Run(ScenarioContext ctx)
    {
        Actor traveler = ctx.SpawnActor("traveler", RoleTypeId.ClassD, SpawnSpec.Native());
        yield return traveler.WaitReady();
        ctx.Require(traveler.Role == RoleTypeId.ClassD, "actor spawned as ClassD at the native spawn point");
        ctx.Info($"spawned natively in room {traveler.RoomName}");

        yield return traveler.GoTo(RoomName.LczToilets);
        ctx.Require(traveler.RoomName != "none", "fast-travel landed inside a room");
        ctx.Info($"fast-travel to LczToilets validated (probed+settled); landed room={traveler.RoomName}");

        // Local movement AT the objective is walked for real at standard.
        yield return traveler.WalkTo(traveler.Position + new UnityEngine.Vector3(2.0f, 0f, 0f));
        ctx.Info($"walked local leg to {Probes.PlacementProbes.Format(traveler.Position)}");

        yield return traveler.Soak(10f);
        ctx.Info("soak complete — bounds+ground held for 10s");
    }
}
