using System;
using System.Collections.Generic;
using System.Linq;
using InventorySystem;
using InventorySystem.Items.Firearms;
using InventorySystem.Items.Firearms.Modules;
using InventorySystem.Items.Firearms.Modules.Misc;
using MEC;
using PlayerRoles;
using PlaytestHarness.Core;
using PlaytestHarness.Probes;
using PlaytestHarness.Telemetry;
using UnityEngine;

namespace PlaytestHarness.Actors;

/// <summary>Native firearm and SCP-melee attacks against damageable world features.</summary>
internal static class WorldCombatFulfiller
{
    private const float MeleeRange = 2.2f;

    internal static IEnumerator<float> Attack(ScenarioContext ctx, Actor attacker, WorldDamageTarget target)
    {
        if (target == null)
        {
            throw new ArgumentNullException(nameof(target));
        }

        foreach (float wait in Coroutines.Pump(CombatFulfiller.IsScpMelee(attacker.Role)
            ? MeleeAttack(ctx, attacker, target)
            : FirearmAttack(ctx, attacker, target)))
        {
            yield return wait;
        }
    }

    private static IEnumerator<float> FirearmAttack(
        ScenarioContext ctx,
        Actor attacker,
        WorldDamageTarget target)
    {
        Inventory inventory = attacker.Hub.inventory;
        Firearm? firearm = inventory.CurInstance as Firearm
            ?? inventory.UserInventory.Items.Values.OfType<Firearm>().FirstOrDefault();
        if (firearm == null)
        {
            throw new RequireException(
                $"Attack(world): actor {attacker.Label} has no firearm (GiveItem one first)");
        }

        foreach (float wait in Coroutines.Pump(CombatFulfiller.Equip(ctx, attacker, firearm)))
        {
            yield return wait;
        }

        foreach (float wait in Coroutines.Pump(CombatFulfiller.EnsureFirearmReady(ctx, attacker, firearm)))
        {
            yield return wait;
        }

        if (target.HitVolume != null)
        {
            foreach (float wait in Coroutines.Pump(EstablishFirearmPosition(ctx, attacker, target)))
            {
                yield return wait;
            }
        }

        bool shotObserved = false;
        float destructibleDamage = 0f;
        float featureDamage = 0f;
        string confirmation = string.Empty;
        void OnFeatureDamaged(float amount)
        {
            if (amount > 0f)
            {
                featureDamage += amount;
            }
        }

        void OnFired(ReferenceHub owner, HitscanResult result)
        {
            if (owner != attacker.Hub)
            {
                return;
            }

            shotObserved = true;
            if (target.Destructible == null)
            {
                return;
            }

            foreach (DestructibleDamageRecord record in result.DamagedDestructibles)
            {
                if (!ReferenceEquals(record.Destructible, target.Destructible) ||
                    record.AppliedDamage <= 0f || record.Handler.Attacker.Hub != attacker.Hub)
                {
                    continue;
                }

                destructibleDamage += record.AppliedDamage;
                confirmation = $"HitscanResult({record.AppliedDamage:0.##})";
            }
        }

        HitscanHitregModuleBase.ServerAnyPlayerFired += OnFired;
        try
        {
            double deadline = ctx.ElapsedSeconds + ctx.Config.AttackTimeoutSeconds;
            int shots = 0;
            while (true)
            {
                if (ctx.ElapsedSeconds >= deadline)
                {
                    throw new RequireException(
                        $"Attack(world): {attacker.Label} fired {shots} shot(s) at {target.Label} without attributable damage within {ctx.Config.AttackTimeoutSeconds:0.#}s");
                }

                foreach (float wait in Coroutines.Pump(target.HitVolume != null
                    ? CombatFulfiller.AimAtBox(ctx, attacker, target.HitVolume)
                    : CombatFulfiller.LookAt(ctx, attacker, target.AimPoint)))
                {
                    yield return wait;
                }

                int ammoBefore = CombatFulfiller.FirearmAmmoUnits(firearm);
                shotObserved = false;
                destructibleDamage = 0f;
                featureDamage = 0f;
                confirmation = string.Empty;
                bool observingFeatureDamage = false;
                try
                {
                    if (target.UsesDamageEvents)
                    {
                        target.BeginDamageObservation(attacker.PlayerId, OnFeatureDamaged);
                        observingFeatureDamage = true;
                    }

                    foreach (float wait in Coroutines.Pump(CombatFulfiller.TriggerFirearm(attacker, firearm)))
                    {
                        yield return wait;
                    }

                    shots++;
                    yield return Timing.WaitForSeconds(0.5f);
                }
                finally
                {
                    if (observingFeatureDamage)
                    {
                        target.EndDamageObservation(OnFeatureDamaged);
                    }
                }

                int ammoAfter = CombatFulfiller.FirearmAmmoUnits(firearm);
                bool confirmed = target.Destructible != null
                    ? destructibleDamage > 0f
                    : featureDamage > 0f;
                if (confirmed)
                {
                    if (ammoBefore >= 0 && ammoAfter >= ammoBefore)
                    {
                        throw new RequireException(
                            $"Attack(world): attributable damage arrived but {firearm.ItemTypeId} consumed no ammo ({ammoBefore} -> {ammoAfter})");
                    }

                    if (target.Destructible == null)
                    {
                        confirmation = $"feature damage event({featureDamage:0.##})";
                    }

                    ctx.Emit(new TelemetryEvent("world_attack_confirmed", target.Label,
                        new Dictionary<string, object?>
                        {
                            ["attacker"] = attacker.Label,
                            ["weapon"] = firearm.ItemTypeId.ToString(),
                            ["shots"] = shots,
                            ["confirmation"] = confirmation,
                            ["hitscan_observed"] = shotObserved,
                            ["ammo_before"] = ammoBefore,
                            ["ammo_after"] = ammoAfter,
                        }));
                    EventLog.Line("STEP",
                        $"world attack confirmed attacker={attacker.Label} target='{target.Label}' weapon={firearm.ItemTypeId} shots={shots} via={confirmation} hitscan={shotObserved}");
                    yield break;
                }

                EventLog.Line("STEP",
                    $"world attack retry attacker={attacker.Label} target='{target.Label}' shot={shots} hitscan={shotObserved} ammo={ammoBefore}->{ammoAfter}");
                foreach (float wait in Coroutines.Pump(CombatFulfiller.EnsureFirearmReady(ctx, attacker, firearm)))
                {
                    yield return wait;
                }
            }
        }
        finally
        {
            HitscanHitregModuleBase.ServerAnyPlayerFired -= OnFired;
        }
    }

