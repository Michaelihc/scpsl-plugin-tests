using System;
using System.Collections.Generic;
using BehaviorTestHarness.Harness;
using CommandSystem;
using InventorySystem.Items;
using LabApi.Features.Wrappers;
using Mirror;
using PlayerRoles;
using UnityEngine;

namespace BehaviorTestHarness.Commands;

/// <summary>
/// Staged manual test for one real RA user. The server asserts health behavior while the user
/// confirms the HSM text rendered by their actual client.
/// </summary>
[CommandHandler(typeof(RemoteAdminCommandHandler))]
public sealed class CivilianProtectionInteractiveCommand : ICommand, IUsageProvider
{
    private const float TestDamage = 15f;
    private static readonly Dictionary<int, Session> Sessions = [];

    public string Command => "civprotest";

    public string[] Aliases => ["cptest", "civilianprotectiontest"];

    public string Description => "Runs the participant-driven CivilianProtection client hint and damage test.";

    public string[] Usage => ["start", "armed", "finish <pass|fail>", "cancel"];

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
            "armed" => Armed(participant, out response),
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

        BehaviorTestHarnessPlugin? plugin = BehaviorTestHarnessPlugin.Instance;
        if (plugin?.Dummies == null)
        {
            response = "BehaviorTestHarness is not enabled.";
            return false;
        }

        BehaviorTestLog log = new();
        Player attacker = plugin.Dummies.Spawn("civpro-guard", log);
        Session session = new(participant, attacker);
        Sessions[participant.PlayerId] = session;

