using System.Collections.Generic;
using System.Linq;
using PlayerRoles;
using PlayerRoles.FirstPersonControl;
using PlaytestHarness.Actors;
using PlaytestHarness.Core;
using UnityEngine;

namespace PlaytestHarness.Scenarios;

/// <summary>
/// Monitor SELF-TEST (excluded from 'run all'): deliberately induces one role-watchdog violation
/// (external ServerSetRole on an owned actor, simulating a role-stomping plugin) and one unordered
/// teleport (raw position write without an ordered-move mark), then ASSERTS via ctx.Monitors that
/// the watchdog and TeleportMonitor actually recorded them. Declares exact ExpectViolation slots so the
/// runner's violations-fail-the-scenario rule does not fail this scenario for succeeding.
/// HARNESS-INTERNAL EXCEPTION to the no-raw-mechanics rule: inducing violations requires bypassing
/// the verbs on purpose.
/// </summary>
public sealed class MonitorSelfTestScenario : Scenario
{
    public override string Name => "monitor-selftest";

    public override string[] Aliases => ["monitors"];

    public override string Description => "Induces role-stomp + unordered TP violations and asserts the watchdog/monitors caught them.";

    public override FidelityRange Supported => FidelityRange.Only(Fidelity.Quick);

    public override bool IncludeInRunAll => false;

    public override IEnumerator<float> Run(ScenarioContext ctx)
    {
        Actor canary = ctx.SpawnActor("canary", RoleTypeId.ClassD, SpawnSpec.Native());
        yield return canary.WaitReady();

        // 1) Role watchdog: an EXTERNAL role change must be flagged. The spawn path consumed the
        //    expected-role mark at WaitReady, so even a same-role set would be flagged now.
        ctx.ExpectViolation("self-test role stomp", "RoleWatchdog", "canary", withinSeconds: 5f);
        ctx.Info("inducing external role change (simulated role-stomping plugin)");
        canary.Hub.roleManager.ServerSetRole(RoleTypeId.Scientist, RoleChangeReason.RemoteAdmin, RoleSpawnFlags.None);
        yield return ctx.WaitUntil(
            () => ctx.Monitors.AllViolations().Any(v => v.Monitor == "RoleWatchdog" && v.Actor == "canary"),
            5f, "role watchdog flagged the external role change");
        ctx.Info($"role watchdog OK: {ctx.Monitors.AllViolations().First(v => v.Monitor == "RoleWatchdog" && v.Actor == "canary").Detail}");

        // 2) Unordered move: raw transform-level position override WITHOUT MarkOrderedMove.
        //    TeleportMonitor must record a violation for the canary. The target is FAR OUTSIDE
        //    every room horizontally (not just above one): a purely vertical hop lets the canary
        //    fall straight back to the floor before BoundsMonitor's next tick, while out here it
        //    keeps falling into the void — a SUSTAINED out-of-bounds + airborne state both
        //    monitor channels can observe deterministically.
        ctx.ExpectViolation("self-test raw position override", "TeleportMonitor", "canary", withinSeconds: 8f);
        ctx.ExpectViolation("self-test out-of-bounds destination", "BoundsMonitor", "canary", withinSeconds: 12f);
        ctx.Info("inducing unordered far-out-of-bounds teleport (no ordered-move mark)");
        Vector3 midAir = canary.Position + new Vector3(250f, 40f, 250f);
        canary.Hub.TryOverridePosition(midAir);

        yield return ctx.WaitUntil(
            () => ctx.Monitors.AllViolations().Any(v => v.Monitor == "TeleportMonitor" && v.Actor == "canary"),
            8f, "TeleportMonitor flagged the unordered jump");
        ctx.Info($"teleport monitor OK: {ctx.Monitors.AllViolations().First(v => v.Monitor == "TeleportMonitor" && v.Actor == "canary").Detail}");

        // 3) BoundsMonitor: +40m over an LCZ room is outside every room's WorldspaceBounds, so the
        //    out-of-bounds channel (TryGetRoom fails) reports within a tick or two while the canary
        //    is still falling back down.
        yield return ctx.WaitUntil(
            () => ctx.Monitors.AllViolations().Any(v => v.Monitor == "BoundsMonitor" && v.Actor == "canary"),
            12f, "BoundsMonitor flagged the out-of-bounds mid-air position");
        ctx.Info("bounds monitor OK — all three monitor channels proven");
    }
}
