using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BehaviorTestHarness.Harness;
using LabApi.Features.Wrappers;
using MEC;
using Logger = LabApi.Features.Console.Logger;

namespace BehaviorTestHarness.Scenarios;

/// <summary>
/// Verifies the GOC evac helicopter flies in and spawns its rig. Calls
/// EvacHelicopterService.SpawnAndLand (reflected, internal) and asserts it reports active, then
/// schedules a depart so the fly-away can be watched. Starts the round first if needed (the heli
/// lands at a surface world position). Watch the server log for "[GocNuke:Heli] Evac helicopter
/// inbound", "Evac helicopter rig spawned.", the ABSENCE of "model is empty", and "[goc-heli] RESULT=".
/// </summary>
public sealed class GocHeliScenario : IBehaviorScenario
{
    public string Name => "goc-heli";

    public string Description => "The GOC evac helicopter flies in, lands, and departs.";

    public IReadOnlyCollection<string> Aliases => ["heli", "evac"];

    public BehaviorTestResult Run(BehaviorScenarioContext context)
    {
        object? plugin = ResolvePlugin(out string reason);
        context.Require(plugin != null, $"could not resolve GocNukePlugin: {reason}");

        if (!Round.IsRoundStarted)
        {
            Round.Start();
            context.Info("goc-heli round not started; started it, dispatching the heli in 15s (watch for '[goc-heli] RESULT=').");
            object capturedPlugin = plugin!;
            Timing.CallDelayed(15f, () =>
            {
                try
                {
                    RunCore(context, capturedPlugin);
                }
                catch (Exception ex)
                {
                    Logger.Error($"[goc-heli] RESULT=FAIL error={ex.GetBaseException().Message}");
                }
            });
            return context.Pass();
        }

        RunCore(context, plugin!);
        return context.Pass();
    }

    private static void RunCore(BehaviorScenarioContext context, object plugin)
    {
        object? heli = plugin.GetType().GetProperty("EvacHelicopter", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(plugin)
            ?? throw new BehaviorAssertionException("EvacHelicopter service unavailable (evac_helicopter.enabled=false or model failed to load)");

        MethodInfo spawn = heli.GetType().GetMethod("SpawnAndLand", BindingFlags.Instance | BindingFlags.Public)
            ?? throw new BehaviorAssertionException("SpawnAndLand(out string) not found");
        object?[] args = { null };
        bool ok = spawn.Invoke(heli, args) is true;
        string response = args[0] as string ?? string.Empty;
        context.Info($"goc-heli SpawnAndLand ok={ok} response='{response}'");
        context.Require(ok, $"SpawnAndLand failed: {response}");

        bool active = heli.GetType().GetProperty("IsActive", BindingFlags.Instance | BindingFlags.Public)?.GetValue(heli) is true;
        context.Require(active, "heli reports not active after SpawnAndLand");

        Logger.Info($"[goc-heli] RESULT=PASS heli inbound ok={ok} isActive={active}. Watch it descend/land, then depart in ~12s.");

        // Let it land, then depart so the fly-away animates (not asserted; visual).
        MethodInfo depart = heli.GetType().GetMethod("Depart", BindingFlags.Instance | BindingFlags.Public)!;
        Timing.CallDelayed(12f, () =>
        {
            try
            {
                object?[] dargs = { null };
                depart.Invoke(heli, dargs);
                Logger.Info($"[goc-heli] depart triggered: {dargs[0] as string}");
            }
            catch (Exception ex)
            {
                Logger.Warn($"[goc-heli] depart failed: {ex.GetBaseException().Message}");
            }
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