        try
        {
            if (!Round.IsRoundStarted)
            {
                throw new InvalidOperationException("the round is not active; join/start the round, then run civprotest start");
            }

            Round.IsLocked = true;
            participant.SetRole(RoleTypeId.ClassD, RoleChangeReason.RemoteAdmin, RoleSpawnFlags.None);
            participant.ClearInventory();
            participant.MaxHealth = Math.Max(100f, participant.MaxHealth);
            participant.Health = participant.MaxHealth;

            attacker.SetRole(RoleTypeId.FacilityGuard, RoleChangeReason.RemoteAdmin, RoleSpawnFlags.None);
            attacker.ClearInventory();
            attacker.Position = participant.Position + participant.ReferenceHub.PlayerCameraReference.right * 1.5f;
            attacker.MaxHealth = Math.Max(100f, attacker.MaxHealth);
            attacker.Health = attacker.MaxHealth;

            float before = participant.Health;
            bool applied = participant.Damage(TestDamage, attacker, Vector3.zero, 0);
            float after = participant.Health;
            BehaviorTestLog.WriteInfo($"interactive civilian stage=unarmed participant={DummyRegistry.Describe(participant)} health={before:0.##}->{after:0.##} applied={applied}");

            if (Math.Abs(after - before) >= 0.01f)
            {
                CleanupExisting(participant.PlayerId, restoreParticipant: true);
                response = $"FAIL: unarmed Foundation damage changed your health {before:0.##}->{after:0.##}.";
                return false;
            }

            session.Stage = SessionStage.AwaitingArmed;
            response =
                "STEP 1 PASS: server health stayed unchanged. Confirm you saw the green PROTECTED/已受保护 hint, " +
                "then run: civprotest armed\n" +
                "Note: this test temporarily replaces your role and inventory; finish/cancel restores your prior role and position with its normal loadout.";
            return true;
        }
        catch (Exception ex)
        {
            CleanupExisting(participant.PlayerId, restoreParticipant: true);
            response = "Interactive test setup failed: " + ex.GetBaseException().Message;
            return false;
        }
    }

    private static bool Armed(Player participant, out string response)
    {
        if (!TryGetSession(participant, SessionStage.AwaitingArmed, out Session? session, out response))
        {
            return false;
        }

        participant.AddItem(ItemType.GunCOM15, ItemAddReason.AdminCommand);
        float before = participant.Health;
        bool applied = participant.Damage(TestDamage, session!.Attacker, Vector3.zero, 0);
        float after = participant.Health;
        BehaviorTestLog.WriteInfo($"interactive civilian stage=armed participant={DummyRegistry.Describe(participant)} item={ItemType.GunCOM15} health={before:0.##}->{after:0.##} applied={applied}");

        if (after >= before - 0.01f)
        {
            response = $"FAIL: armed Foundation damage was still blocked; health remained {before:0.##}->{after:0.##}. Run civprotest cancel.";
            return false;
        }

        session.Stage = SessionStage.AwaitingReport;
        response =
            $"STEP 2 PASS: armed damage reduced health {before:0.##}->{after:0.##}. " +
            "Confirm you saw the red PROTECTION LOST/保护已失效 hint. Report the client result with: " +
            "civprotest finish pass  (or: civprotest finish fail)";
        return true;
    }

    private static bool Finish(Player participant, string report, out string response)
    {
        if (!TryGetSession(participant, SessionStage.AwaitingReport, out _, out response))
        {
            return false;
        }

        bool passed = string.Equals(report, "pass", StringComparison.OrdinalIgnoreCase);
        bool failed = string.Equals(report, "fail", StringComparison.OrdinalIgnoreCase);
        if (!passed && !failed)
        {
            response = "Report your visual result with: civprotest finish pass  (or: civprotest finish fail)";
            return false;
        }

        BehaviorTestLog.WriteInfo($"interactive civilian participant report player={DummyRegistry.Describe(participant)} hsmVisible={passed}");
        CleanupExisting(participant.PlayerId, restoreParticipant: true);
        response = passed
            ? "INTERACTIVE PASS: server damage assertions and your client-side HSM observation passed. Your prior role and position were restored."
            : "INTERACTIVE FAIL: server damage assertions passed, but you reported missing/incorrect HSM output. Your prior role and position were restored.";
        return passed;
    }

    private static bool Cancel(Player participant, out string response)
    {
        bool existed = CleanupExisting(participant.PlayerId, restoreParticipant: true);
        response = existed ? "Interactive test cancelled; your prior role and position were restored." : "No active interactive test session.";
        return existed;
    }

    private static bool TryGetSession(
        Player participant,
        SessionStage expected,
        out Session? session,
        out string response)
    {
        if (!Sessions.TryGetValue(participant.PlayerId, out session))
        {
            response = "No active session. Run: civprotest start";
            return false;
        }

        if (session.Stage != expected)
        {
            response = $"Wrong test stage ({session.Stage}). Run civprotest cancel, then civprotest start.";
            return false;
        }

        response = string.Empty;
        return true;
    }

    private static bool CleanupExisting(int playerId, bool restoreParticipant)
    {
        if (!Sessions.TryGetValue(playerId, out Session? session))
        {
            return false;
        }

        Sessions.Remove(playerId);
        Round.IsLocked = session.OriginalRoundLocked;
        if (session.Attacker.GameObject != null)
        {
            NetworkServer.Destroy(session.Attacker.GameObject);
        }

        if (restoreParticipant && !session.Participant.IsDestroyed)
        {
            session.Participant.SetRole(session.OriginalRole, RoleChangeReason.RemoteAdmin, RoleSpawnFlags.AssignInventory);
            session.Participant.Position = session.OriginalPosition;
            session.Participant.Health = Math.Min(session.OriginalHealth, session.Participant.MaxHealth);
        }

        return true;
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

    private const string Help =
        "Usage: civprotest start | armed | finish <pass|fail> | cancel. " +
        "This temporarily changes your role/inventory and restores the prior role/position with its normal loadout at the end.";

    private enum SessionStage
    {
        AwaitingArmed,
        AwaitingReport,
    }

    private sealed class Session
    {
        public Session(Player participant, Player attacker)
        {
            Participant = participant;
            Attacker = attacker;
            OriginalRole = participant.Role;
            OriginalPosition = participant.Position;
            OriginalHealth = participant.Health;
            OriginalRoundLocked = Round.IsLocked;
        }

        public Player Participant { get; }

        public Player Attacker { get; }

        public RoleTypeId OriginalRole { get; }

        public Vector3 OriginalPosition { get; }

        public float OriginalHealth { get; }

        public bool OriginalRoundLocked { get; }

        public SessionStage Stage { get; set; }
    }
}
