using System;
using System.Collections.Generic;
using LabApi.Events.Arguments.Scp106Events;
using LabApi.Events.Arguments.Scp173Events;
using LabApi.Events.Handlers;
using MapGeneration;
using MEC;
using Mirror;
using PlayerRoles.FirstPersonControl;
using PlayerRoles.PlayableScps.Scp106;
using PlayerRoles.PlayableScps.Scp173;
using PlaytestHarness.Core;
using PlaytestHarness.Telemetry;
using RelativePositioning;
using UnityEngine;

namespace PlaytestHarness.Actors;

public enum AbilityExpectedOutcome
{
    Succeeded,
    Cancelled,
}

/// <summary>Explicit native SCP ability inputs with cancellable-event and outcome confirmation.</summary>
internal static class ScpAbilityFulfiller
{
    internal static IEnumerator<float> AttemptBreakneck(
        ScenarioContext ctx,
        Actor actor,
        AbilityExpectedOutcome expected)
    {
        if (actor.Hub.roleManager.CurrentRole is not Scp173Role role ||
            !role.SubroutineModule.TryGetSubroutine(out Scp173BreakneckSpeedsAbility ability))
        {
            throw new RequireException(
                $"AttemptBreakneck: actor {actor.Label} is not a ready SCP-173 ({actor.Role})");
        }

        double cooldownDeadline = ctx.ElapsedSeconds + 45f;
        while (!ability.Cooldown.IsReady && ctx.ElapsedSeconds < cooldownDeadline)
        {
            yield return Timing.WaitForOneFrame;
        }
        if (!ability.Cooldown.IsReady)
        {
            throw new RequireException(
                $"AttemptBreakneck: native 40s cooldown never became ready for {actor.Label}");
        }

        bool activeBefore = ability.IsActive;
        bool wantedActive = !activeBefore;
        bool changingSeen = false;
        bool finalAllowed = true;
        bool changedSeen = false;
        bool changedActive = ability.IsActive;

        void OnChanging(Scp173BreakneckSpeedChangingEventArgs ev)
        {
            if (ev.Player?.ReferenceHub != actor.Hub || ev.Active != wantedActive)
            {
                return;
            }

            changingSeen = true;
            finalAllowed = ev.IsAllowed;
        }

        void OnChanged(Scp173BreakneckSpeedChangedEventArgs ev)
        {
            if (ev.Player?.ReferenceHub != actor.Hub)
            {
                return;
            }

            changedSeen = true;
            changedActive = ev.Active;
        }

        Scp173Events.BreakneckSpeedChanging += OnChanging;
        Scp173Events.BreakneckSpeedChanged += OnChanged;
        try
        {
            // Breakneck's native command has no payload. Submit it once at the authoritative server
            // boundary, matching Hunter's Atlas and pump-firearm adapters; the RA dummy Run action can
            // enqueue a delayed second command on long-lived headless actors.
            using (NetworkReaderPooled reader = NetworkReaderPool.Get(
                       new ArraySegment<byte>(Array.Empty<byte>())))
            {
                ability.ServerProcessCmd(reader);
            }

            double deadline = ctx.ElapsedSeconds + 3f;
            while (!changingSeen && ctx.ElapsedSeconds < deadline)
            {
                yield return Timing.WaitForOneFrame;
            }

            if (!changingSeen)
            {
                throw new RequireException(
                    $"AttemptBreakneck: native input never reached BreakneckSpeedChanging for {actor.Label} " +
                    $"(active={ability.IsActive}, cooldownReady={ability.Cooldown.IsReady})");
            }

            yield return Timing.WaitForOneFrame;
            if (expected == AbilityExpectedOutcome.Cancelled)
            {
                if (changedSeen || ability.IsActive != activeBefore)
                {
                    throw new RequireException(
                        $"AttemptBreakneck: expected cancellation for {actor.Label}, but allowed_observed={finalAllowed} changed={changedSeen} active={activeBefore}->{ability.IsActive}");
                }
            }
            else if (!changedSeen || changedActive != wantedActive || ability.IsActive != wantedActive)
            {
                throw new RequireException(
                    $"AttemptBreakneck: expected native state {wantedActive} for {actor.Label}, but allowed_observed={finalAllowed} changed={changedSeen}/{changedActive} active={ability.IsActive}");
            }

            ctx.Emit(new TelemetryEvent("scp_ability_attempt", "breakneck",
                new Dictionary<string, object?>
                {
                    ["actor"] = actor.Label,
                    ["expected"] = expected.ToString(),
                    ["wanted_active"] = wantedActive,
                    ["changing_seen"] = changingSeen,
                    ["final_allowed"] = finalAllowed,
                    ["changed_seen"] = changedSeen,
                    ["input_path"] = "server-process-cmd",
                    ["active_after"] = ability.IsActive,
                }));
            EventLog.Line("STEP",
                $"SCP-173 Breakneck native attempt actor={actor.Label} input=ServerProcessCmd expected={expected} allowed={finalAllowed} active={ability.IsActive}");
        }
        finally
        {
            Scp173Events.BreakneckSpeedChanging -= OnChanging;
            Scp173Events.BreakneckSpeedChanged -= OnChanged;
        }
    }

