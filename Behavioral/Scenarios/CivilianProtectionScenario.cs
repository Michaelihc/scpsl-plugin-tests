using System;
using System.Collections.Generic;
using System.Linq;
using BehaviorTestHarness.Harness;
using InventorySystem.Items;
using LabApi.Features.Wrappers;
using PlayerRoles;
using UnityEngine;

namespace BehaviorTestHarness.Scenarios;

/// <summary>
/// Exercises CivilianProtection through the real LabAPI hurting event. Direct damage is intentional:
/// this scenario isolates cancellation and inventory classification without depending on weapon aim.
/// </summary>
public sealed class CivilianProtectionScenario : IBehaviorScenario
{
    private const float TestDamage = 17f;

    public string Name => "civilian-protection";

    public string Description => "Verifies civilian non-aggression and armed/unarmed military damage through PlayerEvents.Hurting.";

    public IReadOnlyCollection<string> Aliases => ["civilian", "civpro"];

    public BehaviorTestResult Run(BehaviorScenarioContext context)
    {
        context.Require(
            AppDomain.CurrentDomain.GetAssemblies().Any(static assembly =>
                string.Equals(assembly.GetName().Name, "CivilianProtection", StringComparison.OrdinalIgnoreCase)),
            "CivilianProtection.dll is not loaded");

        context.Require(Round.IsRoundStarted,
            "round is not active; join/start the round before running scptest run civilian-protection");
        context.Info("civilian test round active");

        Player foundation = context.SpawnDummy("foundation");
        Player chaos = context.SpawnDummy("chaos");
        Player classD = context.SpawnDummy("classd");
        Player scientist = context.SpawnDummy("scientist");

        Prepare(foundation, RoleTypeId.FacilityGuard, new Vector3(0f, 1000f, 0f), context);
        Prepare(chaos, RoleTypeId.ChaosConscript, new Vector3(2f, 1000f, 0f), context);
        Prepare(classD, RoleTypeId.ClassD, new Vector3(4f, 1000f, 0f), context);
        Prepare(scientist, RoleTypeId.Scientist, new Vector3(6f, 1000f, 0f), context);

        AssertBlocked(foundation, classD, "foundation-to-unarmed-classd", context);

        classD.AddItem(ItemType.Medkit, ItemAddReason.AdminCommand);
        AssertBlocked(foundation, classD, "foundation-to-classd-with-medkit", context);

        classD.AddItem(ItemType.GunCOM15, ItemAddReason.AdminCommand);
        AssertAllowed(foundation, classD, "foundation-to-armed-classd", context);

        AssertBlocked(chaos, scientist, "chaos-to-unarmed-scientist", context);
        scientist.AddItem(ItemType.GrenadeFlash, ItemAddReason.AdminCommand);
        AssertAllowed(chaos, scientist, "chaos-to-armed-scientist", context);

        AssertBlocked(classD, scientist, "armed-classd-to-armed-scientist", context);
        AssertBlocked(scientist, classD, "armed-scientist-to-armed-classd", context);

        context.Info("civilian protection matrix observed cases=7 blocked=5 allowed=2");
        return context.Pass();
    }

    private static void Prepare(Player player, RoleTypeId role, Vector3 position, BehaviorScenarioContext context)
    {
        player.SetRole(role, RoleChangeReason.RemoteAdmin, RoleSpawnFlags.None);
        player.ClearInventory();
        player.Position = position;
        player.MaxHealth = Math.Max(100f, player.MaxHealth);
        player.Health = player.MaxHealth;
        context.Info($"civilian player prepared player={DummyRegistry.Describe(player)} role={role} inventory=empty health={player.Health:0.##}");
    }

    private static void AssertBlocked(Player attacker, Player victim, string label, BehaviorScenarioContext context)
    {
        victim.Health = victim.MaxHealth;
        float before = victim.Health;
        bool applied = victim.Damage(TestDamage, attacker, Vector3.zero, 0);
        float after = victim.Health;

        context.Info($"civilian attack label={label} attacker={DummyRegistry.Describe(attacker)} victim={DummyRegistry.Describe(victim)} health={before:0.##}->{after:0.##} applied={applied}");
        context.Require(Math.Abs(after - before) < 0.01f,
            $"{label}: expected blocked damage, health changed {before:0.##}->{after:0.##}");
    }

    private static void AssertAllowed(Player attacker, Player victim, string label, BehaviorScenarioContext context)
    {
        victim.Health = victim.MaxHealth;
        float before = victim.Health;
        bool applied = victim.Damage(TestDamage, attacker, Vector3.zero, 0);
        float after = victim.Health;

        context.Info($"civilian attack label={label} attacker={DummyRegistry.Describe(attacker)} victim={DummyRegistry.Describe(victim)} health={before:0.##}->{after:0.##} applied={applied}");
        context.Require(after < before - 0.01f,
            $"{label}: expected allowed damage, health did not decrease {before:0.##}->{after:0.##}");
    }
}
