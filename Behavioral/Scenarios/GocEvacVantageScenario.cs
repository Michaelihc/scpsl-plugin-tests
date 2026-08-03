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
/// Diagnoses why the evac rider "only sees the shockwave" and "falls at the wins screen". Spawns a dummy in
/// the cabin, runs LockAndDepart (which teleports it onto the viewing platform + locks its camera onto the
/// blast), then SERVER-SIDE verifies, with a RAYCAST, the things a dummy CAN answer:
///  - rider position == the configured viewing platform (not left at the cabin / near the blast),
///  - line-of-sight: a ray from the rider's eye to the blast origin actually REACHES it (not occluded by
///    surface terrain — occlusion would hide the dome),
///  - camera aim: the rider's look-forward (rebuilt from LookRotation via the FpcMouseLook convention,
///    forward = (cosV*sinH, sinV, cosV*cosH)) points AT the blast (angle ~0),
///  - inside/outside the dome (a primitive sphere is back-face culled; the rider must be OUTSIDE it),
///  - the viewing-platform toy exists with the expected flags.
/// Watch for "[goc-evac-vantage] RESULT=".
/// </summary>
public sealed class GocEvacVantageScenario : IBehaviorScenario
{
    public string Name => "goc-evac-vantage";

    public string Description => "Raycast-checks the evac rider's platform position, blast line-of-sight, and camera aim.";

    public IReadOnlyCollection<string> Aliases => ["evac-vantage", "evac-aim"];

    public BehaviorTestResult Run(BehaviorScenarioContext context)
    {
        object? plugin = ResolvePlugin(out string reason);
        context.Require(plugin != null, $"could not resolve GocNukePlugin: {reason}");

        if (!Round.IsRoundStarted)
        {
            Round.Start();
            context.Info("goc-evac-vantage round not started; started it, running in 15s (watch for '[goc-evac-vantage] RESULT=').");
            object captured = plugin!;
            Timing.CallDelayed(15f, () =>
            {
                try { RunCore(context, captured); }
                catch (Exception ex) { Logger.Error($"[goc-evac-vantage] RESULT=FAIL error={ex.GetBaseException().Message}"); }
            });
            return context.Pass();
        }

        RunCore(context, plugin!);
        return context.Pass();
    }

