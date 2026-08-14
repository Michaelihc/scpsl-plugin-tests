using System;
using System.Collections.Generic;
using BehaviorTestHarness.Harness;
using CommandSystem;
using CustomPlayerEffects;
using InventorySystem.Items;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.Handlers;
using LabApi.Features.Wrappers;
using Mirror;
using PlayerRoles;
using UnityEngine;

namespace BehaviorTestHarness.Commands;

/// <summary>
/// Participant-driven live-fire matrix. A real player shoots named dummies while the harness records
/// the actual Hurting event and resulting health; this catches misses, native protection, and plugins
/// that cancel or re-allow damage after CivilianProtection.
/// </summary>
[CommandHandler(typeof(RemoteAdminCommandHandler))]
public sealed class CivilianProtectionInteractiveCommand : ICommand, IUsageProvider
{
    private const float TargetHealth = 100f;
    private static readonly Dictionary<int, Session> Sessions = [];

    static CivilianProtectionInteractiveCommand()
    {
        PlayerEvents.Hurting += OnHurting;
        PlayerEvents.Left += OnPlayerLeft;
        ServerEvents.RoundRestarted += OnRoundReset;
        ServerEvents.WaitingForPlayers += OnRoundReset;
    }

    public string Command => "civprotest";

    public string[] Aliases => ["cptest", "civilianprotectiontest"];

    public string Description => "Runs the participant-driven CivilianProtection live-fire and hint matrix.";

    public string[] Usage => ["start", "check", "finish <pass|fail>", "cancel"];

    public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
    {
        string action = GetArgument(arguments, 0).ToLowerInvariant();
        if (action.Length == 0)
        {
            response = Help;
            return false;
        }

        Player? participant = Player.Get(sender);
        if (participant == null || participant.IsDummy || !participant.IsReady)
        {
            response = "Run civprotest from the in-game RA console as a connected, ready player.";
            return false;
        }

        return action switch
        {
            "start" => Start(participant, out response),
            "check" => Check(participant, out response),
            "finish" => Finish(participant, GetArgument(arguments, 1), out response),
            "cancel" => Cancel(participant, out response),
            _ => Unknown(action, out response),
        };
    }

    private static bool Start(Player participant, out string response)
    {
        CleanupExisting(participant.PlayerId, restoreParticipant: true);
        if (Sessions.Count != 0)
        {
            response = "Another participant already has an active civprotest session.";
            return false;
        }

        if (!Round.IsRoundStarted)
        {
            response = "The round is not active. Join/start the round, then run civprotest start.";
            return false;
        }

        BehaviorTestHarnessPlugin? plugin = BehaviorTestHarnessPlugin.Instance;
        if (plugin?.Dummies == null)
        {
            response = "BehaviorTestHarness is not enabled.";
            return false;
        }

        // Requeue the observer after gameplay plugins. It never mutates the event; it records the final
        // plugin decision before native damage processing.
        PlayerEvents.Hurting -= OnHurting;
        PlayerEvents.Hurting += OnHurting;

        Session session = new(participant, plugin.Dummies);
        Sessions[participant.PlayerId] = session;
        Round.IsLocked = true;

        try
        {
            SetupGuardPhase(session);
            response =
                "GUARD PHASE READY. Close RA and use the equipped COM-15 to shoot BOTH named Class-D targets once:\n" +
                "LEFT = BT-CP-PROTECTED-D (empty inventory; shot must be blocked)\n" +
                "RIGHT = BT-CP-ARMED-D (visibly holding a COM-15; shot must deal damage)\n" +
                "You should see the amber ATTACK BLOCKED/攻击已阻止 hint only for the protected target. Then run: civprotest check";
            return true;
        }
        catch (Exception ex)
        {
            CleanupExisting(participant.PlayerId, restoreParticipant: true);
            response = "Interactive test setup failed: " + ex.GetBaseException().Message;
            return false;
        }
    }

    private static bool Check(Player participant, out string response)
    {
        if (!Sessions.TryGetValue(participant.PlayerId, out Session? session))
        {
            response = "No active session. Run: civprotest start";
            return false;
        }

        return session.Stage switch
        {
            SessionStage.GuardShots => CheckGuard(session, out response),
            SessionStage.ScientistShot => CheckSingleCivilianDirection(session, RoleTypeId.Scientist, out response),
            SessionStage.ClassDShot => CheckSingleCivilianDirection(session, RoleTypeId.ClassD, out response),
            SessionStage.AwaitingReport => AlreadyComplete(out response),
            _ => WrongStage(session.Stage, out response),
        };
    }

