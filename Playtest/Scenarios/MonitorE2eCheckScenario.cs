using System.Collections.Generic;
using System.Linq;
using PlayerRoles;
using PlayerRoles.FirstPersonControl;
using PlaytestHarness.Actors;
using PlaytestHarness.Core;
using UnityEngine;

namespace PlaytestHarness.Scenarios;

/// <summary>
/// EndToEnd adversarial monitor check (excluded from 'run all'; run by name): after a native spawn,
/// performs a deliberate UNORDERED ~3m code teleport — the exact contract breach e2e forbids — and
/// asserts TeleportMonitor recorded it. Declares one exact ExpectViolation slot (this scenario's PASS = the
/// monitor caught the breach; in a real scenario the runner would FAIL the scenario on the same
/// violation). HARNESS-INTERNAL EXCEPTION to the no-raw-mechanics rule: inducing the breach
/// requires bypassing the verbs on purpose.
/// </summary>
public sealed class MonitorE2eCheckScenario : Scenario
{
    public override string Name => "monitor-e2e-check";

    public override string[] Aliases => ["monitors-e2e"];

    public override string Description => "e2e adversarial: deliberate unordered 3m code TP must be caught by TeleportMonitor.";

    public override FidelityRange Supported => FidelityRange.Only(Fidelity.EndToEnd);

    public override bool IncludeInRunAll => false;

    public override IEnumerator<float> Run(ScenarioContext ctx)
    {
        Actor mover = ctx.SpawnActor("e2e-canary", RoleTypeId.ClassD, SpawnSpec.Native());
        yield return mover.WaitReady();

        // No warm-up delay: the ready notification synchronously seeds and hooks the actor, so an
        // immediate first-frame override is part of this regression check (PT-006).
        ctx.ExpectViolation("e2e adversarial raw code teleport", "TeleportMonitor", "e2e-canary", withinSeconds: 8f);
        Vector3 from = mover.Position;
        Vector3 to = from + new Vector3(3f, 0f, 0f);
        ctx.Info($"inducing unordered 3m code TP {Probes.PlacementProbes.Format(from)} -> {Probes.PlacementProbes.Format(to)} at e2e");
        mover.Hub.TryOverridePosition(to);

        yield return ctx.WaitUntil(
            () => ctx.Monitors.AllViolations().Any(v => v.Monitor == "TeleportMonitor" && v.Actor == "e2e-canary"),
            8f, "TeleportMonitor flagged the unordered e2e teleport");
        ctx.Info($"caught as designed: {ctx.Monitors.AllViolations().First(v => v.Monitor == "TeleportMonitor" && v.Actor == "e2e-canary").Detail}");
    }
}