    private static IEnumerator<float> EstablishFirearmPosition(
        ScenarioContext ctx,
        Actor attacker,
        WorldDamageTarget damageTarget)
    {
        WorldInteractionBox target = damageTarget.HitVolume
            ?? throw new InvalidOperationException("Feature firearm target has no interaction volume.");
        if (damageTarget.PreferredFirearmPosition.HasValue)
        {
            foreach (float wait in Coroutines.Pump(MovementFulfiller.GoTo(
                ctx,
                attacker,
                damageTarget.PreferredFirearmPosition.Value)))
            {
                yield return wait;
            }

            Vector3 preferredCamera = attacker.Hub.PlayerCameraReference.position;
            if (!HasClearLineToVolume(preferredCamera, attacker.Hub, target))
            {
                throw new RequireException(
                    $"Attack(world): preferred firing position has no clear line to {target.Label}");
            }

            EventLog.Line("PROBE",
                $"world firearm preferred position actor={attacker.Label} target='{target.Label}' distance={Vector3.Distance(preferredCamera, target.Center):0.##}m");
            yield break;
        }

        Vector3 camera = attacker.Hub.PlayerCameraReference.position;
        float distance = Vector3.Distance(camera, target.Center);
        float minimumDistance = Mathf.Max(3f, target.Size.magnitude * 0.6f);
        if (distance >= minimumDistance && distance <= target.MaxDistance * 0.9f &&
            HasClearLineToVolume(camera, attacker.Hub, target))
        {
            yield break;
        }

        Vector3 away = attacker.Position - target.Center;
        away.y = 0f;
        if (away.sqrMagnitude < 0.01f)
        {
            away = Vector3.back;
        }

        away.Normalize();
        Vector3 cameraOffset = attacker.Hub.PlayerCameraReference.position - attacker.Position;
        Vector3? clearPosition = null;
        float[] angles = { 0f, 45f, -45f, 90f, -90f, 135f, -135f, 180f };
        float[] radii = { 5f, 7f, 9f };
        foreach (float angle in angles)
        {
            Vector3 direction = Quaternion.AngleAxis(angle, Vector3.up) * away;
            foreach (float radius in radii)
            {
                if (radius >= target.MaxDistance * 0.9f)
                {
                    continue;
                }

                Vector3 candidate = target.Center + (direction * radius);
                candidate.y = attacker.Position.y;
                ProbeResult probe = PlacementProbes.ValidateTeleportTarget(
                    candidate,
                    attacker.CapsuleSettings,
                    ctx.Config.FastTravelMaxDropMeters,
                    attacker.Hub);
                if (!probe.Ok ||
                    !PlacementProbes.TryFindGround(candidate, ctx.Config.FastTravelMaxDropMeters,
                        out float groundY, out _))
                {
                    continue;
                }

                Vector3 standingRoot = new(
                    candidate.x,
                    groundY + PlacementProbes.ExpectedRootHeight(attacker.CapsuleSettings),
                    candidate.z);
                Vector3 candidateCamera = standingRoot + cameraOffset;
                if (HasClearLineToVolume(candidateCamera, attacker.Hub, target))
                {
                    clearPosition = candidate;
                    break;
                }
            }

            if (clearPosition.HasValue)
            {
                break;
            }
        }

        if (!clearPosition.HasValue)
        {
            throw new RequireException(
                $"Attack(world): no probe-validated clear firing position within {target.MaxDistance:0.#}m of {target.Label}");
        }

        foreach (float wait in Coroutines.Pump(MovementFulfiller.GoTo(ctx, attacker, clearPosition.Value)))
        {
            yield return wait;
        }

        EventLog.Line("PROBE",
            $"world firearm position actor={attacker.Label} target='{target.Label}' distance={Vector3.Distance(attacker.Hub.PlayerCameraReference.position, target.Center):0.##}m");
    }

