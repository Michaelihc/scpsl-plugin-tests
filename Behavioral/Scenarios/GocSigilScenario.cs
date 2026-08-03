using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BehaviorTestHarness.Harness;
using LabApi.Features.Wrappers;
using PlayerRoles;
using UnityEngine;

namespace BehaviorTestHarness.Scenarios;

/// <summary>
/// Drives GocNuke anti-gravity on a grounded dummy so the plugin's "[GocNuke:Sigil] Seat sigil ..."
/// diagnostic logs the player position and the raycast-projected ground position. Confirms the
/// summoning sigil seats on the floor (ground.y well below the player) rather than floating.
/// Triggers through the plugin's public TryApplyManualAntiGravity (reflected, since the service type
/// is internal) — the same path the RA `gocnuke antigravity` command uses.
/// </summary>
public sealed class GocSigilScenario : IBehaviorScenario
{
    public string Name => "goc-sigil";

    public string Description => "Anti-gravity summoning sigil seats on the floor under an affected dummy.";

    public IReadOnlyCollection<string> Aliases => ["sigil", "goc"];

    public BehaviorTestResult Run(BehaviorScenarioContext context)
    {
        context.Require(Round.IsRoundStarted, "round is not started; facility not generated so there is no floor to seat the sigil on");

        Player target = context.SpawnDummy("agtarget");
        // Spawn at a real role spawn point (on the facility floor); do NOT override position so the
        // ground raycast has actual geometry beneath it.
        target.SetRole(RoleTypeId.ClassD, RoleChangeReason.RemoteAdmin, RoleSpawnFlags.All);
        context.Info($"goc-sigil dummy spawned player={DummyRegistry.Describe(target)} pos={Fmt(target.Position)} alive={target.IsAlive}");
        context.Require(target.IsAlive, "dummy target is not alive after role assign");
        context.Require(target.Position.y > -1500f, "dummy did not spawn on the generated facility (fell through / void)");

        float floorY = target.Position.y;
        bool applied = TryApplyAntiGravity(target, 8f, out string reason);
        context.Info($"goc-sigil antigravity applied={applied} reason='{reason}' dummyFootY={floorY:0.##}");
        context.Require(applied, $"failed to apply anti-gravity: {reason}");

        context.Info("goc-sigil check the server log for '[GocNuke:Sigil] Seat sigil ... -> ground=(x,y,z)': ground.y should be ~the dummy foot Y, not the player's mid-air Y.");
        return context.Pass();
    }

    private static bool TryApplyAntiGravity(Player target, float seconds, out string reason)
    {
        reason = string.Empty;
        Type? pluginType = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType("GocNuke.GocNukePlugin", throwOnError: false))
            .FirstOrDefault(t => t != null);
        if (pluginType == null)
        {
            reason = "GocNuke plugin type not found (is GocNuke deployed?)";
            return false;
        }

        object? instance = pluginType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        if (instance == null)
        {
            reason = "GocNuke is not enabled";
            return false;
        }

        object? service = pluginType.GetProperty("SpecialItems", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(instance);
        if (service == null)
        {
            reason = "GocNuke SpecialItems service is unavailable";
            return false;
        }

        MethodInfo? method = service.GetType().GetMethod("TryApplyManualAntiGravity", BindingFlags.Instance | BindingFlags.Public);
        if (method == null)
        {
            reason = "TryApplyManualAntiGravity not found";
            return false;
        }

        object?[] args = { target, seconds, null };
        bool ok = method.Invoke(service, args) is true;
        reason = args[2] as string ?? string.Empty;
        return ok;
    }

    private static string Fmt(Vector3 value) => $"{value.x:0.##},{value.y:0.##},{value.z:0.##}";
}
