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
/// Exercises the GOC detonation overhaul end-to-end on a headless server (no real riders, so the escapee
/// count is 0; this checks the flow runs clean, not boarding survival). Spawns the evac heli, opens
/// boarding, fires the detonation shockwave, then locks-and-departs. Asserts the shockwave reports it
/// played and the boarding lock returns without throwing. Watch the server log for
/// "[GocNuke:Shockwave] Detonation shockwave erupting", "[GocNuke:Evac] Departure locked", and
/// "[goc-detonation] RESULT=". All target services are internal, so everything is reflected.
/// </summary>
public sealed class GocDetonationScenario : IBehaviorScenario
{
    public string Name => "goc-detonation";

    public string Description => "The GOC evac boarding lock + detonation shockwave run end-to-end.";

    public IReadOnlyCollection<string> Aliases => ["detonation", "shockwave", "evac-detonate"];

    public BehaviorTestResult Run(BehaviorScenarioContext context)
    {
        object? plugin = ResolvePlugin(out string reason);
        context.Require(plugin != null, $"could not resolve GocNukePlugin: {reason}");

        if (!Round.IsRoundStarted)
        {
            Round.Start();
            context.Info("goc-detonation round not started; started it, running the flow in 15s (watch for '[goc-detonation] RESULT=').");
            object capturedPlugin = plugin!;
            Timing.CallDelayed(15f, () =>
            {
                try
                {
                    RunCore(context, capturedPlugin);
                }
                catch (Exception ex)
                {
                    Logger.Error($"[goc-detonation] RESULT=FAIL error={ex.GetBaseException().Message}");
                }
            });
            return context.Pass();
        }

        RunCore(context, plugin!);
        return context.Pass();
    }

    private static void RunCore(BehaviorScenarioContext context, object plugin)
    {
        Type pluginType = plugin.GetType();

        object heli = GetService(pluginType, plugin, "EvacHelicopter", "evac heli (evac_helicopter.enabled false or model missing?)");
        object boarding = GetService(pluginType, plugin, "EvacBoarding", "evac boarding service");
        object shockwave = GetService(pluginType, plugin, "Shockwave", "detonation shockwave service");

        // Spawn + land the heli so LockAndDepart has an active run to read seats from.
        object?[] spawnArgs = { null };
        bool spawned = heli.GetType().GetMethod("SpawnAndLand", BindingFlags.Instance | BindingFlags.Public)!
            .Invoke(heli, spawnArgs) is true;
        context.Require(spawned, $"SpawnAndLand failed: {spawnArgs[0] as string}");

        // Open boarding (no real riders on a headless server, so nobody boards).
        boarding.GetType().GetMethod("BeginBoarding", BindingFlags.Instance | BindingFlags.Public)!
            .Invoke(boarding, Array.Empty<object>());

        // Fire the shockwave spectacle.
        bool played = shockwave.GetType().GetMethod("Play", BindingFlags.Instance | BindingFlags.Public)!
            .Invoke(shockwave, new object[] { "goc-detonation scenario" }) is true;
        context.Require(played, "shockwave.Play returned false (shockwave.enabled false?)");

        // Lock + depart (expects 0 escapees here, but must run without throwing).
        object?[] lockArgs = { null };
        object lockResult = boarding.GetType().GetMethod("LockAndDepart", BindingFlags.Instance | BindingFlags.Public)!
            .Invoke(boarding, lockArgs)!;
        int escapees = lockResult is int i ? i : -1;
        context.Require(escapees >= 0, "LockAndDepart did not return a rider count");
        context.Info($"goc-detonation LockAndDepart escapees={escapees} response='{lockArgs[0] as string}'");

        heli.GetType().GetMethod("Depart", BindingFlags.Instance | BindingFlags.Public)!
            .Invoke(heli, new object?[] { null });

        Logger.Info($"[goc-detonation] RESULT=PASS shockwave played={played}, boarding lock ran escapees={escapees}.");
    }

    private static object GetService(Type pluginType, object plugin, string property, string label)
    {
        return pluginType.GetProperty(property, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(plugin)
            ?? throw new BehaviorAssertionException($"{label} unavailable ({property} null)");
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
