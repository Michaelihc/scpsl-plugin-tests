using System.Collections.Generic;
using System.Linq;
using BehaviorTestHarness.Harness;
using LabApi.Features.Wrappers;
using MapGeneration;
using MEC;
using UnityEngine;
using Logger = LabApi.Features.Console.Logger;

namespace BehaviorTestHarness.Scenarios;

/// <summary>
/// Discovery: logs the Alpha Warhead geometry so we can aim the nuke-arm effect at the warhead
/// "death pit" instead of the arming lever. Logs the controller + panel positions, the HczWarhead
/// room center/bounds, and downward raycasts to find the pit floor depth. Read the [PitFind] lines.
/// </summary>
public sealed class GocPitFindScenario : IBehaviorScenario
{
    public string Name => "goc-pitfind";

    public string Description => "Logs Alpha Warhead controller/panel/room geometry to locate the death pit.";

    public IReadOnlyCollection<string> Aliases => ["pitfind"];

    public BehaviorTestResult Run(BehaviorScenarioContext context)
    {
        if (!Round.IsRoundStarted)
        {
            Round.Start();
            context.Info("goc-pitfind round not started; started it, dumping warhead geometry in 15s.");
            Timing.CallDelayed(15f, Dump);
            return context.Pass();
        }

        Dump();
        return context.Pass();
    }

    private static void Dump()
    {
        // Warhead controller transform (likely the warhead/pit anchor).
        if (AlphaWarheadController.SingletonSet && AlphaWarheadController.Singleton != null)
        {
            Vector3 c = AlphaWarheadController.Singleton.transform.position;
            Logger.Info($"[PitFind] AlphaWarheadController.transform = {Fmt(c)}");
        }
        else
        {
            Logger.Info("[PitFind] AlphaWarheadController singleton not set");
        }

        // Arming panel + lever (what the effect currently uses).
        AlphaWarheadNukesitePanel panel = AlphaWarheadNukesitePanel.Singleton;
        if (panel != null)
        {
            Logger.Info($"[PitFind] NukesitePanel.transform = {Fmt(panel.transform.position)}; lever = {(panel.lever != null ? Fmt(panel.lever.position) : "null")}");
            RaycastDown("panelDown", panel.transform.position, 80f);
        }
        else
        {
            Logger.Info("[PitFind] NukesitePanel singleton null");
        }

        // HczWarhead room(s): center + bounds.
        foreach (Room room in Room.List.Where(r => r.Name == RoomName.HczWarhead))
        {
            Bounds b = room.Base.WorldspaceBounds;
            Logger.Info($"[PitFind] HczWarhead room pos={Fmt(room.Position)} boundsCenter={Fmt(b.center)} boundsSize={Fmt(b.size)} min={Fmt(b.min)} max={Fmt(b.max)}");
            RaycastDown("roomCenterDown", b.center, 80f);
            RaycastDown("roomPosDown", room.Position + Vector3.up * 3f, 80f);
        }
    }

    private static void RaycastDown(string label, Vector3 from, float distance)
    {
        if (Physics.Raycast(from + Vector3.up * 1f, Vector3.down, out RaycastHit hit, distance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
        {
            Logger.Info($"[PitFind] {label} from {Fmt(from)} -> floor {Fmt(hit.point)} (drop {(from.y - hit.point.y):0.#}m, collider={hit.collider?.name})");
        }
        else
        {
            Logger.Info($"[PitFind] {label} from {Fmt(from)} -> no floor within {distance}m");
        }
    }

    private static string Fmt(Vector3 v) => $"({v.x:0.##},{v.y:0.##},{v.z:0.##})";
}