    private static void RunCore(BehaviorScenarioContext context, object plugin)
    {
        Type pt = plugin.GetType();
        object config = pt.GetProperty("Config", BindingFlags.Public | BindingFlags.Instance)!.GetValue(plugin)!;
        object heliCfg = GetProp(config, "EvacHelicopter");
        object shockCfg = GetProp(config, "Shockwave");

        // Instant-land the heli (no descent coroutine to wait on).
        SetProp(heliCfg, "DescentStartHeight", 0f);
        SetProp(heliCfg, "DescentSeconds", 0.5f);

        Vector3 lookTarget = ToVec(GetProp(heliCfg, "ViewingPlatformLookTarget"));
        Vector3 platformPos = ToVec(GetProp(heliCfg, "ViewingPlatformPosition"));
        float domeRadius = Convert.ToSingle(GetProp(shockCfg, "DomeEndRadiusMeters"));

        object objective = GetService(pt, plugin, "Objective");
        object heli = GetService(pt, plugin, "EvacHelicopter");
        object boarding = GetService(pt, plugin, "EvacBoarding");

        objective.GetType().GetMethod("ForceStartCountdown", BindingFlags.Instance | BindingFlags.Public)!
            .Invoke(objective, new object?[] { null, null });

        object? run = heli.GetType().GetProperty("ActiveRun", BindingFlags.Instance | BindingFlags.Public)?.GetValue(heli);
        context.Require(run != null, "no active heli run after countdown start (evac_helicopter disabled?)");
        Vector3 cabin = (Vector3)run!.GetType().GetProperty("CabinWorldCenter", BindingFlags.Instance | BindingFlags.Public)!.GetValue(run)!;

        Player rider = context.SpawnDummy("vantage-rider");
        rider.SetRole(RoleTypeId.ClassD, RoleChangeReason.RemoteAdmin, RoleSpawnFlags.All);
        rider.Position = cabin;

        MethodInfo lockAndDepart = boarding.GetType().GetMethod("LockAndDepart", BindingFlags.Instance | BindingFlags.Public)!;
        FieldInfo platformField = boarding.GetType().GetField("_platform", BindingFlags.Instance | BindingFlags.NonPublic)!;

        Timing.CallDelayed(1.5f, () =>
        {
            try
            {
                lockAndDepart.Invoke(boarding, new object?[] { null });

                Vector3 pos = rider.Position;
                Vector2 look = rider.LookRotation; // (x=vertical, y=horizontal) degrees
                float v = look.x * Mathf.Deg2Rad;
                float h = look.y * Mathf.Deg2Rad;
                // FpcMouseLook: hub yaw = Euler(up*H), cam pitch = Euler(left*V) => forward below.
                Vector3 forward = new Vector3(Mathf.Cos(v) * Mathf.Sin(h), Mathf.Sin(v), Mathf.Cos(v) * Mathf.Cos(h)).normalized;

                Vector3 eye = pos + (Vector3.up * 1.6f);
                Vector3 toBlast = lookTarget - eye;
                float distToBlast = toBlast.magnitude;
                Vector3 dirToBlast = toBlast.normalized;
                float aimAngle = Vector3.Angle(forward, dirToBlast);

                // Platform placement: did the rider actually land on the configured platform?
                float platformErr = Vector3.Distance(new Vector3(pos.x, 0f, pos.z), new Vector3(platformPos.x, 0f, platformPos.z));

                // Inside/outside the dome (back-face culled sphere => must be OUTSIDE).
                float distCenter = Vector3.Distance(pos, lookTarget);
                bool outsideDome = distCenter > domeRadius;

                // RAYCAST eye -> blast: is the line of sight clear, or does terrain occlude the dome?
                string los;
                bool losClear;
                if (Physics.Raycast(eye, dirToBlast, out RaycastHit hit, distToBlast + 5f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                {
                    losClear = hit.distance >= distToBlast - 2f;
                    los = $"hit '{hit.collider?.name}' at {hit.distance:0.#}m (blast at {distToBlast:0.#}m) -> {(losClear ? "CLEAR(hit past/at blast)" : "OCCLUDED")}";
                }
                else
                {
                    losClear = true;
                    los = $"no hit within {distToBlast + 5f:0.#}m -> CLEAR(open)";
                }

                // Platform toy.
                object? platform = platformField.GetValue(boarding);
                string platformInfo = DescribePlatform(platform);

                bool aimOk = aimAngle < 8f;
                bool platformOk = platformErr < 3f;
                bool pass = aimOk && platformOk && outsideDome && losClear;
                Logger.Info($"[goc-evac-vantage] RESULT={(pass ? "PASS" : "FAIL")} " +
                    $"riderPos={Fmt(pos)} platformErr={platformErr:0.##}m(want<3) " +
                    $"look=(V{look.x:0.#},H{look.y:0.#}) fwd={Fmt(forward)} aimAngle={aimAngle:0.#}deg(want<8) " +
                    $"distToBlast={distCenter:0.#}m domeR={domeRadius:0.#} outsideDome={outsideDome} " +
                    $"LOS={los} platform=[{platformInfo}]");
            }
            catch (Exception ex)
            {
                Logger.Error($"[goc-evac-vantage] RESULT=FAIL diag error={ex.GetBaseException().Message}");
            }
        });
    }

    private static string DescribePlatform(object? platform)
    {
        if (platform == null)
        {
            return "NULL (not spawned)";
        }

        try
        {
            Type t = platform.GetType();
            bool destroyed = (bool)(t.GetProperty("IsDestroyed")?.GetValue(platform) ?? true);
            object? flags = t.GetProperty("Flags")?.GetValue(platform);
            object? p = t.GetProperty("Position")?.GetValue(platform);
            return $"destroyed={destroyed} flags={flags} pos={(p is Vector3 v ? Fmt(v) : "?")}";
        }
        catch (Exception ex)
        {
            return $"err {ex.GetBaseException().Message}";
        }
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

    private static Vector3 ToVec(object vec3Config)
    {
        return (Vector3)vec3Config.GetType().GetMethod("ToUnityVector3", BindingFlags.Instance | BindingFlags.Public)!
            .Invoke(vec3Config, Array.Empty<object>())!;
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
