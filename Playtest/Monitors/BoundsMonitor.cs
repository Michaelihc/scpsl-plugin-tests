using System.Collections.Generic;
using PlayerRoles.FirstPersonControl;
using PlaytestHarness.Actors;
using PlaytestHarness.Probes;
using PlaytestHarness.Telemetry;
using UnityEngine;

namespace PlaytestHarness.Monitors;

/// <summary>
/// Watches owned actors for spatial-truth violations: TryGetRoom failing (out of every room's
/// bounds), continuous Y sinking, or staying not-grounded longer than the limit. Runs on the
/// MonitorHost tick for the whole run — this is the "leave-alone void soak" oracle.
/// </summary>
internal sealed class BoundsMonitor : IRoundMonitor
{
    private const float NotGroundedLimitSeconds = 5f;
    private const float SinkLimitMeters = 6f;

    private readonly ActorRegistry _registry;
    private readonly EventLog _log;
    // Keyed by Actor: destroyed ReferenceHubs throw NRE from GetHashCode (dead gameObject).
    private readonly List<Violation> _violations = new();
    private readonly Dictionary<Actor, TrackState> _tracks = new();
    private readonly HashSet<Actor> _seenThisTick = new();
    private readonly List<Actor> _staleTracks = new();

    private sealed class TrackState
    {
        public float LastGroundedTime;
        public float LastGroundedY;
        public bool ReportedOutOfBounds;
        public bool ReportedAirborne;
        public bool ReportedSinking;
    }

    public BoundsMonitor(ActorRegistry registry, EventLog log)
    {
        _registry = registry;
        _log = log;
    }

    public string Name => "BoundsMonitor";

    public IReadOnlyList<Violation> Violations => _violations;

    public void OnEvent(TelemetryEvent e)
    {
    }

    public void Tick()
    {
        _seenThisTick.Clear();
        foreach (Actor actor in System.Linq.Enumerable.ToArray(_registry.Actors))
        {
            ReferenceHub hub = actor.Hub;
            if (!hub || !hub.gameObject || !actor.IsReady)
            {
                continue;
            }

            _seenThisTick.Add(actor);

            // Only monitor FPC (alive, placed) actors; spectators/none are not spatial.
            if (hub.roleManager.CurrentRole is not PlayerRoles.FirstPersonControl.IFpcRole)
            {
                _tracks.Remove(actor);
                continue;
            }

            Vector3 pos = hub.transform.position;

            // A feature transition may deliberately own a bounded off-map pocket between two exact
            // expected destinations. Suppress ordinary room/ground checks only until the final move
            // expectation is consumed (or the allowance expires); teleports remain fully monitored.
            // The track is kept (baseline refreshed in place, reported-flags preserved) so closing
            // a transit does not recreate a blank state and double-report an unchanged condition.
            if (_registry.IsFeatureSpatialTransitionActive(hub))
            {
                if (_tracks.TryGetValue(actor, out TrackState? suppressed))
                {
                    suppressed.LastGroundedTime = Time.time;
                    suppressed.LastGroundedY = pos.y;
                }

                continue;
            }

            if (!_tracks.TryGetValue(actor, out TrackState? track))
            {
                track = new TrackState { LastGroundedTime = Time.time, LastGroundedY = pos.y };
                _tracks[actor] = track;
            }

            bool inRoom = MapGeneration.RoomUtils.TryGetRoom(pos, out MapGeneration.RoomIdentifier room) && room != null;
            if (!inRoom && !track.ReportedOutOfBounds)
            {
                track.ReportedOutOfBounds = true;
                Report(actor, $"TryGetRoom FAILED at {PlacementProbes.Format(pos)} — actor left every room's bounds");
            }
            else if (inRoom)
            {
                track.ReportedOutOfBounds = false;
            }

            if (hub.IsGrounded())
            {
                track.LastGroundedTime = Time.time;
                track.LastGroundedY = pos.y;
                track.ReportedAirborne = false;
                track.ReportedSinking = false;
            }
            else
            {
                if (!track.ReportedAirborne && Time.time - track.LastGroundedTime > NotGroundedLimitSeconds)
                {
                    track.ReportedAirborne = true;
                    Report(actor, $"not grounded for over {NotGroundedLimitSeconds:0.#}s at {PlacementProbes.Format(pos)}");
                }

                if (!track.ReportedSinking && track.LastGroundedY - pos.y > SinkLimitMeters)
                {
                    track.ReportedSinking = true;
                    Report(actor, $"sinking: {track.LastGroundedY - pos.y:0.##}m below last grounded Y at {PlacementProbes.Format(pos)}");
                }
            }
        }

        // Prune destroyed/untracked actors so track state never accumulates across a long run.
        _staleTracks.Clear();
        foreach (Actor tracked in _tracks.Keys)
        {
            if (!_seenThisTick.Contains(tracked))
            {
                _staleTracks.Add(tracked);
            }
        }

        foreach (Actor stale in _staleTracks)
        {
            _tracks.Remove(stale);
        }
    }

    private void Report(Actor actor, string detail)
    {
        Violation violation = new(Name, actor.Label, detail, _log.ElapsedSeconds);
        _violations.Add(violation);
        EventLog.Line("VIOLATION", violation.ToString(), error: true);
        _log.Emit(new TelemetryEvent("violation", detail, new Dictionary<string, object?>
        {
            ["monitor"] = Name,
            ["actor"] = actor.Label,
        }));
    }
}
