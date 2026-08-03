using System.Collections.Generic;
using PlayerRoles;
using PlaytestHarness.Actors;
using PlaytestHarness.Core;
using UnityEngine;

namespace PlaytestHarness.Scenarios;

/// <summary>
/// Exit-gate demo (excluded from 'run all'; run by name): orders a walk to a deliberately OFF-MESH
/// target (far outside the facility). EXPECTED OUTCOME: the scenario FAILS LOUDLY within seconds —
/// the provider reports OffMesh/NoPath, WalkTo throws with the provider's structured reason, and
/// the run terminates instead of hanging. A PASS here would itself be a harness bug. This is the
/// plan's "stalled walk = loud scenario failure, never a hang" acceptance check.
/// </summary>
public sealed class WalkOffMeshDemoScenario : Scenario
{
    public override string Name => "walk-offmesh-demo";

    public override string[] Aliases => ["offmesh"];

    public override string Description => "DELIBERATE-FAIL demo: walk order to an off-mesh point must FAIL loudly with the provider's reason (never hang).";

    public override FidelityRange Supported => new(Fidelity.Standard, Fidelity.EndToEnd);

    public override bool IncludeInRunAll => false;

    public override IEnumerator<float> Run(ScenarioContext ctx)
    {
        Actor walker = ctx.SpawnActor("offmesh-walker", RoleTypeId.ClassD, SpawnSpec.Native());
        yield return walker.WaitReady();

        // A point far outside every room/mesh: 500m sideways from the walker, same height.
        Vector3 nowhere = walker.Position + new Vector3(500f, 0f, 500f);
        ctx.Info($"ordering walk to deliberately off-mesh target {Probes.PlacementProbes.Format(nowhere)} — EXPECT a loud FAIL next");
        yield return walker.WalkTo(nowhere);

        // Unreachable when the provider behaves: WalkTo must have thrown by now.
        ctx.Require(false, "walk to an off-mesh target unexpectedly SUCCEEDED — provider failed to reject it");
    }
}
