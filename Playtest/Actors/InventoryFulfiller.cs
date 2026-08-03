using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using InventorySystem.Items;
using InventorySystem.Searching;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.Handlers;
using LabApi.Features.Wrappers;
using MEC;
using PlayerRoles.FirstPersonControl;
using PlaytestHarness.Core;
using PlaytestHarness.Telemetry;
using UnityEngine;
using BaseThrowableItem = InventorySystem.Items.ThrowableProjectiles.ThrowableItem;
using ThrowableNetworkHandler = InventorySystem.Items.ThrowableProjectiles.ThrowableNetworkHandler;

namespace PlaytestHarness.Actors;

/// <summary>Native inventory verbs shared by plugin-local scenarios.</summary>
internal static class InventoryFulfiller
{
    internal static IEnumerator<float> PickUp(ScenarioContext ctx, Actor actor, WorldItem worldItem)
    {
        if (worldItem == null)
        {
            throw new System.ArgumentNullException(nameof(worldItem));
        }

        Pickup? pickup = worldItem.Resolve();
        if (pickup == null)
        {
            throw new RequireException(
                $"PickUp: exact serial {worldItem.Serial} ({worldItem.Type}) no longer exists in the world");
        }

        foreach (float wait in Coroutines.Pump(CombatFulfiller.LookAt(ctx, actor, pickup.Position)))
        {
            yield return wait;
        }

        SearchCoordinator coordinator = actor.Hub.searchCoordinator;

        // Acquire the native search lock FIRST — the same Locked/InUse gate + InUse=true write the
        // SearchSessionPipe runs for a real client request (public ItemPickupBase.ServerValidateRequest).
        // This makes contention native: a second searcher (player or actor) is now rejected by the
        // game, not silently raced. The full byte-path pipe (SearchSessionPipe.ReceiveRequest) is NOT
        // drivable for dummies — its server-delay clamp reads LiteNetLib4MirrorServer.Peers[connId].Ping,
        // which has no entry for a DummyNetworkConnection and throws (decompiled
        // SearchCoordinator.ReceiveRequestUnsafe) — so the harness drives the same lock + completor
        // lifecycle the pipe would.
        if (!pickup.Base.ServerValidateRequest(actor.Hub.connectionToClient, coordinator.SessionPipe))
        {
            throw new RequireException(
                $"PickUp: native search request rejected serial {worldItem.Serial} for {actor.Label} — " +
                $"pickup is locked or already in use (competing search in progress?)");
        }

        // The lock is held from here: every failure path must release it via the native abort
        // (ServerHandleAbort clears InUse), or the pickup stays unsearchable for everyone.
        bool lockReleased = false;
        try
        {
            ISearchCompletor completor = pickup.Base.GetSearchCompletor(coordinator, coordinator.ServerMaxRayDistanceSqr);
            if (completor == null || !completor.ValidateStart())
            {
                throw new RequireException(
                    $"PickUp: native search start rejected serial {worldItem.Serial} for actor {actor.Label} (distance/inventory/interaction gate)");
            }

            float searchSeconds = pickup.Base.SearchTimeForPlayer(actor.Hub);
            ctx.Emit(new TelemetryEvent("item_pickup_start", null, new Dictionary<string, object?>
            {
                ["actor"] = actor.Label,
                ["serial"] = worldItem.Serial,
                ["type"] = worldItem.Type.ToString(),
                ["search_s"] = searchSeconds,
            }));
            yield return Timing.WaitForSeconds(searchSeconds);
            if (!completor.ValidateUpdate())
            {
                throw new RequireException(
                    $"PickUp: native search update rejected serial {worldItem.Serial} for actor {actor.Label}");
            }

            // Complete() owns the lock's endgame natively: success destroys the pickup; a plugin
            // cancelling PickingUpItem clears InUse itself (decompiled ItemSearchCompletor.Complete).
            lockReleased = true;
            completor.Complete();
            double deadline = ctx.ElapsedSeconds + 3f;
            while (!actor.Hub.inventory.UserInventory.Items.ContainsKey(worldItem.Serial))
            {
                if (ctx.ElapsedSeconds >= deadline)
                {
                    throw new RequireException(
                        $"PickUp: exact serial {worldItem.Serial} did not enter {actor.Label}'s inventory within 3s " +
                        "(a plugin may have cancelled PickingUpItem)");
                }

                yield return Timing.WaitForOneFrame;
            }

            EventLog.Line("STEP",
                $"native pickup actor={actor.Label} type={worldItem.Type} serial={worldItem.Serial} search={searchSeconds:0.###}s");
        }
        finally
        {
            if (!lockReleased && pickup.Base != null)
            {
                try
                {
                    pickup.Base.ServerHandleAbort(actor.Hub);
                }
                catch (System.Exception e)
                {
                    EventLog.Line("STEP",
                        $"PickUp: native search-abort failed for serial {worldItem.Serial}: {e.Message} — pickup may stay InUse",
                        error: true);
                }
            }
        }
    }