    internal static IEnumerator<float> AttemptHuntersAtlas(
        ScenarioContext ctx,
        Actor actor,
        Vector3 mapSelection,
        AbilityExpectedOutcome expected)
    {
        if (ctx.Fidelity == Fidelity.EndToEnd)
        {
            throw new InvalidOperationException(
                "AttemptHuntersAtlas is Standard-only because its delayed native teleport needs an actor-scoped monitor expectation.");
        }

        if (actor.Hub.roleManager.CurrentRole is not Scp106Role role ||
            !role.SubroutineModule.TryGetSubroutine(out Scp106HuntersAtlasAbility ability))
        {
            throw new RequireException(
                $"AttemptHuntersAtlas: actor {actor.Label} is not a ready SCP-106 ({actor.Role})");
        }

        if (!actor.Hub.IsGrounded() || role.Sinkhole.SubmergeProgress > 0f ||
            !role.Sinkhole.ReadonlyCooldown.IsReady)
        {
            throw new RequireException(
                $"AttemptHuntersAtlas: {actor.Label} is not ready (grounded={actor.Hub.IsGrounded()} submerge={role.Sinkhole.SubmergeProgress:0.###} cooldownReady={role.Sinkhole.ReadonlyCooldown.IsReady})");
        }

        if (!mapSelection.TryGetRoom(out RoomIdentifier room))
        {
            throw new RequireException(
                $"AttemptHuntersAtlas: target {mapSelection} is not inside a native room");
        }

        VigorStat vigor = actor.Hub.playerStats.GetModule<VigorStat>();
        float vigorBefore = vigor.CurValue;
        float estimatedCost = Vector3.ProjectOnPlane(actor.Position - mapSelection, Vector3.up).magnitude * 0.015f;
        if (vigorBefore < 0.25f || estimatedCost > vigorBefore)
        {
            throw new RequireException(
                $"AttemptHuntersAtlas: insufficient vigor for {actor.Label} (vigor={vigorBefore:0.###}, estimatedCost={estimatedCost:0.###})");
        }

        bool usingSeen = false;
        bool finalAllowed = true;
        Vector3 routedDestination = mapSelection;
        bool usedSeen = false;
        void OnUsing(Scp106UsingHunterAtlasEventArgs ev)
        {
            if (ev.Player?.ReferenceHub != actor.Hub)
            {
                return;
            }

            usingSeen = true;
            finalAllowed = ev.IsAllowed;
            routedDestination = ev.DestinationPosition;
        }

        void OnUsed(Scp106UsedHunterAtlasEventArgs ev)
        {
            if (ev.Player?.ReferenceHub == actor.Hub)
            {
                usedSeen = true;
            }
        }

        Scp106Events.UsingHunterAtlas += OnUsing;
        Scp106Events.UsedHunterAtlas += OnUsed;
        Vector3 start = actor.Position;
        bool completed = false;
        try
        {
            if (expected == AbilityExpectedOutcome.Succeeded)
            {
                ctx.Registry.AuthorizeFeatureTransition(actor, "native SCP-106 Hunter's Atlas",
                    new FeatureTransitionSpec(8f, positions: new[]
                    {
                        new PositionTransitionExpectation("Hunter's Atlas safe destination", position =>
                            Vector3.ProjectOnPlane(position - mapSelection, Vector3.up).magnitude <= 20f &&
                            Mathf.Abs(position.y - mapSelection.y) <= 45f),
                    }));
            }

            using NetworkWriterPooled writer = NetworkWriterPool.Get();
            writer.WriteRelativePosition(new RelativePosition(room.transform.position));
            Vector3Int offset = Vector3Int.RoundToInt((mapSelection - room.transform.position) * 50f);
            if (offset.x < short.MinValue || offset.x > short.MaxValue ||
                offset.z < short.MinValue || offset.z > short.MaxValue)
            {
                throw new RequireException(
                    $"AttemptHuntersAtlas: room-relative target offset overflows native shorts ({offset.x},{offset.z})");
            }

            writer.WriteShort((short)offset.x);
            writer.WriteShort((short)offset.z);
            using NetworkReaderPooled reader = NetworkReaderPool.Get(writer.ToArraySegment());
            ability.ServerProcessCmd(reader);

            if (!usingSeen)
            {
                throw new RequireException(
                    $"AttemptHuntersAtlas: server command did not reach UsingHunterAtlas for {actor.Label}");
            }

            if (expected == AbilityExpectedOutcome.Cancelled)
            {
                // Brace the actor before the stillness window: an ambient provider order or
                // residual local drive would trip the displacement bound even though the ability
                // genuinely was cancelled. The teleport oracles (wants_submerged/used/vigor) stay
                // primary; displacement only backstops them.
                if (ctx.Movement.Available && ctx.Movement.Controls(actor.Hub))
                {
                    ctx.Movement.Cancel(actor.Hub);
                    ctx.Movement.StopLocal(actor.Hub);
                }

                Vector3 braced = actor.Position;
                yield return Timing.WaitForSeconds(3.25f);
                if (ability.ServerWantsSubmerged || usedSeen ||
                    Vector3.Distance(braced, actor.Position) > 0.75f ||
                    vigor.CurValue < vigorBefore - 0.001f)
                {
                    throw new RequireException(
                        $"AttemptHuntersAtlas: expected cancellation for {actor.Label}, but allowed_observed={finalAllowed} " +
                        $"wants_submerged={ability.ServerWantsSubmerged} used={usedSeen} " +
                        $"displacement={Vector3.Distance(braced, actor.Position):0.###}m " +
                        $"vigor={vigorBefore:0.###}->{vigor.CurValue:0.###}");
                }
            }
            else
            {
                double deadline = ctx.ElapsedSeconds + 8f;
                while (!usedSeen && ctx.ElapsedSeconds < deadline)
                {
                    yield return Timing.WaitForOneFrame;
                }

                // Arrival is judged against the routed destination, not raw displacement from the
                // start — a legitimate same-room Atlas (short hop) must not false-fail. The event +
                // vigor drain confirm the native teleport actually ran.
                float arrivalError = Vector3.ProjectOnPlane(actor.Position - routedDestination, Vector3.up).magnitude;
                if (!usedSeen || arrivalError > 20f ||
                    Mathf.Abs(actor.Position.y - routedDestination.y) > 45f ||
                    vigor.CurValue >= vigorBefore)
                {
                    throw new RequireException(
                        $"AttemptHuntersAtlas: incomplete native teleport for {actor.Label} (used={usedSeen} " +
                        $"arrival_error={arrivalError:0.###}m dy={Mathf.Abs(actor.Position.y - routedDestination.y):0.###}m " +
                        $"vigor={vigorBefore:0.###}->{vigor.CurValue:0.###})");
                }
            }

            ctx.Emit(new TelemetryEvent("scp_ability_attempt", "hunters_atlas",
                new Dictionary<string, object?>
                {
                    ["actor"] = actor.Label,
                    ["expected"] = expected.ToString(),
                    ["allowed"] = finalAllowed,
                    ["used"] = usedSeen,
                    ["start"] = new[] { start.x, start.y, start.z },
                    ["selection"] = new[] { mapSelection.x, mapSelection.y, mapSelection.z },
                    ["routed_destination"] = new[] { routedDestination.x, routedDestination.y, routedDestination.z },
                    ["end"] = new[] { actor.Position.x, actor.Position.y, actor.Position.z },
                    ["vigor_before"] = vigorBefore,
                    ["vigor_after"] = vigor.CurValue,
                }));
            EventLog.Line("STEP",
                $"SCP-106 Hunter's Atlas native attempt actor={actor.Label} expected={expected} allowed={finalAllowed} used={usedSeen}");
            completed = true;
        }
        finally
        {
            Scp106Events.UsingHunterAtlas -= OnUsing;
            Scp106Events.UsedHunterAtlas -= OnUsed;

            // Failure path: never leave the 8s position allowance armed — a coincidental later
            // teleport of this actor would be silently consumed by the leaked authorization.
            if (!completed && expected == AbilityExpectedOutcome.Succeeded)
            {
                ctx.Registry.RevokeFeatureMoves(actor.Hub, "Hunter's Atlas attempt failed before confirmation");
            }
        }
    }
}
