using System;
using UnityEngine;

namespace PlaytestHarness.Actors;

/// <summary>
/// A world-space damage target for native firearm or SCP-melee input. Native IDestructible targets
/// are confirmed from hit-registration records; feature-owned devices provide an exact interaction
/// volume and an observer wired to their real attacker-attributed damage handler.
/// </summary>
public sealed class WorldDamageTarget
{
    private WorldDamageTarget(
        string label,
        IDestructible? destructible,
        WorldInteractionBox? hitVolume,
        Vector3? preferredFirearmPosition,
        Action<int, Action<float>>? subscribeDamage,
        Action<Action<float>>? unsubscribeDamage)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            throw new ArgumentException("World damage target label is required.", nameof(label));
        }

        Label = label;
        Destructible = destructible;
        HitVolume = hitVolume;
        PreferredFirearmPosition = preferredFirearmPosition;
        SubscribeDamage = subscribeDamage;
        UnsubscribeDamage = unsubscribeDamage;
    }

    public string Label { get; }

    /// <summary>
    /// A breakable window as a native hit-registration target, via its LabAPI wrapper. Scenarios
    /// hand over wrapped world objects; the harness resolves the underlying IDestructible itself
    /// (batteries-included: no raw game-object/component walking in scenario code).
    /// </summary>
    public static WorldDamageTarget FromWindow(string label, LabApi.Features.Wrappers.Window window)
    {
        if (window == null)
        {
            throw new ArgumentNullException(nameof(window));
        }

        return FromDestructible(label, window.Base);
    }

    /// <summary>
    /// Raw-interface seam for harness fulfillers and future wrapped-handle factories. Kept internal:
    /// sourcing an IDestructible requires component walks on raw game objects, which the
    /// batteries-included contract keeps out of scenarios.
    /// </summary>
    internal static WorldDamageTarget FromDestructible(string label, IDestructible target)
    {
        if (target == null)
        {
            throw new ArgumentNullException(nameof(target));
        }

        return new WorldDamageTarget(label, target, null, null, null, null);
    }

    public static WorldDamageTarget FromFeature(
        string label,
        WorldInteractionBox hitVolume,
        Action<int, Action<float>> subscribeDamage,
        Action<Action<float>> unsubscribeDamage,
        Vector3? preferredFirearmPosition = null)
    {
        if (hitVolume == null)
        {
            throw new ArgumentNullException(nameof(hitVolume));
        }

        if (subscribeDamage == null)
        {
            throw new ArgumentNullException(nameof(subscribeDamage));
        }

        if (unsubscribeDamage == null)
        {
            throw new ArgumentNullException(nameof(unsubscribeDamage));
        }

        return new WorldDamageTarget(
            label,
            null,
            hitVolume,
            preferredFirearmPosition,
            subscribeDamage,
            unsubscribeDamage);
    }

    internal IDestructible? Destructible { get; }

    internal WorldInteractionBox? HitVolume { get; }

    internal Vector3? PreferredFirearmPosition { get; }

    private Action<int, Action<float>>? SubscribeDamage { get; }

    private Action<Action<float>>? UnsubscribeDamage { get; }

    internal Vector3 AimPoint => Destructible?.CenterOfMass ?? HitVolume!.Center;

    internal bool UsesDamageEvents => SubscribeDamage != null;

    internal void BeginDamageObservation(int attackerPlayerId, Action<float> observer) =>
        SubscribeDamage?.Invoke(attackerPlayerId, observer);

    internal void EndDamageObservation(Action<float> observer) => UnsubscribeDamage?.Invoke(observer);
}