    private static bool HasClearLineToVolume(Vector3 origin, ReferenceHub attacker, WorldInteractionBox target)
    {
        Vector3 delta = target.Center - origin;
        float distance = delta.magnitude;
        if (distance < 0.05f)
        {
            return false;
        }

        float earliestTargetDistance = Mathf.Max(0f, distance - (target.Size.magnitude * 0.5f));
        foreach (RaycastHit hit in Physics.RaycastAll(
                     origin,
                     delta / distance,
                     distance,
                     PlayerRoles.FirstPersonControl.FpcStateProcessor.Mask,
                     QueryTriggerInteraction.Ignore)
                     .OrderBy(static result => result.distance))
        {
            if (hit.collider == null)
            {
                continue;
            }

            ReferenceHub? hitHub = hit.collider.GetComponentInParent<ReferenceHub>();
            if (hitHub == null)
            {
                HitboxIdentity? hitbox = hit.collider.GetComponentInParent<HitboxIdentity>();
                hitHub = hitbox != null ? hitbox.TargetHub : null;
            }

            if (hitHub == attacker)
            {
                continue;
            }

            return hit.distance >= earliestTargetDistance - 0.25f;
        }

        return true;
    }

    private static IEnumerator<float> MeleeAttack(
        ScenarioContext ctx,
        Actor attacker,
        WorldDamageTarget target)
    {
        if (!target.UsesDamageEvents || target.HitVolume == null)
        {
            throw new RequireException(
                "Attack(world melee): feature targets require an interaction volume and attacker-attributed damage events");
        }

        Vector3 horizontal = target.AimPoint - attacker.Position;
        horizontal.y = 0f;
        if (horizontal.magnitude > MeleeRange)
        {
            Vector3 approachDirection = horizontal.sqrMagnitude > 1e-6f ? -horizontal.normalized : Vector3.back;
            Vector3 approach = target.AimPoint + (approachDirection * (MeleeRange * 0.55f));
            approach.y = attacker.Position.y;
            foreach (float wait in Coroutines.Pump(MovementFulfiller.WalkTo(ctx, attacker, approach)))
            {
                yield return wait;
            }
        }

        string actionName = CombatFulfiller.MeleeActionName(attacker);

        float featureDamage = 0f;
        void OnFeatureDamaged(float amount)
        {
            if (amount > 0f)
            {
                featureDamage += amount;
            }
        }

        double deadline = ctx.ElapsedSeconds + ctx.Config.AttackTimeoutSeconds;
        int attempts = 0;
        while (ctx.ElapsedSeconds < deadline)
        {
            foreach (float wait in Coroutines.Pump(CombatFulfiller.AimAtBox(ctx, attacker, target.HitVolume)))
            {
                yield return wait;
            }

            featureDamage = 0f;
            bool observingFeatureDamage = false;
            try
            {
                target.BeginDamageObservation(attacker.PlayerId, OnFeatureDamaged);
                observingFeatureDamage = true;
                if (!CombatFulfiller.TryInvokeDummyAction(attacker.Hub, actionName))
                {
                    throw new RequireException(
                        $"Attack(world melee): dummy action '{actionName}' unavailable for {attacker.Label} ({attacker.Role})");
                }

                attempts++;
                yield return Timing.WaitForSeconds(1f);
            }
            finally
            {
                if (observingFeatureDamage)
                {
                    target.EndDamageObservation(OnFeatureDamaged);
                }
            }

            if (featureDamage > 0f)
            {
                string confirmation = $"feature damage event({featureDamage:0.##})";
                ctx.Emit(new TelemetryEvent("world_attack_confirmed", target.Label,
                    new Dictionary<string, object?>
                    {
                        ["attacker"] = attacker.Label,
                        ["role"] = attacker.Role.ToString(),
                        ["attempts"] = attempts,
                        ["confirmation"] = confirmation,
                    }));
                EventLog.Line("STEP",
                    $"world melee confirmed attacker={attacker.Label} target='{target.Label}' action='{actionName}' attempts={attempts} via={confirmation}");
                yield break;
            }
        }

        throw new RequireException(
            $"Attack(world melee): {attacker.Label} made {attempts} native attempts against {target.Label} without attributable damage");
    }
}
