using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BehaviorTestHarness.Harness;
using LabApi.Features.Wrappers;
using MEC;
using PlayerRoles;
using UnityEngine;
using Logger = LabApi.Features.Console.Logger;

namespace BehaviorTestHarness.Scenarios;

/// <summary>
/// Diagnostic: does the GOC anti-gravity BATON actually float an RA dummy? Spawns a dummy, applies the
/// baton's manual anti-gravity (SpecialItemService.TryApplyManualAntiGravity, reflected), and samples its
/// world Y over a few seconds. If a server-side dummy rises here, then gravity-driven motion DOES move
/// dummies (and the evac's riderΔ=0 is a bug, not a fundamental limit). Watch for "[goc-baton-dummy] RESULT=".
/// </summary>
public sealed class GocBatonDummyScenario : IBehaviorScenario
{
    public string Name => "goc-baton-dummy";

    public string Description => "Does the anti-gravity baton float an RA dummy? Samples the dummy's Y.";

    public IReadOnlyCollection<string> Aliases => ["baton-dummy", "ag-dummy"];

    public BehaviorTestResult Run(BehaviorScenarioContext context)
    {
        object? plugin = ResolvePlugin(out string reason);
        context.Require(plugin != null, $"could not resolve GocNukePlugin: {reason}");

        if (!Round.IsRoundStarted)
        {
            Round.Start();
            context.Info("goc-baton-dummy round not started; started it, running in 15s.");
            object captured = plugin!;
            Timing.CallDelayed(15f, () =>
            {
                try { RunCore(context, captured); }
                catch (Exception ex) { Logger.Error($"[goc-baton-dummy] RESULT=FAIL error={ex.GetBaseException().Message}"); }
            });
            return context.Pass();
        }

        RunCore(context, plugin!);
        return context.Pass();
    }

    private static void RunCore(BehaviorScenarioContext context, object plugin)
    {
        Type pt = plugin.GetType();
        object special = pt.GetProperty("SpecialItems", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(plugin)
            ?? throw new BehaviorAssertionException("SpecialItems service unavailable");
        MethodInfo apply = special.GetType().GetMethod("TryApplyManualAntiGravity", BindingFlags.Instance | BindingFlags.Public)
            ?? throw new BehaviorAssertionException("TryApplyManualAntiGravity(Player,float,out string) not found");

        Player dummy = context.SpawnDummy("agdummy");
        dummy.SetRole(RoleTypeId.ClassD, RoleChangeReason.RemoteAdmin, RoleSpawnFlags.All);

        // Let the FPC module initialize, then apply the baton's anti-gravity and watch the Y.
        Timing.CallDelayed(1.5f, () =>
        {
            try
            {
                float y0 = dummy.Position.y;
                object?[] args = { dummy, 12f, null };
                bool ok = apply.Invoke(special, args) is true;
                context.Info($"goc-baton-dummy applied={ok} y0={y0:0.##} resp='{args[2]}'");

                Timing.CallDelayed(4f, () =>
                {
                    try
                    {
                        float y1 = dummy.IsDestroyed ? y0 : dummy.Position.y;
                        float dz = y1 - y0;
                        bool rose = dz > 1f;
                        Logger.Info($"[goc-baton-dummy] RESULT={(rose ? "ROSE" : "STAYED")} applied={ok} y0={y0:0.##} y1={y1:0.##} dz={dz:0.##}m " +
                            "(ROSE => server dummies DO float via gravity, so the evac riderΔ=0 is a bug; STAYED => dummies don't float server-side)");
                    }
                    catch (Exception ex) { Logger.Error($"[goc-baton-dummy] RESULT=FAIL sample error={ex.GetBaseException().Message}"); }
                });
            }
            catch (Exception ex) { Logger.Error($"[goc-baton-dummy] RESULT=FAIL apply error={ex.GetBaseException().Message}"); }
        });
    }

    private static object? ResolvePlugin(out string reason)
    {
        reason = string.Empty;
        Type? pluginType = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType("GocNuke.GocNukePlugin", throwOnError: false))
            .FirstOrDefault(t => t != null);
        if (pluginType == null)
        {
            reason = "GocNuke plugin type not found (is GocNuke deployed?)";
            return null;
        }

        object? instance = pluginType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        if (instance == null)
        {
            reason = "GocNuke is not enabled";
        }

        return instance;
    }
}
