using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BehaviorTestHarness.Harness;
using LabApi.Features.Wrappers;
using PlayerRoles;
using UnityEngine;

namespace BehaviorTestHarness.Scenarios;

/// <summary>Live structural regression for WarmupScpSelector's continuous full-room Aim layout.</summary>
public sealed class WarmupAimFullRoomScenario : IBehaviorScenario
{
    public string Name => "warmup-aim-full-room";

    public string Description => "Checks the full-width room seam, combined activity bounds, floor, and six persistent counter guns.";

    public IReadOnlyCollection<string> Aliases => ["warmup-aim"];

    public BehaviorTestResult Run(BehaviorScenarioContext context)
    {
        Type productType = AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType("WarmupScpSelector.WarmupScpSelectorPlugin", false))
            .FirstOrDefault(type => type != null)
            ?? throw new BehaviorAssertionException("WarmupScpSelector assembly is not loaded");
        object product = Property(productType, null, "Instance")
            ?? throw new BehaviorAssertionException("WarmupScpSelector.Instance is null");
        object controller = Field(product, "_controller");
        object room = Field(controller, "_room");
        object lane = Field(controller, "_aimLane");
        object world = Field(lane, "_world");
        object shelves = Field(lane, "_shelves");
        object state = Field(shelves, "_state");

        context.Require(Value<bool>(room, "IsSpawned"), "selector room is not spawned");
        context.Require(Field<bool>(lane, "_running"), "Aim lane is not running");
        context.Require(Value<bool>(world, "IsSpawned"), "Aim world is not spawned");
        context.Require(NullableField(room, "_aimDoorGate") == null, "temporary full-width setup gate is still present");

        object layout = Value<object>(world, "Layout");
        Vector3 origin = Value<Vector3>(room, "Origin");
        float roomWidth = Value<float>(room, "Width");
        float roomDepth = Value<float>(room, "Depth");
        float shellWidth = Value<float>(layout, "ShellWidth");
        float counterWidth = Value<float>(layout, "ShootingCounterWidth");
        float seamZ = Value<float>(layout, "DoorPlaneZ");
        Bounds bounds = Value<Bounds>(layout, "VerifiedBounds");
        context.Require(Math.Abs(shellWidth - roomWidth) <= 0.01f,
            $"training shell is narrower than gallery shell={shellWidth:0.###} gallery={roomWidth:0.###}");
        context.Require(Math.Abs(counterWidth - (shellWidth - 0.6f)) <= 0.01f,
            $"shooting counter does not span the room counter={counterWidth:0.###} shell={shellWidth:0.###}");
        context.Require(bounds.Contains(origin + new Vector3(0f, 0.5f, 4f)) &&
                        bounds.Contains(origin + new Vector3(roomWidth / 2f - 0.5f, 0.5f, -4f)) &&
                        bounds.Contains(origin + new Vector3(0f, 0.5f, -26.4f)),
            $"activity bounds do not cover full combined room center={Format(bounds.center)} size={Format(bounds.size)}");
        context.Require(Math.Abs(bounds.size.z - (roomDepth + 22f - 0.4f)) <= 0.02f,
            $"combined activity depth is wrong boundsZ={bounds.size.z:0.###} galleryDepth={roomDepth:0.###}");
        bool selectorUi = (bool)(layout.GetType().GetMethod("ContainsAimUi")?.Invoke(layout, [origin + new Vector3(0f, 0.5f, seamZ - origin.z + 0.5f)]) ?? true);
        bool aimUi = (bool)(layout.GetType().GetMethod("ContainsAimUi")?.Invoke(layout, [origin + new Vector3(0f, 0.5f, seamZ - origin.z - 0.5f)]) ?? false);
        context.Require(!selectorUi && aimUi,
            $"UI-only seam routing is wrong selectorSide={selectorUi} aimSide={aimUi}");

        foreach (float x in new[] { -shellWidth / 2f + 0.5f, -8f, 0f, 8f, shellWidth / 2f - 0.5f })
        {
            AssertFloor(origin.x + x, origin.y, seamZ + 0.05f, context);
            AssertFloor(origin.x + x, origin.y, seamZ - 0.05f, context);
        }

        foreach (float x in new[] { -shellWidth / 2f + 0.8f, 0f, shellWidth / 2f - 0.8f })
        {
            Vector3 rayOrigin = new(origin.x + x, origin.y + 1.8f, seamZ + 0.8f);
            context.Require(Physics.Raycast(rayOrigin, Vector3.back, out RaycastHit hit, 28f) && hit.distance >= 2f,
                $"former seam is obstructed within 2m x={x:0.##} hit={hit.collider?.name ?? "miss"} distance={hit.distance:0.###}");
            if (Math.Abs(x) > shellWidth / 4f)
            {
                context.Require(Math.Abs(hit.distance - 22.5f) <= 0.25f,
                    $"wide room edge does not continue to the backstop x={x:0.##} hit={hit.collider?.name ?? "miss"} distance={hit.distance:0.###}");
            }

            Vector3 counterRay = new(origin.x + x, origin.y + 0.55f, seamZ - 5f);
            context.Require(Physics.Raycast(counterRay, Vector3.back, out RaycastHit counterHit, 3f) && Math.Abs(counterHit.distance - 1.675f) <= 0.16f,
                $"full-width counter missing x={x:0.##} hit={counterHit.collider?.name ?? "miss"} distance={counterHit.distance:0.###}");
        }

