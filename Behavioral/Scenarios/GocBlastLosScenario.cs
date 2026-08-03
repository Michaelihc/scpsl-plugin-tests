using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using BehaviorTestHarness.Harness;
using LabApi.Features.Wrappers;
using MEC;
using UnityEngine;
using Logger = LabApi.Features.Console.Logger;

namespace BehaviorTestHarness.Scenarios;

/// <summary>
/// Finds a viewing-platform vantage that actually SEES the blast. The current platform is occluded by surface
/// terrain (a raycast to the blast hits a collision box ~29m out), so the dome is hidden behind a wall. This
/// sweeps candidate vantage points around the configured shockwave origin, raycasts each one straight at the
/// origin, and logs which are CLEAR (unoccluded line of sight) with their distance + inside/outside-dome, so
/// a good platform position can be picked. Watch for "[goc-blast-los] RESULT=" and the CLEAR lines.
/// </summary>
public sealed class GocBlastLosScenario : IBehaviorScenario
{
    public string Name => "goc-blast-los";

    public string Description => "Raycast-sweeps vantage points around the blast to find one with clear line of sight.";

    public IReadOnlyCollection<string> Aliases => ["blast-los", "evac-los"];

    public BehaviorTestResult Run(BehaviorScenarioContext context)
    {
        object? plugin = ResolvePlugin(out string reason);
        context.Require(plugin != null, $"could not resolve GocNukePlugin: {reason}");

        if (!Round.IsRoundStarted)
        {
            Round.Start();
            context.Info("goc-blast-los round not started; started it, sweeping in 15s (watch for '[goc-blast-los]').");
            object captured = plugin!;
            Timing.CallDelayed(15f, () =>
            {
                try { RunCore(captured); }
                catch (Exception ex) { Logger.Error($"[goc-blast-los] RESULT=FAIL error={ex.GetBaseException().Message}"); }
            });
            return context.Pass();
        }

        RunCore(plugin!);
        return context.Pass();
    }

    private static void RunCore(object plugin)
    {
        Type pt = plugin.GetType();
        object config = pt.GetProperty("Config", BindingFlags.Public | BindingFlags.Instance)!.GetValue(plugin)!;
        object shockCfg = config.GetType().GetProperty("Shockwave")!.GetValue(config)!;
        Vector3 origin = ToVec(shockCfg.GetType().GetProperty("Origin")!.GetValue(shockCfg)!);
        float domeRadius = Convert.ToSingle(shockCfg.GetType().GetProperty("DomeEndRadiusMeters")!.GetValue(shockCfg));

        // Candidate vantages: rings of points around the origin at several heights + horizontal offsets, plus
        // the current platform spot. Eye height +1.6 added when casting.
        var heights = new[] { 30f, 50f, 70f, 90f };
        var radii = new[] { 0f, 25f, 45f, 65f };
        var dirs = new (string n, Vector3 d)[]
        {
            ("N", new Vector3(0, 0, 1)), ("S", new Vector3(0, 0, -1)),
            ("E", new Vector3(1, 0, 0)), ("W", new Vector3(-1, 0, 0)),
        };

        var candidates = new List<Vector3> { new Vector3(12f, 320f, -70f) }; // the current (occluded) platform
        foreach (float dy in heights)
        {
            foreach (float r in radii)
            {
                if (r == 0f)
                {
                    candidates.Add(origin + new Vector3(0f, dy, 0f));
                    continue;
                }

                foreach (var (_, d) in dirs)
                {
                    candidates.Add(origin + (d * r) + new Vector3(0f, dy, 0f));
                }
            }
        }

        var clear = new List<(Vector3 pos, float dist)>();
        var sb = new StringBuilder();
        foreach (Vector3 pos in candidates)
        {
            Vector3 eye = pos + (Vector3.up * 1.6f);
            Vector3 to = origin - eye;
            float dist = to.magnitude;
            // RaycastAll so invisible physics-only "Collision Box" volumes don't mask a real (visible) blocker.
            // A hit only occludes the VIEW if its GameObject (or a parent) has an enabled Renderer.
            bool visibleBlocker = false;
            string what = "open";
            RaycastHit[] hits = Physics.RaycastAll(eye, to.normalized, dist - 2f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            foreach (RaycastHit h in hits.OrderBy(x => x.distance))
            {
                Renderer? rend = h.collider != null ? h.collider.GetComponentInParent<Renderer>() : null;
                bool vis = rend != null && rend.enabled && rend.gameObject.activeInHierarchy;
                if (vis)
                {
                    visibleBlocker = true;
                    what = $"VISIBLE '{h.collider?.name}'@{h.distance:0.#}m";
                    break;
                }
            }

            if (!visibleBlocker)
            {
                what = hits.Length > 0 ? $"{hits.Length} invisible-collider(s) only" : "open";
            }

            bool los = !visibleBlocker; // line of SIGHT is clear unless a rendered object blocks it
            bool outside = dist > domeRadius;
            bool good = los && outside && dist >= domeRadius + 5f && dist <= 90f;
            if (good)
            {
                clear.Add((pos, dist));
            }

            sb.AppendLine($"[goc-blast-los] cand={Fmt(pos)} dist={dist:0.#}m outside={outside} los={(los ? "CLEAR" : "OCCLUDED")} ({what}){(good ? " <== GOOD" : string.Empty)}");
        }

        foreach (string line in sb.ToString().Split('\n'))
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                Logger.Info(line.TrimEnd());
            }
        }

        if (clear.Count > 0)
        {
            // Prefer the candidate whose distance is closest to domeRadius + 12m (big dome in view, clear margin).
            float ideal = domeRadius + 12f;
            var best = clear.OrderBy(c => Mathf.Abs(c.dist - ideal)).First();
            Logger.Info($"[goc-blast-los] RESULT=PASS origin={Fmt(origin)} domeR={domeRadius:0.#} clearCount={clear.Count} " +
                $"BEST={Fmt(best.pos)} dist={best.dist:0.#}m");
        }
        else
        {
            Logger.Error($"[goc-blast-los] RESULT=FAIL origin={Fmt(origin)} no clear vantage among {candidates.Count} candidates (all occluded or inside dome)");
        }
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
