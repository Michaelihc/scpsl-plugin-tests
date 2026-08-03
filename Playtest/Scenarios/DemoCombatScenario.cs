using System.Collections.Generic;
using MapGeneration;
using PlayerRoles;
using PlaytestHarness.Actors;
using PlaytestHarness.Core;
using UnityEngine;

namespace PlaytestHarness.Scenarios;

/// <summary>
/// Native-ballistics proof: two actors; at quick the damage is Damage() plumbing (legal there
/// only); at standard actor A gets a COM-15 via GiveItem and Attack(B) fires REAL shots through the
/// native trigger/hitreg pipeline until B's health drops. Zero aim math here — the verbs own it.
/// </summary>
public sealed class DemoCombatScenario : Scenario
{
    public override string Name => "demo-combat";

    public override string[] Aliases => ["combat"];

    public override string Description => "Two actors; quick: Damage plumbing; standard: COM-15 + Attack with native ballistics until health drops.";

    public override FidelityRange Supported => new(Fidelity.Quick, Fidelity.Standard);

    public override IEnumerator<float> Run(ScenarioContext ctx)
    {
        // ClassD carries no native firearm, so the COM-15 below is provably the weapon that fires.
        // Target is NTF: hostile to ClassD both ways — same-team damage is friendly-fire-blocked.
        Actor shooter = ctx.SpawnActor("shooter", RoleTypeId.ClassD, SpawnSpec.Native());
        yield return shooter.WaitReady();
        Actor target = ctx.SpawnActor("target", RoleTypeId.NtfPrivate, SpawnSpec.Native());
        yield return target.WaitReady();

        // Face-off placement: fast-travel both to probed points in the open centre of the ClassD
        // spawn room (fixed world offsets cross cell walls and are rightly rejected by the capsule
        // probe; room resolution avoids occupied spots so the two actors land apart with LOS).
        yield return shooter.GoTo(RoomName.LczClassDSpawn);
        yield return target.GoTo(RoomName.LczClassDSpawn);
        ctx.Require(Vector3.Distance(shooter.Position, target.Position) > 0.9f, "actors resolved distinct probed points");

        float before = target.Health;
        ctx.Info($"target health before: {before:0.#} (tier {ctx.Fidelity})");

        if (ctx.Fidelity == Fidelity.Quick)
        {
            shooter.Damage(target, 25f);
            yield return ctx.Wait(0.5f);
        }
        else
        {
            // Standard shortcut #2 (pre-arranged state): the slice under test is "shoot a dummy",
            // not "find a gun" — the firearm is Arranged in, explicitly and telemetry-logged.
            ctx.Arrange("combat slice starts armed: pre-grant COM-15 + ammo",
                () => shooter.GiveItem(ItemType.GunCOM15));
            yield return shooter.Attack(target);
        }

        ctx.Require(target.Health < before, $"target health dropped ({before:0.#} -> {target.Health:0.#})");
        ctx.Info($"combat confirmed: target health {before:0.#} -> {target.Health:0.#}");
    }
}
