using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using PlayerRoles;
using PlaytestHarness.Telemetry;
using UnityEngine;

namespace PlaytestHarness.Movement;

/// <summary>
/// IMovementProvider bound to the spike bot plugin's <c>SCPSLBot.AI.BotOrders</c> facade VIA
/// REFLECTION — no compile-time reference, so the planned bot rewrite only has to ship a new
/// provider (plan §"Movement provider"). Availability is probed at startup; a missing SCPSLBot.dll
/// simply reports Available=false and walk-requiring scenarios SKIP.
///
/// Bound surface (spike branch spike/bot-orders, SCPSLBot\AI\BotOrders.cs):
///   static ReferenceHub SpawnBot(string nickname, RoleTypeId role)
///   static bool MoveTo(ReferenceHub, Vector3) / MoveToRoom(ReferenceHub, RoomName)
///   static bool Stop(ReferenceHub) / TryGetStatus(ReferenceHub, out BotOrderStatus) / DespawnBot(ReferenceHub)
/// BotOrderStatus properties read reflectively: CurrentOrder (enum: None/MoveTo/MoveToRoom/Completed/
/// Stopped/FailedOffMesh/FailedNoPath), IsActive, FailureReason, DistanceRemaining, StallCount,
/// SecondsSinceProgress, TeleportDetected, GroundProbeMisses.
///
/// Drift policy: methods are bound by exact parameter signature (an overload or rename means
/// UNAVAILABLE, never a wrong-overload bind or AmbiguousMatchException); a missing status property
/// logs a warn-once error line, because a silently-defaulted property would disable a provider
/// oracle (stall FAIL, PT-012 teleport verdict) with no trace — the one failure mode the harness
/// contract forbids.
/// </summary>
public sealed class BotOrdersProvider : IMovementProvider
{
    /// <summary>
    /// A bot with no waypoint progress for this long is surfaced as Stalled (loud FAIL). The spike
    /// replans + logs its own STALL every 5s and usually recovers; cumulative stall COUNT is not a
    /// failure signal (a run that recovered 5 times is still making progress), sustained
    /// no-progress time is.
    /// </summary>
    private const float StallSecondsLimit = 15f;

    private MethodInfo? _spawnBot;
    private MethodInfo? _moveTo;
    private MethodInfo? _moveToRoom;
    private MethodInfo? _stop;
    private MethodInfo? _tryGetStatus;
    private MethodInfo? _despawnBot;
    private PropertyInfo? _botManagerInstance;
    private PropertyInfo? _botPlayers;
    private bool _bindAttempted;

    /// <summary>Warn-once registry for status properties missing from the bound BotOrderStatus.</summary>
    private readonly HashSet<string> _reportedMissingProperties = new(StringComparer.Ordinal);

    /// <summary>
    /// Reference-equality set: destroyed ReferenceHubs throw from GetHashCode (it reads the dead
    /// gameObject), so the default comparer would poison Remove/Contains for dead hubs.
    /// </summary>
    private readonly HashSet<ReferenceHub> _controlled = new(ReferenceComparer.Instance);

    private sealed class ReferenceComparer : IEqualityComparer<ReferenceHub>
    {
        public static readonly ReferenceComparer Instance = new();

        public bool Equals(ReferenceHub? x, ReferenceHub? y) => ReferenceEquals(x, y);

        public int GetHashCode(ReferenceHub obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }

    /// <summary>
    /// Lazy bind: plugin load order is not guaranteed, so the reflection probe runs on first use
    /// (by which time all plugin assemblies are loaded), not at PlaytestHarness.Enable.
    /// </summary>
    private void EnsureBound()
    {
        if (_bindAttempted)
        {
            return;
        }

        _bindAttempted = true;
        Type? ordersType = AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly =>
            {
                try
                {
                    return assembly.GetType("SCPSLBot.AI.BotOrders", throwOnError: false);
                }
                catch
                {
                    return null;
                }
            })
            .FirstOrDefault(t => t != null);

        if (ordersType == null)
        {
            EventLog.Line("MOVEMENT", "BotOrders not found in loaded assemblies — movement provider UNAVAILABLE (walk scenarios will SKIP).");
            return;
        }