    private static bool CheckGuard(Session session, out string response)
    {
        Player protectedTarget = session.Targets[0];
        Player armedTarget = session.Targets[1];

        if (!session.AttemptedTargetIds.Contains(protectedTarget.PlayerId) ||
            !session.AttemptedTargetIds.Contains(armedTarget.PlayerId))
        {
            response =
                $"NOT READY: shoot both targets first. observedProtected={session.AttemptedTargetIds.Contains(protectedTarget.PlayerId)} " +
                $"observedArmed={session.AttemptedTargetIds.Contains(armedTarget.PlayerId)}.";
            return false;
        }

        bool protectedBlocked = Math.Abs(protectedTarget.Health - TargetHealth) < 0.01f &&
                                session.FinalAllowed.TryGetValue(protectedTarget.PlayerId, out bool protectedAllowed) && !protectedAllowed;
        bool armedDamaged = armedTarget.Health < TargetHealth - 0.01f &&
                            session.FinalAllowed.TryGetValue(armedTarget.PlayerId, out bool armedAllowed) && armedAllowed;

        if (!protectedBlocked || !armedDamaged)
        {
            response =
                $"GUARD PHASE FAIL: protectedHealth={protectedTarget.Health:0.##} protectedBlocked={protectedBlocked}; " +
                $"armedHealth={armedTarget.Health:0.##} armedAllowed={armedDamaged}. Run civprotest cancel.";
            return false;
        }

        BehaviorTestLog.WriteInfo(
            $"interactive livefire phase=guard result=pass protectedHealth={protectedTarget.Health:0.##} armedHealth={armedTarget.Health:0.##}");
        SetupCivilianDirection(session, RoleTypeId.Scientist, RoleTypeId.ClassD, "BT-CP-CLASSD-TARGET");
        response =
            "GUARD PHASE PASS. SCIENTIST→CLASS-D PHASE READY. Close RA and shoot the armed " +
            "BT-CP-CLASSD-TARGET once with your equipped COM-15. Damage must be blocked even though both civilians are armed, " +
            "and you should see the amber blocked hint. Then run: civprotest check";
        return true;
    }

    private static bool CheckSingleCivilianDirection(Session session, RoleTypeId expectedShooter, out string response)
    {
        Player target = session.Targets[0];
        if (!session.AttemptedTargetIds.Contains(target.PlayerId))
        {
            response = $"NOT READY: no real shot from {expectedShooter} to {target.Nickname} was observed. Close RA, shoot it once, then run civprotest check.";
            return false;
        }

        bool allowed = true;
        bool blocked = Math.Abs(target.Health - TargetHealth) < 0.01f &&
                       session.FinalAllowed.TryGetValue(target.PlayerId, out allowed) && !allowed;
        if (!blocked)
        {
            response = $"{expectedShooter} PHASE FAIL: targetHealth={target.Health:0.##} finalAllowed={allowed}. Run civprotest cancel.";
            return false;
        }

        BehaviorTestLog.WriteInfo($"interactive livefire phase={expectedShooter} result=pass target={DummyRegistry.Describe(target)} health={target.Health:0.##}");

        if (expectedShooter == RoleTypeId.Scientist)
        {
            SetupCivilianDirection(session, RoleTypeId.ClassD, RoleTypeId.Scientist, "BT-CP-SCIENTIST-TARGET");
            response =
                "SCIENTIST→CLASS-D PASS. CLASS-D→SCIENTIST PHASE READY. Close RA and shoot the armed " +
                "BT-CP-SCIENTIST-TARGET once. Damage must be blocked and the amber hint should appear. Then run: civprotest check";
            return true;
        }

        session.Stage = SessionStage.AwaitingReport;
        response =
            "CLASS-D→SCIENTIST PASS. All server-side live-fire assertions passed. Report whether the amber blocked hints " +
            "were visible during the three protected shot phases with: civprotest finish pass  (or: civprotest finish fail)";
        return true;
    }

    private static void SetupGuardPhase(Session session)
    {
        CleanupTargets(session);
        PrepareShooter(session.Participant, RoleTypeId.FacilityGuard);

        Player protectedTarget = SpawnTarget(session, "CP-PROTECTED-D", RoleTypeId.ClassD, armed: false);
        Player armedTarget = SpawnTarget(session, "CP-ARMED-D", RoleTypeId.ClassD, armed: true);
        PlacePair(session.Participant, protectedTarget, armedTarget);

        session.Targets.Add(protectedTarget);
        session.Targets.Add(armedTarget);
        session.Stage = SessionStage.GuardShots;
        session.ResetObservations();
        LogInventory(protectedTarget, "guard-protected");
        LogInventory(armedTarget, "guard-armed");
    }

