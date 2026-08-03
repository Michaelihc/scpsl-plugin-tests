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
/// Validates the occult-sigil "merge overlapping sigils" optimization deterministically. Seats three
/// affected-player sigils under ONE synthetic field owner via the plugin's internal
/// OccultSigilService.SpawnAffectedSigil (reflected): two dummies 1m apart (footprints overlap -> must
/// merge into ONE cluster) and a third 8m away (no overlap -> stays its own cluster). Asserts the live
/// cluster count via the internal ActiveSigilClusterCount hook: 1 after the overlapping pair, 2 after
/// the far one. Starts the round first if needed (the floor raycast and dummy spawns need a generated
/// facility) and defers the check; watch the server log for "[goc-sigil-merge] RESULT=".
/// </summary>
public sealed class GocSigilMergeScenario : IBehaviorScenario
{
    private const int OwnerId = 770001; // synthetic field owner key; not a real player id.

    public string Name => "goc-sigil-merge";

    public string Description => "Overlapping anti-gravity sigils merge into one; far-apart sigils stay separate.";

    public IReadOnlyCollection<string> Aliases => ["sigil-merge", "goc-merge"];

    public BehaviorTestResult Run(BehaviorScenarioContext context)
    {
        object? sigil = ResolveSigilService(out string reason);
        context.Require(sigil != null, $"could not resolve OccultSigilService: {reason}");

        if (!Round.IsRoundStarted)
        {
            Round.Start();
            context.Info("goc-sigil-merge round not started; started it, running the merge check in 15s after facility generation (watch for '[goc-sigil-merge] RESULT=').");
            BehaviorScenarioContext captured = context;
            object capturedSigil = sigil!;
            Timing.CallDelayed(15f, () =>
            {
                try
                {
                    RunCore(captured, capturedSigil);
                }
                catch (Exception ex)
                {
                    Logger.Error($"[goc-sigil-merge] RESULT=FAIL error={ex.GetBaseException().Message}");
                }
            });
            return context.Pass();
        }

        RunCore(context, sigil!);
        return context.Pass();
    }

    private static void RunCore(BehaviorScenarioContext context, object sigil)
    {
        MethodInfo spawn = sigil.GetType().GetMethod("SpawnAffectedSigil", BindingFlags.Instance | BindingFlags.Public)
            ?? throw new BehaviorAssertionException("SpawnAffectedSigil(int, Player) not found");
        MethodInfo count = sigil.GetType().GetMethod("ActiveSigilClusterCount", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new BehaviorAssertionException("ActiveSigilClusterCount(int) not found");

        context.Require(Round.IsRoundStarted, "round is not started; facility not generated so there is no floor to seat sigils on");

        // Anchor dummy on a real facility spawn so the ground raycast has geometry under it.
        Player anchor = context.SpawnDummy("merge-anchor");
        anchor.SetRole(RoleTypeId.ClassD, RoleChangeReason.RemoteAdmin, RoleSpawnFlags.All);
        context.Require(anchor.IsAlive, "anchor dummy is not alive after role assign");
        context.Require(anchor.Position.y > -1500f, "anchor dummy did not spawn on the generated facility (fell through / void)");
        Vector3 basePos = anchor.Position;
        context.Info($"goc-sigil-merge anchor at {Fmt(basePos)}");

        // Second dummy 1.9m away on the same floor: the circles (single footprint ~1.05m radius, so
        // they touch at ~2.1m) clearly OVERLAP, so it must MERGE. 1.9m specifically sits in the band
        // the old subtractive 0.35m margin left un-merged (threshold was ~1.75m) — the regression this
        // guards. With additive reach any overlap merges.
        Player near = context.SpawnDummy("merge-near");
        near.SetRole(RoleTypeId.ClassD, RoleChangeReason.RemoteAdmin, RoleSpawnFlags.All);
        near.Position = basePos + new Vector3(1.9f, 0f, 0f);

        // Third dummy 8m away: well outside any merged footprint, so it stays its own cluster.
        Player far = context.SpawnDummy("merge-far");
        far.SetRole(RoleTypeId.ClassD, RoleChangeReason.RemoteAdmin, RoleSpawnFlags.All);
        far.Position = basePos + new Vector3(8.0f, 0f, 0f);

        Seat(spawn, sigil, anchor);
        int afterAnchor = Count(count, sigil);
        context.Info($"goc-sigil-merge clusters after anchor = {afterAnchor} (expect 1)");
        context.Require(afterAnchor == 1, $"expected 1 cluster after the first sigil, got {afterAnchor}");

        Seat(spawn, sigil, near);
        int afterNear = Count(count, sigil);
        context.Info($"goc-sigil-merge clusters after overlapping near dummy = {afterNear} (expect 1 = MERGED)");
        context.Require(afterNear == 1, $"overlapping sigils did not merge: expected 1 cluster, got {afterNear}");

        Seat(spawn, sigil, far);
        int afterFar = Count(count, sigil);
        context.Info($"goc-sigil-merge clusters after far dummy = {afterFar} (expect 2 = separate)");
        context.Require(afterFar == 2, $"far sigil should stay separate: expected 2 clusters, got {afterFar}");

        Logger.Info($"[goc-sigil-merge] RESULT=PASS anchor={afterAnchor} near={afterNear}(merged) far={afterFar}(separate). Overlapping embles merged to one; the distant emble stayed its own.");
    }

    private static void Seat(MethodInfo spawn, object sigil, Player player) =>
        spawn.Invoke(sigil, new object[] { OwnerId, player });

    private static int Count(MethodInfo count, object sigil) =>
        (int)(count.Invoke(sigil, new object[] { OwnerId }) ?? 0);

    private static object? ResolveSigilService(out string reason)
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
            return null;
        }

        object? service = pluginType.GetProperty("OccultSigil", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(instance);
        if (service == null)
        {
            reason = "OccultSigil service is unavailable (occult_sigil.enabled=false, or model failed to load)";
            return null;
        }

        return service;
    }

    private static string Fmt(Vector3 value) => $"{value.x:0.##},{value.y:0.##},{value.z:0.##}";
}