        // Exact-signature binds into locals first (all-or-nothing: a partial bind must never leave
        // Available==true with a null method). Signature filtering also means a future overload or
        // renamed parameter type reports "surface mismatch — UNAVAILABLE" instead of throwing
        // AmbiguousMatchException out of the Available getter or binding a wrong-signature method
        // that only fails at Invoke time.
        MethodInfo? spawnBot = BindStatic(ordersType, "SpawnBot", typeof(string), typeof(RoleTypeId));
        MethodInfo? moveTo = BindStatic(ordersType, "MoveTo", typeof(ReferenceHub), typeof(Vector3));
        MethodInfo? moveToRoom = BindStatic(ordersType, "MoveToRoom", typeof(ReferenceHub), typeof(MapGeneration.RoomName));
        MethodInfo? stop = BindStatic(ordersType, "Stop", typeof(ReferenceHub));
        MethodInfo? tryGetStatus = BindTryGetStatus(ordersType);
        MethodInfo? despawnBot = BindStatic(ordersType, "DespawnBot", typeof(ReferenceHub));

        if (spawnBot == null || moveTo == null || moveToRoom == null || stop == null || tryGetStatus == null)
        {
            EventLog.Line("MOVEMENT",
                $"BotOrders found but surface mismatch (SpawnBot={spawnBot != null} MoveTo={moveTo != null} MoveToRoom={moveToRoom != null} Stop={stop != null} TryGetStatus={tryGetStatus != null}) — provider UNAVAILABLE.",
                error: true);
            return;
        }

        _spawnBot = spawnBot;
        _moveTo = moveTo;
        _moveToRoom = moveToRoom;
        _stop = stop;
        _tryGetStatus = tryGetStatus;
        _despawnBot = despawnBot;
        Type? managerType = ordersType.Assembly.GetType("SCPSLBot.AI.BotManager", throwOnError: false);
        // Bind the Instance PROPERTY, not its value: a round-scoped manager recreated on restart
        // would leave a cached instance permanently stale (spurious "local input unavailable").
        _botManagerInstance = managerType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        _botPlayers = managerType?.GetProperty("BotPlayers", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        EventLog.Line("MOVEMENT", $"movement provider bound: {Name} (reflection, assembly {ordersType.Assembly.GetName().Name})");
    }

    /// <summary>Exact static-method bind; overloads/renames yield null, never AmbiguousMatchException.</summary>
    private static MethodInfo? BindStatic(Type type, string name, params Type[] parameterTypes)
    {
        try
        {
            return type.GetMethod(name, BindingFlags.Public | BindingFlags.Static, binder: null, parameterTypes, modifiers: null);
        }
        catch (Exception e)
        {
            EventLog.Line("MOVEMENT", $"binding BotOrders.{name} threw {e.GetType().Name}: {e.Message}", error: true);
            return null;
        }
    }

    /// <summary>
    /// TryGetStatus(ReferenceHub, out BotOrderStatus): the status type is plugin-internal, so the
    /// out parameter is matched by shape (by-ref, non-generic) instead of an exact type.
    /// </summary>
    private static MethodInfo? BindTryGetStatus(Type type)
    {
        try
        {
            List<MethodInfo> candidates = type
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(method =>
                {
                    if (method.Name != "TryGetStatus" || method.ReturnType != typeof(bool))
                    {
                        return false;
                    }

                    ParameterInfo[] parameters = method.GetParameters();
                    return parameters.Length == 2
                        && parameters[0].ParameterType == typeof(ReferenceHub)
                        && parameters[1].IsOut;
                })
                .ToList();
            if (candidates.Count != 1)
            {
                if (candidates.Count > 1)
                {
                    EventLog.Line("MOVEMENT",
                        $"BotOrders.TryGetStatus has {candidates.Count} matching overloads — refusing an arbitrary pick.",
                        error: true);
                }

                return null;
            }

            return candidates[0];
        }
        catch (Exception e)
        {
            EventLog.Line("MOVEMENT", $"binding BotOrders.TryGetStatus threw {e.GetType().Name}: {e.Message}", error: true);
            return null;
        }
    }

    public bool Available
    {
        get
        {
            EnsureBound();
            return _spawnBot != null;
        }
    }

    public string Name => "BotOrders(spike)";

