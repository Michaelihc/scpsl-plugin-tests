using System.Collections.Generic;
using System.Linq;
using PlayerRoles;
using PlaytestHarness.Actors;
using PlaytestHarness.Core;
using UnityEngine;

namespace PlaytestHarness.Scenarios;

/// <summary>
/// Exit-gate B1 tier-contrast demo — the mid-air-spawn bug class caught BY DESIGN. Deliberately-bad
/// GoTo targets (mid-air over void, inside geometry):
///   quick    -> raw TP happily goes there (shortcuts legal), scenario PASSES;
///   standard -> fast-travel probes REJECT both targets (asserted via AssertGoToRejected), and the
///               scenario PASSES only because rejection is what standard promises.
/// Excluded from 'run all' (IncludeInRunAll=false): it exists to demo the contrast by name.
/// </summary>
public sealed class BadGotoContrastScenario : Scenario
{
    public override string Name => "bad-goto-contrast";

    public override string[] Aliases => ["badgoto"];

    public override string Description => "Tier contrast: mid-air/in-geometry GoTo targets pass at quick, are probe-rejected at standard.";

    public override FidelityRange Supported => new(Fidelity.Quick, Fidelity.Standard);

    public override bool IncludeInRunAll => false;

    public override IEnumerator<float> Run(ScenarioContext ctx)
    {
        Actor probe = ctx.SpawnActor("probe", RoleTypeId.ClassD, SpawnSpec.Native());
        yield return probe.WaitReady();
        Vector3 home = probe.Position;

        // Deliberately-bad target 1: mid-air over the void, far above/away from any room.
        Vector3 midAirOverVoid = home + new Vector3(250f, 60f, 250f);

        // Deliberately-bad target 2: inside geometry — below the floor the actor stands on.
        Vector3 insideGeometry = new(home.x, home.y - 2.5f, home.z);

        if (ctx.Fidelity == Fidelity.Quick)
        {
            // Quick: raw TP goes anywhere — which legitimately trips BoundsMonitor while the probe
            // hangs mid-air. That is the point of the demo; opt out of violations-fail-the-scenario.
            ctx.ExpectViolation("quick branch deliberately leaves every room", "BoundsMonitor", "probe", withinSeconds: 3f);
            yield return probe.GoTo(midAirOverVoid);
            ctx.Info($"quick: raw TP to mid-air {Probes.PlacementProbes.Format(probe.Position)} (no probes by design)");
            yield return ctx.WaitUntil(
                () => ctx.Monitors.AllViolations().Any(v => v.Monitor == "BoundsMonitor" && v.Actor == "probe"),
                3f, "BoundsMonitor observed the deliberately invalid quick placement");
            yield return probe.GoTo(home);
            yield return probe.GoTo(insideGeometry);
            ctx.Info("quick: raw TP into geometry accepted (no probes by design)");
            yield return probe.GoTo(home);
            yield return probe.Settle();
        }
        else
        {
            // Standard: both targets must be REJECTED loudly by the fast-travel probes.
            probe.AssertGoToRejected(midAirOverVoid);
            ctx.Info("standard: mid-air-over-void target rejected by probes (as designed)");
            probe.AssertGoToRejected(insideGeometry);
            ctx.Info("standard: inside-geometry target rejected by probes (as designed)");
            ctx.Require(Vector3.Distance(probe.Position, home) < 1.5f, "actor never moved during rejected fast-travels");
        }
    }
}
