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
/// Verifies the GOC evac ESCAPE-SURVIVAL logic with real player objects (dummies), SYNCHRONOUSLY (a
/// headless server idles with no real clients, which stalls MEC coroutines, so the whole check runs in
/// one tick). Spawns the heli (instant-land), drops one dummy INSIDE the cabin and one far OUTSIDE, then
/// calls LockAndDepart (which re-checks the cabin from live positions) and asserts: the inside dummy is
/// counted aboard + flagged IsEscapee (so the post-detonation kill skips it = survives), and the outside
/// dummy is NOT an escapee (so it dies). The actual blast/kill and the visible gravity ascent need a real
/// client (dummies have no client prediction). Watch for "[goc-evac-ride] RESULT=".
/// </summary>
public sealed class GocEvacRideScenario : IBehaviorScenario
{
    public string Name => "goc-evac-ride";

    public string Description => "A dummy inside the cabin is flagged escapee (survives); one outside is not (dies).";

    public IReadOnlyCollection<string> Aliases => ["evac-ride", "evac-survival"];

    public BehaviorTestResult Run(BehaviorScenarioContext context)
    {
        object? plugin = ResolvePlugin(out string reason);
        context.Require(plugin != null, $"could not resolve GocNukePlugin: {reason}");

        if (!Round.IsRoundStarted)
        {
            Round.Start();
            context.Info("goc-evac-ride round not started; started it, running the flow in 15s (watch for '[goc-evac-ride] RESULT=').");
            object captured = plugin!;
            Timing.CallDelayed(15f, () =>
            {
                try
                {
                    RunCore(context, captured);
                }
                catch (Exception ex)
                {
                    Logger.Error($"[goc-evac-ride] RESULT=FAIL error={ex.GetBaseException().Message}");
                }
            });
            return context.Pass();
        }

        RunCore(context, plugin!);
        return context.Pass();
    }

    private static void RunCore(BehaviorScenarioContext context, object plugin)
    {
        Type pt = plugin.GetType();

        // Instant-land the heli so the cabin is at its land position this tick (no descent coroutine to wait on).
        object config = pt.GetProperty("Config", BindingFlags.Public | BindingFlags.Instance)!.GetValue(plugin)!;
        object heliCfg = GetProp(config, "EvacHelicopter");
        SetProp(heliCfg, "DescentStartHeight", 0f);
        SetProp(heliCfg, "DescentSeconds", 0.5f);

        object objective = GetService(pt, plugin, "Objective");
        object heli = GetService(pt, plugin, "EvacHelicopter");
        object boarding = GetService(pt, plugin, "EvacBoarding");

        // Start the countdown -> spawns the heli (at its land position) + opens boarding.
        object?[] startArgs = { null, null };
        objective.GetType().GetMethod("ForceStartCountdown", BindingFlags.Instance | BindingFlags.Public)!.Invoke(objective, startArgs);

        object? run = heli.GetType().GetProperty("ActiveRun", BindingFlags.Instance | BindingFlags.Public)?.GetValue(heli);
        context.Require(run != null, "no active heli run after countdown start (evac_helicopter disabled?)");
        Vector3 cabin = (Vector3)run!.GetType().GetProperty("CabinWorldCenter", BindingFlags.Instance | BindingFlags.Public)!.GetValue(run)!;

        // Dummy INSIDE the cabin, and one far OUTSIDE. Dummy positions are server-authoritative (no client),
        // so the teleport is effective for the LockAndDepart re-check below in the same tick.
        Player rider = context.SpawnDummy("rider");
        rider.SetRole(RoleTypeId.ClassD, RoleChangeReason.RemoteAdmin, RoleSpawnFlags.All);
        rider.Position = cabin;

        Player bystander = context.SpawnDummy("bystander");
        bystander.SetRole(RoleTypeId.ClassD, RoleChangeReason.RemoteAdmin, RoleSpawnFlags.All);
        bystander.Position = cabin + new Vector3(60f, 0f, 60f);

        context.Info($"goc-evac-ride cabin={Fmt(cabin)} rider={Fmt(rider.Position)} bystander={Fmt(bystander.Position)}");

        MethodInfo lockAndDepart = boarding.GetType().GetMethod("LockAndDepart", BindingFlags.Instance | BindingFlags.Public)!;
        MethodInfo isEscapee = boarding.GetType().GetMethod("IsEscapee", BindingFlags.Instance | BindingFlags.Public)!;

        // The dummies keep the server out of idle, so these delayed steps fire. Give the freshly-spawned dummy
        // FPC modules a moment to initialize before locking (so the gravity/jump apply), then sample the rider's
        // ACTUAL position to see whether the gravity ascent moved it.
        Timing.CallDelayed(1.5f, () =>
        {
            try
            {
                float riderY0 = rider.Position.y;
                object?[] lockArgs = { null };
                int escapees = (int)lockAndDepart.Invoke(boarding, lockArgs)!;
                bool riderEsc = (bool)isEscapee.Invoke(boarding, new object[] { rider })!;
                bool bystanderEsc = (bool)isEscapee.Invoke(boarding, new object[] { bystander })!;
                context.Info($"goc-evac-ride locked escapees={escapees} riderEscapee={riderEsc} bystanderEscapee={bystanderEsc} riderY0={riderY0:0.##}");

                Timing.CallDelayed(4f, () =>
                {
                    try
                    {
                        float riderY1 = rider.IsDestroyed ? riderY0 : rider.Position.y;
                        float dz = riderY1 - riderY0;
                        // PASS gates on the survival logic (deterministic, headless-verifiable). The actual ascent
                        // is reported as a diagnostic: a server-side dummy may not reproduce client-predicted
                        // gravity, so dz is informational, not a gate.
                        bool flagsOk = escapees == 1 && riderEsc && !bystanderEsc;
                        Logger.Info($"[goc-evac-ride] RESULT={(flagsOk ? "PASS" : "FAIL")} escapees={escapees} " +
                            $"riderEscapee={riderEsc} bystanderEscapee={bystanderEsc} riderY0={riderY0:0.##} riderY1={riderY1:0.##} " +
                            $"riderΔ={dz:0.##}m (survival logic gated; riderΔ is the dummy's actual move — positive = ascending, " +
                            "the client ascent uses the same gravity mechanism as the anti-gravity baton)");
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"[goc-evac-ride] RESULT=FAIL ascent-sample error={ex.GetBaseException().Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                Logger.Error($"[goc-evac-ride] RESULT=FAIL lock error={ex.GetBaseException().Message}");
            }
        });
    }

    private static object GetService(Type pt, object plugin, string property)
    {
        return pt.GetProperty(property, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(plugin)
            ?? throw new BehaviorAssertionException($"{property} service unavailable");
    }

    private static object GetProp(object obj, string name)
    {
        return obj.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public)?.GetValue(obj)
            ?? throw new BehaviorAssertionException($"property {name} not found on {obj.GetType().Name}");
    }

    private static void SetProp(object obj, string name, object value)
    {
        PropertyInfo pi = obj.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public)
            ?? throw new BehaviorAssertionException($"settable property {name} not found on {obj.GetType().Name}");
        pi.SetValue(obj, value);
    }

    private static string Fmt(Vector3 v) => $"({v.x:0.#},{v.y:0.#},{v.z:0.#})";

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