    internal static IEnumerator<float> ThrowHeldItemAt(
        ScenarioContext ctx,
        Actor actor,
        Vector3 target,
        bool fullForce) => ThrowHeldItemAt(ctx, actor, () => target, fullForce);

    internal static IEnumerator<float> ThrowHeldItemAt(
        ScenarioContext ctx,
        Actor actor,
        Actor target,
        bool fullForce) => ThrowHeldItemAt(ctx, actor, () => CombatFulfiller.AimPoint(target.Hub), fullForce);

    private static IEnumerator<float> ThrowHeldItemAt(
        ScenarioContext ctx,
        Actor actor,
        System.Func<Vector3> targetProvider,
        bool fullForce)
    {
        if (actor.Hub.inventory.CurInstance is not BaseThrowableItem throwable)
        {
            ItemBase? held = actor.Hub.inventory.CurInstance;
            throw new RequireException(
                $"ThrowHeldItemAt: actor {actor.Label} is holding {(held == null ? "nothing" : held.ItemTypeId + " (" + held.GetType().Name + ")")}, not a native throwable");
        }

        ushort serial = throwable.ItemSerial;
        ItemType type = throwable.ItemTypeId;
        bool threwEventSeen = false;
        bool observedFullForce = false;
        void OnThrew(PlayerThrewProjectileEventArgs ev)
        {
            ushort eventSerial = ev.Projectile?.Serial ?? ev.ThrowableItem?.Serial ?? 0;
            if (ev.Player?.ReferenceHub == actor.Hub && eventSerial == serial)
            {
                threwEventSeen = true;
                observedFullForce = ev.FullForce;
            }
        }

        PlayerEvents.ThrewProjectile += OnThrew;
        try
        {
            Vector3 finalTarget = targetProvider();
            foreach (float wait in Coroutines.Pump(CombatFulfiller.LookAt(ctx, actor, finalTarget)))
            {
                yield return wait;
            }

            double armStartedAt = ctx.ElapsedSeconds;
            ProcessThrowableRequest(actor, new ThrowableNetworkHandler.ThrowableItemRequestMessage(
                throwable,
                ThrowableNetworkHandler.RequestType.BeginThrow));
            float animationSeconds = Mathf.Max(0.05f, throwable.ThrowingAnimTime);
            yield return Timing.WaitForSeconds(animationSeconds);
            double armElapsed = ctx.ElapsedSeconds - armStartedAt;
            if (armElapsed < animationSeconds * 0.9f)
            {
                throw new RequireException(
                    $"ThrowHeldItemAt: {type} arm animation completed in {armElapsed:0.##}s but native ThrowingAnimTime is {animationSeconds:0.##}s");
            }

            float releaseDelay = Mathf.Max(0f,
                (fullForce ? throwable.FullThrowSettings : throwable.WeakThrowSettings).TriggerTime);
            if (releaseDelay > 0f)
            {
                yield return Timing.WaitForSeconds(releaseDelay);
            }

            finalTarget = targetProvider();
            foreach (float wait in Coroutines.Pump(CombatFulfiller.LookAt(ctx, actor, finalTarget)))
            {
                yield return wait;
            }

            if (actor.Hub.inventory.CurInstance is not BaseThrowableItem currentThrowable ||
                currentThrowable.ItemSerial != serial ||
                !ReferenceEquals(currentThrowable, throwable))
            {
                throw new RequireException(
                    $"ThrowHeldItemAt: exact throwable serial {serial} is no longer the selected item at confirmation time");
            }

            Vector3 startVelocity = Vector3.zero;
            if (actor.Hub.roleManager.CurrentRole is IFpcRole fpc)
            {
                startVelocity = fpc.FpcModule.Motor.MoveDirection;
                startVelocity.y = Mathf.Max(startVelocity.y, 0f);
                if (startVelocity.y > 1f)
                {
                    startVelocity.y = fpc.FpcModule.JumpSpeed;
                }

                startVelocity = ThrowableNetworkHandler.GetLimitedVelocity(startVelocity);
            }

            ctx.TrackWorldItem(new WorldItem(serial, type), $"pending native throw by {actor.Label}");
            ProcessThrowableRequest(actor, new ThrowableNetworkHandler.ThrowableItemRequestMessage(
                currentThrowable,
                fullForce
                    ? ThrowableNetworkHandler.RequestType.ConfirmThrowFullForce
                    : ThrowableNetworkHandler.RequestType.ConfirmThrowWeak,
                startVelocity));

            double deadline = ctx.ElapsedSeconds + 2f;
            while (!threwEventSeen && ctx.ElapsedSeconds < deadline)
            {
                yield return Timing.WaitForOneFrame;
            }

            if (!threwEventSeen)
            {
                throw new RequireException(
                    $"ThrowHeldItemAt: native ThrewProjectile event did not confirm exact serial {serial} ({type}) for {actor.Label}");
            }

            if (observedFullForce != fullForce)
            {
                throw new RequireException(
                    $"ThrowHeldItemAt: requested fullForce={fullForce} but native event reported {observedFullForce}");
            }

            ctx.Emit(new TelemetryEvent("item_throw", null, new Dictionary<string, object?>
            {
                ["actor"] = actor.Label,
                ["serial"] = serial,
                ["type"] = type.ToString(),
                ["full_force"] = fullForce,
                ["arm_s"] = System.Math.Round(armElapsed, 3),
                ["release_delay_s"] = releaseDelay,
                ["target"] = new[] { finalTarget.x, finalTarget.y, finalTarget.z },
            }));
            EventLog.Line("STEP",
                $"native throw actor={actor.Label} type={type} serial={serial} fullForce={fullForce} arm={armElapsed:0.###}s releaseDelay={releaseDelay:0.###}s target={Probes.PlacementProbes.Format(finalTarget)}");
        }
        finally
        {
            PlayerEvents.ThrewProjectile -= OnThrew;
        }
    }