    public bool TrySpawnActor(string nickname, RoleTypeId role, out ReferenceHub? hub)
    {
        hub = null;
        if (!Available)
        {
            return false;
        }

        try
        {
            hub = _spawnBot!.Invoke(null, new object[] { nickname, role }) as ReferenceHub;
        }
        catch (Exception e)
        {
            EventLog.Line("MOVEMENT", $"BotOrders.SpawnBot threw {e.GetType().Name}: {e.Message}", error: true);
            return false;
        }

        if (hub == null)
        {
            return false;
        }

        _controlled.Add(hub);
        return true;
    }

    public bool Controls(ReferenceHub hub) => hub != null && _controlled.Contains(hub);

    public bool OrderWalk(ReferenceHub hub, Vector3 target)
    {
        if (!Available || !Controls(hub))
        {
            return false;
        }

        try
        {
            return (bool)_moveTo!.Invoke(null, new object[] { hub, target });
        }
        catch (Exception e)
        {
            EventLog.Line("MOVEMENT", $"BotOrders.MoveTo threw {e.GetType().Name}: {e.Message}", error: true);
            return false;
        }
    }

    public bool DriveLocal(ReferenceHub hub, Vector3 worldDirection)
    {
        if (!Available || !Controls(hub) || _botManagerInstance == null || _botPlayers == null)
        {
            return false;
        }

        try
        {
            // Re-read Instance per call: a bot plugin that recreates its manager per round must not
            // leave this provider driving (or failing against) a stale manager forever.
            object? botManager = _botManagerInstance.GetValue(null);
            if (botManager == null)
            {
                return false;
            }

            if (_botPlayers.GetValue(botManager) is not IDictionary players || !players.Contains(hub))
            {
                return false;
            }

            object? botHub = players[hub];
            object? fpcPlayer = botHub?.GetType().GetProperty(
                "CurrentBotPlayer", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(botHub);
            if (fpcPlayer == null)
            {
                return false;
            }

            object? move = fpcPlayer.GetType().GetProperty(
                "Move", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(fpcPlayer);
            PropertyInfo? desired = move?.GetType().GetProperty(
                "DesiredLocalDirection", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (move == null || desired?.GetSetMethod(nonPublic: true) == null)
            {
                return false;
            }

            Vector3 horizontal = Vector3.ProjectOnPlane(worldDirection, Vector3.up);
            Vector3 local = horizontal.sqrMagnitude > 1e-6f
                ? hub.transform.InverseTransformDirection(horizontal.normalized)
                : Vector3.zero;
            desired.SetValue(move, local);
            return true;
        }
        catch (Exception e)
        {
            EventLog.Line("MOVEMENT", $"BotOrders local-drive reflection threw {e.GetType().Name}: {e.Message}", error: true);
            return false;
        }
    }

    public void StopLocal(ReferenceHub hub)
    {
        if (hub != null)
        {
            DriveLocal(hub, Vector3.zero);
        }
    }

    public bool OrderWalkToRoom(ReferenceHub hub, MapGeneration.RoomName room)
    {
        if (!Available || !Controls(hub))
        {
            return false;
        }

        try
        {
            return (bool)_moveToRoom!.Invoke(null, new object[] { hub, room });
        }
        catch (Exception e)
        {
            EventLog.Line("MOVEMENT", $"BotOrders.MoveToRoom threw {e.GetType().Name}: {e.Message}", error: true);
            return false;
        }
    }

    public MovementStatus Status(ReferenceHub hub)
    {
        Vector3 pos = hub != null && hub.gameObject != null ? hub.transform.position : Vector3.zero;
        if (!Available || hub == null)
        {
            return new MovementStatus(MovementState.None, "provider unavailable or hub null", pos, float.NaN);
        }

        object?[] args = { hub, null };
        bool found;
        try
        {
            found = (bool)_tryGetStatus!.Invoke(null, args);
        }
        catch (Exception e)
        {
            return new MovementStatus(MovementState.None, $"TryGetStatus threw {e.GetType().Name}: {e.Message}", pos, float.NaN);
        }

        if (!found || args[1] == null)
        {
            return new MovementStatus(MovementState.None, "no order state for this hub", pos, float.NaN);
        }

        object status = args[1]!;
        Type statusType = status.GetType();
        string kind = Read<object>(statusType, status, "CurrentOrder")?.ToString() ?? "None";
        bool isActive = Read<bool>(statusType, status, "IsActive");
        string reason = Read<string>(statusType, status, "FailureReason") ?? string.Empty;
        float remaining = Read<float>(statusType, status, "DistanceRemaining");
        int stallCount = Read<int>(statusType, status, "StallCount");
        float sinceProgress = Read<float>(statusType, status, "SecondsSinceProgress");
        bool teleportDetected = Read<bool>(statusType, status, "TeleportDetected");
        int groundProbeMisses = Read<int>(statusType, status, "GroundProbeMisses");

        MovementState state = kind switch
        {
            "Completed" => MovementState.Arrived,
            "FailedOffMesh" => MovementState.OffMesh,
            "FailedNoPath" => MovementState.Stalled,
            "Stopped" => MovementState.None,
            "None" => MovementState.None,
            _ => isActive ? MovementState.Moving : MovementState.None,
        };

        // The spike can complete its order while declaring the spatial verdict FAIL. Preserve those
        // provider-owned oracles instead of mapping every Completed status to Arrived (PT-012).
        if (teleportDetected || groundProbeMisses > 0)
        {
            state = MovementState.OffMesh;
            reason = $"provider spatial verdict failed (teleportDetected={teleportDetected}, groundProbeMisses={groundProbeMisses})";
        }
        else if (state == MovementState.Moving && sinceProgress >= StallSecondsLimit)
        {
            state = MovementState.Stalled;
            reason = $"no waypoint progress for {sinceProgress:0.#}s (stall/replan cycles: {stallCount})";
        }

        if (state == MovementState.Stalled && string.IsNullOrEmpty(reason))
        {
            reason = kind == "FailedNoPath" ? "navigator found no connected path" : "stalled";
        }

        return new MovementStatus(state, reason, pos, remaining);
    }

    public void Cancel(ReferenceHub hub)
    {
        if (!Available || hub == null)
        {
            return;
        }

        try
        {
            _stop!.Invoke(null, new object[] { hub });
        }
        catch (Exception e)
        {
            EventLog.Line("MOVEMENT", $"BotOrders.Stop threw {e.GetType().Name}: {e.Message}", error: true);
        }
    }

    public void Release(ReferenceHub hub)
    {
        if (hub == null)
        {
            return;
        }

        // Reference-comparer Remove is safe for destroyed hubs.
        if (!_controlled.Remove(hub) || !Available)
        {
            return;
        }

        // Only call into the bot plugin while the hub is still alive: its BotPlayers dictionary
        // keys ReferenceHub with the default comparer, and a destroyed hub's GetHashCode throws.
        // For already-destroyed hubs the plugin's own ReferenceHub.OnPlayerRemoved hook has
        // dropped the bot state — nothing left to do.
        if (!hub || !hub.gameObject)
        {
            return;
        }

        try
        {
            _despawnBot?.Invoke(null, new object[] { hub });
        }
        catch (Exception e)
        {
            EventLog.Line("MOVEMENT", $"BotOrders.DespawnBot threw {e.GetType().Name}: {e.Message}", error: true);
        }
    }

    /// <summary>
    /// Status-property read. Still degrades to default (one drifted property must not take the whole
    /// provider down mid-run), but NEVER silently: a missing/mistyped/throwing property is a
    /// disabled oracle (stall FAIL, PT-012 teleport verdict), so it logs a warn-once error line.
    /// </summary>
    private T? Read<T>(Type type, object instance, string property)
    {
        PropertyInfo? info = type.GetProperty(property, BindingFlags.Public | BindingFlags.Instance);
        if (info == null)
        {
            WarnPropertyOnce(property, $"missing from {type.FullName}");
            return default;
        }

        try
        {
            object? value = info.GetValue(instance);
            if (value is T typed)
            {
                return typed;
            }

            // A null on an existing property is a legitimate value (e.g. FailureReason), not drift.
            if (value != null)
            {
                WarnPropertyOnce(property, $"is {value.GetType().Name}, expected {typeof(T).Name}");
            }

            return default;
        }
        catch (Exception e)
        {
            WarnPropertyOnce(property, $"getter threw {e.GetType().Name}: {e.Message}");
            return default;
        }
    }

    private void WarnPropertyOnce(string property, string detail)
    {
        if (_reportedMissingProperties.Add(property))
        {
            EventLog.Line("MOVEMENT",
                $"BotOrderStatus.{property} {detail} — this provider oracle is DEGRADED to its default for the session.",
                error: true);
        }
    }
}
