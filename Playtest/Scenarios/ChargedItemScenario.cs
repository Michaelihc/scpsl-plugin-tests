using System.Collections.Generic;
using MapGeneration;
using PlayerRoles;
using PlaytestHarness.Actors;
using PlaytestHarness.Core;
using UnityEngine;

namespace PlaytestHarness.Scenarios;

/// <summary>
/// The long-charge native-item proof (plan: "UseHeldItem on a 20 s-charge item spends the 20 s").
/// Standard tier: Arrange pre-grants a Micro H.I.D., fast-travel to an open room, one-liner
/// UseHeldItem. The verb holds the native Shoot key via the dummy-key emulator; the item's real
/// CycleController runs Standby → WindingUp (real ~3 s+ primary charge at WindUpRate=1/3, after
/// the native 1.5 s standby idle) → Firing → WindingDown. The adapter validates observed wind-up
/// against the live module rate, energy drain, and an attributable MicroHID hit on the owned target.
/// Zero aim/charge/range math here — the targeted adapter owns it.
/// </summary>
public sealed class ChargedItemScenario : Scenario
{
    public override string Name => "native-charged-item";

    public override string[] Aliases => ["chargeditem", "microhid"];

    public override string Description => "Arrange Micro H.I.D. -> UseHeldItem runs the REAL windup/fire/winddown cycle; asserts native duration + energy drain.";

    public override FidelityRange Supported => FidelityRange.Only(Fidelity.Standard);

    public override IEnumerator<float> Run(ScenarioContext ctx)
    {
        Actor operator1 = ctx.SpawnActor("operator", RoleTypeId.ClassD, SpawnSpec.Native());
        yield return operator1.WaitReady();

        // Standard shortcut #2: the slice is "use the charged item", not "find a Micro H.I.D.".
        ctx.Arrange("charged-item slice starts equipped: pre-grant Micro H.I.D.",
            () => operator1.GiveItem(ItemType.MicroHID));

        // Fast-travel both actors into an open room. Room resolution chooses distinct capsule-clear
        // points, then the targeted verb owns approach/LOS/live-hitbox aim.
        yield return operator1.GoTo(RoomName.LczToilets);
        Actor target = ctx.SpawnActor("micro-target", RoleTypeId.NtfPrivate, SpawnSpec.Native());
        yield return target.WaitReady();
        ctx.Arrange("charged-item proof needs a durable owned target for the full 1.5s native beam hold",
            () => target.SetHealth(1000f));
        yield return target.GoTo(RoomName.LczToilets);
        yield return operator1.Equip(ItemType.MicroHID);

        float healthBefore = target.Health;
        double before = ctx.ElapsedSeconds;
        ctx.Info("starting targeted native Micro H.I.D. cycle (real standby+windup+beam effect+winddown)");
        yield return operator1.UseHeldItemOn(target);
        double took = ctx.ElapsedSeconds - before;

        ctx.Require(target.Health < healthBefore,
            $"targeted Micro H.I.D. cycle produced no health effect ({healthBefore:0.##} -> {target.Health:0.##})");
        ctx.Info($"full native cycle + attributable beam effect observed in {took:0.##}s; target health {healthBefore:0.##} -> {target.Health:0.##}");
    }
}