    private static void SetupCivilianDirection(Session session, RoleTypeId shooterRole, RoleTypeId targetRole, string targetLabel)
    {
        CleanupTargets(session);
        PrepareShooter(session.Participant, shooterRole);
        Player target = SpawnTarget(session, targetLabel, targetRole, armed: true);
        PlaceSingle(session.Participant, target);

        session.Targets.Add(target);
        session.Stage = shooterRole == RoleTypeId.Scientist ? SessionStage.ScientistShot : SessionStage.ClassDShot;
        session.ResetObservations();
        LogInventory(target, shooterRole == RoleTypeId.Scientist ? "scientist-to-classd" : "classd-to-scientist");
    }

    private static void PrepareShooter(Player participant, RoleTypeId role)
    {
        participant.SetRole(role, RoleChangeReason.RemoteAdmin, RoleSpawnFlags.None);
        participant.ClearInventory();
        participant.DisableEffect<SpawnProtected>();
        participant.MaxHealth = Math.Max(TargetHealth, participant.MaxHealth);
        participant.Health = participant.MaxHealth;

        Item firearm = participant.AddItem(ItemType.GunCOM15, ItemAddReason.AdminCommand)
            ?? throw new InvalidOperationException("could not add the participant COM-15");
        participant.AddAmmo(ItemType.Ammo9x19, 60);
        participant.CurrentItem = firearm;
    }

    private static Player SpawnTarget(Session session, string label, RoleTypeId role, bool armed)
    {
        BehaviorTestLog log = new();
        Player target = session.Dummies.Spawn(label, log);
        target.SetRole(role, RoleChangeReason.RemoteAdmin, RoleSpawnFlags.None);
        target.ClearInventory();
        target.DisableEffect<SpawnProtected>();
        target.MaxHealth = TargetHealth;
        target.Health = TargetHealth;

        if (armed)
        {
            Item weapon = target.AddItem(ItemType.GunCOM15, ItemAddReason.AdminCommand)
                ?? throw new InvalidOperationException($"could not arm target {label}");
            target.CurrentItem = weapon;
        }

        return target;
    }

    private static void PlacePair(Player participant, Player left, Player right)
    {
        GetFlatAxes(participant, out Vector3 forward, out Vector3 rightAxis);
        Vector3 origin = participant.Position + forward * 5f;
        left.Position = Ground(origin - rightAxis * 1.35f);
        right.Position = Ground(origin + rightAxis * 1.35f);
        BehaviorTestLog.WriteInfo($"interactive targets placed left={Format(left.Position)} right={Format(right.Position)}");
    }

    private static void PlaceSingle(Player participant, Player target)
    {
        GetFlatAxes(participant, out Vector3 forward, out _);
        target.Position = Ground(participant.Position + forward * 5f);
        BehaviorTestLog.WriteInfo($"interactive target placed target={DummyRegistry.Describe(target)} position={Format(target.Position)}");
    }

    private static void GetFlatAxes(Player participant, out Vector3 forward, out Vector3 right)
    {
        forward = participant.ReferenceHub.PlayerCameraReference.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.01f)
        {
            forward = Vector3.forward;
        }

