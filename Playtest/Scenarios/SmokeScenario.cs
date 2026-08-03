using System.Collections.Generic;
using System.Linq;
using Mirror;
using NetworkManagerUtils.Dummies;
using PlaytestHarness.Core;
using PlaytestHarness.Telemetry;

namespace PlaytestHarness.Scenarios;

/// <summary>
/// Built-in trivial smoke scenario: proves the runner, telemetry, cleanup and command plumbing work.
/// BOOTSTRAP-ONLY EXCEPTION: this scenario uses raw DummyUtils/NetworkServer calls because the actor
/// API (SpawnActor + verbs) is phase B. No other scenario may copy this pattern — scenarios call
/// harness verbs, never raw spawn/aim/align mechanics (see ScenarioTemplate.cs.txt).
/// Quick-ONLY: raw spawn/destroy violates the Standard/EndToEnd contracts (native gameplay actions,
/// native role spawn placement), so per the honest-Supported-range rule this scenario must not be
/// runnable — let alone PASS — at those tiers. Phase A's exit gate is 'smoke quick' only.
/// </summary>
public sealed class SmokeScenario : Scenario
{
    public override string Name => "smoke";

    public override string Description => "Spawns one dummy, verifies it exists and is tracked, cleans it up.";

    public override FidelityRange Supported => FidelityRange.Only(Fidelity.Quick);

    public override IEnumerator<float> Run(ScenarioContext ctx)
    {
        string prefix = PlaytestPlugin.Instance?.Config?.NicknamePrefix ?? "PT-";
        string nickname = prefix + "smoke";

        ctx.Info($"spawning dummy nickname={nickname}");
        ReferenceHub hub = DummyUtils.SpawnDummy(nickname);
        ctx.Require(hub != null, "DummyUtils.SpawnDummy returned a hub");
        ctx.Emit(new TelemetryEvent("dummy_spawn", nickname, new Dictionary<string, object?>
        {
            ["player_id"] = hub!.PlayerId,
        }));

        // Let the dummy register on the network/hub lists for a few frames.
        yield return ctx.Wait(0.5f);

        ctx.Require(hub.IsDummy, "spawned hub reports IsDummy");
        bool tracked = ReferenceHub.AllHubs.Any(h => h == hub);
        ctx.Require(tracked, "dummy hub is tracked in ReferenceHub.AllHubs");
        ctx.Info($"dummy tracked player_id={hub.PlayerId} role={hub.roleManager.CurrentRole.RoleTypeId}");

        // Prove yield-based WaitUntil works: condition is already true, must complete immediately.
        yield return ctx.WaitUntil(() => hub.isActiveAndEnabled, 5f, "dummy hub active");

        ctx.Info("destroying dummy");
        NetworkServer.Destroy(hub.gameObject);
        yield return ctx.Wait(0.25f);
        ctx.Require(ReferenceHub.AllHubs.All(h => h != hub), "dummy removed from ReferenceHub.AllHubs after destroy");
        ctx.Emit(new TelemetryEvent("dummy_destroyed", nickname));

        // Completion = PASS. No early Pass() exists by design.
    }
}