        List<LightSourceToy> counterLights = Items(Field(world, "_toys")).OfType<LightSourceToy>().ToList();
        context.Require(counterLights.Count == 3 && counterLights.All(light => light.Intensity >= 23.5f && light.Range >= 15.5f),
            $"expected three very bright non-HDR counter lights actual={string.Join(";", counterLights.Select(light => $"{light.Intensity:0.##}/{light.Range:0.##}"))}");
        List<LightSourceToy> selectorLights = Items(Field(room, "_toys")).OfType<LightSourceToy>().ToList();
        context.Require(selectorLights.Count == 3 && selectorLights.All(light => light.Intensity >= 23.5f && light.Range >= 17.5f),
            $"expected three very bright non-HDR selector lights actual={string.Join(";", selectorLights.Select(light => $"{light.Intensity:0.##}/{light.Range:0.##}"))}");

        List<object> slots = Items(Value<object>(state, "Slots")).ToList();
        context.Require(slots.Count == 6, $"expected six counter slots, found {slots.Count}");
        HashSet<ushort> serials = new();
        List<Vector3> positions = new();
        foreach (object slot in slots)
        {
            ushort serial = Value<ushort>(slot, "PickupSerial");
            context.Require(serial != 0 && serials.Add(serial), $"counter pickup serial is zero or duplicated serial={serial}");
            context.Require(Pickup.TryGet(serial, out Pickup? pickup) && pickup != null && !pickup.IsDestroyed,
                $"counter pickup wrapper missing serial={serial}");
            context.Require(pickup!.Rigidbody != null && pickup.Rigidbody.isKinematic,
                $"counter pickup is not frozen serial={serial}");
            positions.Add(pickup.Position);
        }

        context.Require(positions.All(position => position.y >= origin.y + 1.25f && position.y <= origin.y + 1.55f),
            $"one or more guns are not visibly above the counter positions={string.Join(";", positions.Select(Format))}");
        context.Require(positions.Max(position => position.z) - positions.Min(position => position.z) <= 0.05f,
            $"counter guns do not share one counter line positions={string.Join(";", positions.Select(Format))}");

        Player walker = context.SpawnDummy("warmup-full-room-walker");
        walker.SetRole(RoleTypeId.Tutorial, RoleChangeReason.RemoteAdmin, RoleSpawnFlags.All);
        walker.Position = origin + new Vector3(0f, 0.5f, 0f);
        context.Require(bounds.Contains(walker.Position), $"walker spawned outside full-room bounds position={Format(walker.Position)}");
        AssertFloor(walker.Position.x, origin.y, walker.Position.z, context, walker);

        context.Info($"warmup full room observed width={shellWidth:0.##} counterWidth={counterWidth:0.##} combinedDepth={bounds.size.z:0.##} counterPickups={serials.Count} counterLights={counterLights.Count} selectorLights={selectorLights.Count} seamRays=3 counterRays=3 floorRays=11");
        return context.Pass();
    }

    private static void AssertFloor(float x, float floorY, float z, BehaviorScenarioContext context, Player? ignoredPlayer = null)
    {
        Vector3 origin = new(x, floorY + 3f, z);
        RaycastHit hit = Physics.RaycastAll(origin, Vector3.down, 4f)
            .OrderBy(candidate => candidate.distance)
            .FirstOrDefault(candidate => ignoredPlayer?.ReferenceHub == null ||
                candidate.collider.GetComponentInParent<ReferenceHub>() != ignoredPlayer.ReferenceHub);
        context.Require(hit.collider != null &&
                        Math.Abs(hit.point.y - floorY) <= 0.12f && Math.Abs(hit.distance - 3f) <= 0.16f,
            $"floor ray failed origin={Format(origin)} hit={hit.collider?.name ?? "miss"} point={Format(hit.point)} distance={hit.distance:0.###}");
    }

    private static object Field(object target, string name)
    {
        Type? type = target.GetType();
        while (type != null)
        {
            FieldInfo? field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null) return field.GetValue(target) ?? throw new BehaviorAssertionException($"field {name} is null");
            type = type.BaseType;
        }

        throw new BehaviorAssertionException($"field {name} was not found on {target.GetType().FullName}");
    }

    private static T Field<T>(object target, string name)
    {
        object value = Field(target, name);
        return value is T typed ? typed : throw new BehaviorAssertionException($"field {name} is not {typeof(T).Name}");
    }

    private static object? NullableField(object target, string name)
    {
        Type? type = target.GetType();
        while (type != null)
        {
            FieldInfo? field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null) return field.GetValue(target);
            type = type.BaseType;
        }

        throw new BehaviorAssertionException($"field {name} was not found on {target.GetType().FullName}");
    }

    private static object? Property(Type type, object? target, string name) =>
        type.GetProperty(name, BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(target);

    private static T Value<T>(object target, string name)
    {
        object? value = target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(target);
        return value is T typed ? typed : throw new BehaviorAssertionException($"property {name} is missing, null, or not {typeof(T).Name}");
    }

    private static IEnumerable<object> Items(object value)
    {
        if (value is not IEnumerable enumerable) yield break;
        foreach (object? item in enumerable)
        {
            if (item != null) yield return item;
        }
    }

    private static string Format(Vector3 value) => $"{value.x:0.##},{value.y:0.##},{value.z:0.##}";
}