        forward.Normalize();
        right = Vector3.Cross(Vector3.up, forward).normalized;
    }

    private static Vector3 Ground(Vector3 candidate)
    {
        Vector3 rayOrigin = candidate + Vector3.up * 2f;
        if (Physics.Raycast(rayOrigin, Vector3.down, out RaycastHit hit, 8f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
        {
            return hit.point + Vector3.up * 0.15f;
        }

        BehaviorTestLog.WriteInfo($"interactive target ground probe missed candidate={Format(candidate)}");
        return candidate;
    }

    private static void OnHurting(PlayerHurtingEventArgs ev)
    {
        Player? attacker = ev.Attacker;
        if (attacker == null || !Sessions.TryGetValue(attacker.PlayerId, out Session? session))
        {
            return;
        }

        Player target = ev.Player;
        if (!session.Targets.Exists(candidate => candidate.PlayerId == target.PlayerId))
        {
            return;
        }

        session.AttemptedTargetIds.Add(target.PlayerId);
        session.FinalAllowed[target.PlayerId] = ev.IsAllowed;
        BehaviorTestLog.WriteInfo(
            $"interactive live shot phase={session.Stage} attacker={DummyRegistry.Describe(attacker)} target={DummyRegistry.Describe(target)} " +
            $"allowedAfterPlugins={ev.IsAllowed} healthBefore={target.Health:0.##}");
    }

    private static bool Finish(Player participant, string report, out string response)
    {
        if (!Sessions.TryGetValue(participant.PlayerId, out Session? session) || session.Stage != SessionStage.AwaitingReport)
        {
            response = "Complete all live-fire phases first; use civprotest check after each phase.";
            return false;
        }

        bool passed = string.Equals(report, "pass", StringComparison.OrdinalIgnoreCase);
        bool failed = string.Equals(report, "fail", StringComparison.OrdinalIgnoreCase);
        if (!passed && !failed)
        {
            response = "Report the visible hint result with: civprotest finish pass  (or: civprotest finish fail)";
            return false;
        }

        BehaviorTestLog.WriteInfo($"interactive livefire participant report player={DummyRegistry.Describe(participant)} hintsVisible={passed}");
        CleanupExisting(participant.PlayerId, restoreParticipant: true);
        response = passed
            ? "INTERACTIVE PASS: all real-shot server assertions and your client-side hint observations passed. Prior role/position restored."
            : "INTERACTIVE FAIL: server live-fire assertions passed, but you reported missing/incorrect hints. Prior role/position restored.";
        return passed;
    }

    private static bool Cancel(Player participant, out string response)
    {
        bool existed = CleanupExisting(participant.PlayerId, restoreParticipant: true);
        response = existed ? "Interactive test cancelled; targets removed and prior role/position restored." : "No active interactive test session.";
        return existed;
    }

    private static void OnPlayerLeft(PlayerLeftEventArgs ev)
    {
        CleanupExisting(ev.Player.PlayerId, restoreParticipant: false);
    }

    private static void OnRoundReset()
    {
        List<int> playerIds = new(Sessions.Keys);
        foreach (int playerId in playerIds)
        {
            CleanupExisting(playerId, restoreParticipant: false);
        }
    }

    private static bool CleanupExisting(int playerId, bool restoreParticipant)
    {
        if (!Sessions.TryGetValue(playerId, out Session? session))
        {
            return false;
        }

        Sessions.Remove(playerId);
        CleanupTargets(session);
        Round.IsLocked = session.OriginalRoundLocked;

        if (restoreParticipant && !session.Participant.IsDestroyed)
        {
            session.Participant.SetRole(session.OriginalRole, RoleChangeReason.RemoteAdmin, RoleSpawnFlags.AssignInventory);
            session.Participant.Position = session.OriginalPosition;
            session.Participant.Health = Math.Min(session.OriginalHealth, session.Participant.MaxHealth);
        }

        return true;
    }

    private static void CleanupTargets(Session session)
    {
        foreach (Player target in session.Targets)
        {
            if (!target.IsDestroyed && target.GameObject != null)
            {
                NetworkServer.Destroy(target.GameObject);
            }
        }

        session.Targets.Clear();
        session.ResetObservations();
    }

    private static void LogInventory(Player player, string label)
    {
        List<string> items = [];
        foreach (ItemBase item in player.Inventory.UserInventory.Items.Values)
        {
            items.Add($"{item.ItemTypeId}/{item.Category}/{item.ItemSerial}");
        }

        BehaviorTestLog.WriteInfo($"interactive inventory label={label} player={DummyRegistry.Describe(player)} items=[{string.Join(",", items)}]");
    }

    private static string GetArgument(ArraySegment<string> arguments, int index)
    {
        return arguments.Array != null && index >= 0 && index < arguments.Count
            ? arguments.Array[arguments.Offset + index]
            : string.Empty;
    }

    private static bool Unknown(string action, out string response)
    {
        response = $"Unknown action '{action}'. {Help}";
        return false;
    }

    private static bool WrongStage(SessionStage stage, out string response)
    {
        response = $"Wrong test stage ({stage}). Run civprotest cancel, then civprotest start.";
        return false;
    }

    private static bool AlreadyComplete(out string response)
    {
        response = "All shot phases passed. Use civprotest finish pass or civprotest finish fail.";
        return true;
    }

    private static string Format(Vector3 value) => $"{value.x:0.##},{value.y:0.##},{value.z:0.##}";

    private const string Help =
        "Usage: civprotest start | check | finish <pass|fail> | cancel. " +
        "This runs Guard→two Class-D targets, Scientist→Class-D, and Class-D→Scientist using your real COM-15 shots.";

    private enum SessionStage
    {
        GuardShots,
        ScientistShot,
        ClassDShot,
        AwaitingReport,
    }

    private sealed class Session
    {
        public Session(Player participant, DummyRegistry dummies)
        {
            Participant = participant;
            Dummies = dummies;
            OriginalRole = participant.Role;
            OriginalPosition = participant.Position;
            OriginalHealth = participant.Health;
            OriginalRoundLocked = Round.IsLocked;
        }

        public Player Participant { get; }

        public DummyRegistry Dummies { get; }

        public RoleTypeId OriginalRole { get; }

        public Vector3 OriginalPosition { get; }

        public float OriginalHealth { get; }

        public bool OriginalRoundLocked { get; }

        public List<Player> Targets { get; } = [];

        public HashSet<int> AttemptedTargetIds { get; } = [];

        public Dictionary<int, bool> FinalAllowed { get; } = [];

        public SessionStage Stage { get; set; }

        public void ResetObservations()
        {
            AttemptedTargetIds.Clear();
            FinalAllowed.Clear();
        }
    }
}
