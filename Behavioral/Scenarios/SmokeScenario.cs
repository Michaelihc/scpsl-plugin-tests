using System;
using System.Collections.Generic;
using BehaviorTestHarness.Harness;
using InventorySystem.Items;
using LabApi.Features.Wrappers;
using PlayerRoles;
using UnityEngine;

namespace BehaviorTestHarness.Scenarios;

public sealed class SmokeScenario : IBehaviorScenario
{
    public string Name => "smoke";

    public string Description => "Validates the dummy behavior path: spawn, role, item, attack, and reward observation.";

    public IReadOnlyCollection<string> Aliases => ["basic"];

    public BehaviorTestResult Run(BehaviorScenarioContext context)
    {
        Player attacker = context.SpawnDummy("attacker");
        Player target = context.SpawnDummy("target");

        PreparePlayer(attacker, RoleTypeId.ClassD, new Vector3(0f, 1000f, 0f), context);
        PreparePlayer(target, RoleTypeId.Scientist, new Vector3(1.5f, 1000f, 0f), context);

        attacker.AddItem(ItemType.GunCOM15, ItemAddReason.AdminCommand);
        context.Info($"dummy player item added player={DummyRegistry.Describe(attacker)} item={ItemType.GunCOM15}");

        float beforeHealth = target.Health;
        bool damaged = target.Damage(context.Config.SmokeDamage, attacker, Vector3.zero, context.Config.SmokeArmorPenetration);
        float afterHealth = target.Health;
        context.Info($"dummy player attacked attacker={DummyRegistry.Describe(attacker)} target={DummyRegistry.Describe(target)} requestedDamage={context.Config.SmokeDamage:0.##} health={beforeHealth:0.##}->{afterHealth:0.##} applied={damaged}");

        context.Require(damaged, $"damage was rejected health={beforeHealth:0.##}->{afterHealth:0.##}");
        context.Require(afterHealth < beforeHealth, $"damage did not reduce health health={beforeHealth:0.##}->{afterHealth:0.##}");

        int beforeXp = context.Experience.Get(attacker);
        int afterXp = context.Experience.Add(attacker, context.Config.SmokeExperienceAward);
        context.Info($"dummy exp increased player={DummyRegistry.Describe(attacker)} amount={context.Config.SmokeExperienceAward} exp={beforeXp}->{afterXp}");

        return context.Pass();
    }

    private static void PreparePlayer(Player player, RoleTypeId role, Vector3 position, BehaviorScenarioContext context)
    {
        player.SetRole(role, RoleChangeReason.RemoteAdmin, RoleSpawnFlags.AssignInventory);
        player.Position = position;
        if (player.MaxHealth < 100f)
        {
            player.MaxHealth = 100f;
        }

        player.Health = player.MaxHealth;
        context.Info($"dummy player prepared player={DummyRegistry.Describe(player)} role={role} position={Format(position)} health={player.Health:0.##}");
    }

    private static string Format(Vector3 value) => $"{value.x:0.##},{value.y:0.##},{value.z:0.##}";
}