    private static void ProcessThrowableRequest(
        Actor actor,
        ThrowableNetworkHandler.ThrowableItemRequestMessage request)
    {
        MethodInfo handler = typeof(ThrowableNetworkHandler).GetMethod(
            "ServerProcessRequest",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(ThrowableNetworkHandler).FullName, "ServerProcessRequest");
        if (actor.Hub.connectionToClient == null)
        {
            throw new RequireException($"native throwable request has no server connection for {actor.Label}");
        }

        try
        {
            handler.Invoke(null, new object[] { actor.Hub.connectionToClient, request });
        }
        catch (TargetInvocationException exception) when (exception.InnerException != null)
        {
            throw exception.InnerException;
        }
    }

    internal static IEnumerator<float> DropHeldItem(ScenarioContext ctx, Actor actor)
    {
        ItemBase? held = actor.Hub.inventory.CurInstance;
        if (held == null)
        {
            throw new RequireException($"DropHeldItem: actor {actor.Label} has no selected item");
        }

        ushort serial = held.ItemSerial;
        ItemType type = held.ItemTypeId;
        string action = $"{type} (#{serial}):Drop";
        if (!CombatFulfiller.TryInvokeDummyAction(actor.Hub, action))
        {
            throw new RequireException(
                $"DropHeldItem: native dummy action '{action}' unavailable for actor {actor.Label}");
        }

        double deadline = ctx.ElapsedSeconds + 3f;
        Pickup? pickup = null;
        while (pickup == null)
        {
            pickup = Pickup.List.FirstOrDefault(candidate =>
                candidate != null && !candidate.IsDestroyed && candidate.Serial == serial);
            if (pickup != null)
            {
                break;
            }

            if (ctx.ElapsedSeconds >= deadline)
            {
                throw new RequireException(
                    $"DropHeldItem: exact serial {serial} ({type}) did not appear as a world pickup within 3s");
            }

            yield return Timing.WaitForOneFrame;
        }

        actor.LastDroppedItem = new WorldItem(serial, type);
        ctx.TrackWorldItem(actor.LastDroppedItem, $"native drop by {actor.Label}");
        ctx.Emit(new TelemetryEvent("item_drop", null, new Dictionary<string, object?>
        {
            ["actor"] = actor.Label,
            ["serial"] = serial,
            ["type"] = type.ToString(),
            ["pos"] = new[] { pickup.Position.x, pickup.Position.y, pickup.Position.z },
        }));
        EventLog.Line("STEP",
            $"native drop actor={actor.Label} type={type} serial={serial} pos={Probes.PlacementProbes.Format(pickup.Position)}");
    }
}
