using System;
using System.Collections.Generic;
using PlayerRoles;
using UnityEngine;

namespace PlaytestHarness.Core;

/// <summary>
/// A narrow allowance for a feature's own asynchronous role/position transitions. Every expectation
/// is actor-scoped, ordered, predicate-checked, count-limited, time-bounded, and telemetry-logged.
/// It does not disable either monitor and is forbidden at EndToEnd because e2e permits no teleports.
/// </summary>
public sealed class FeatureTransitionSpec
{
    public FeatureTransitionSpec(
        float withinSeconds,
        IReadOnlyList<RoleTransitionExpectation>? roles = null,
        IReadOnlyList<PositionTransitionExpectation>? positions = null)
    {
        if (withinSeconds <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(withinSeconds));
        }

        WithinSeconds = withinSeconds;
        Roles = roles ?? Array.Empty<RoleTransitionExpectation>();
        Positions = positions ?? Array.Empty<PositionTransitionExpectation>();
    }

    public float WithinSeconds { get; }

    public IReadOnlyList<RoleTransitionExpectation> Roles { get; }

    public IReadOnlyList<PositionTransitionExpectation> Positions { get; }
}

/// <summary>
/// The authored constraints of one bounded feature-owned physical transit (see
/// ScenarioContext.BeginFeatureTransit). Named properties replace a long positional parameter list
/// so the two Y-bands and the two production-state gates cannot be transposed at a call site.
/// Validation happens here, once, at construction.
/// </summary>
public sealed class FeatureTransitSpec
{
    public FeatureTransitSpec(
        float withinSeconds,
        float maxHorizontalDistance,
        float minY,
        float maxY,
        Func<Actors.Actor, bool> preArmOverrideGate,
        Func<Actors.Actor, bool> continuousOverrideGate,
        float overrideMinY,
        float overrideMaxY,
        float maxOverrideSpeed)
    {
        if (withinSeconds <= 0f || maxHorizontalDistance <= 0f || minY > maxY ||
            overrideMinY > overrideMaxY || maxOverrideSpeed <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(withinSeconds),
                "Feature transit requires positive time/radius/speed and ordered transit/override Y bands.");
        }

        WithinSeconds = withinSeconds;
        MaxHorizontalDistance = maxHorizontalDistance;
        MinY = minY;
        MaxY = maxY;
        PreArmOverrideGate = preArmOverrideGate ?? throw new ArgumentNullException(nameof(preArmOverrideGate));
        ContinuousOverrideGate = continuousOverrideGate ?? throw new ArgumentNullException(nameof(continuousOverrideGate));
        OverrideMinY = overrideMinY;
        OverrideMaxY = overrideMaxY;
        MaxOverrideSpeed = maxOverrideSpeed;
    }

    /// <summary>Transit deadline from arming, seconds.</summary>
    public float WithinSeconds { get; }

    /// <summary>Max horizontal distance from the transit anchor, meters.</summary>
    public float MaxHorizontalDistance { get; }

    /// <summary>Inclusive world-Y corridor the transit itself may occupy.</summary>
    public float MinY { get; }

    /// <summary>Inclusive world-Y corridor the transit itself may occupy.</summary>
    public float MaxY { get; }

    /// <summary>Production-state gate required for near-anchor corrections while still queued.</summary>
    public Func<Actors.Actor, bool> PreArmOverrideGate { get; }

    /// <summary>Production-state gate required for continuous overrides once armed.</summary>
    public Func<Actors.Actor, bool> ContinuousOverrideGate { get; }

    /// <summary>Inclusive world-Y band continuous overrides are confined to.</summary>
    public float OverrideMinY { get; }

    /// <summary>Inclusive world-Y band continuous overrides are confined to.</summary>
    public float OverrideMaxY { get; }

    /// <summary>Max override speed, m/s (shared budget per simulation frame).</summary>
    public float MaxOverrideSpeed { get; }
}

/// <summary>One expected external role assignment in the feature transition's exact order.</summary>
public sealed class RoleTransitionExpectation
{
    public RoleTransitionExpectation(string label, RoleTypeId role)
    {
        Label = string.IsNullOrWhiteSpace(label) ? role.ToString() : label;
        Role = role;
    }

    public string Label { get; }

    public RoleTypeId Role { get; }
}

/// <summary>
/// One expected external position override/displacement. The predicate describes the semantic
/// destination (for example an off-map cinematic pocket or the Surface landing band).
/// </summary>
public sealed class PositionTransitionExpectation
{
    public PositionTransitionExpectation(string label, Func<Vector3, bool> destination)
    {
        Label = string.IsNullOrWhiteSpace(label) ? "feature destination" : label;
        Destination = destination ?? throw new ArgumentNullException(nameof(destination));
    }

    public string Label { get; }

    public Func<Vector3, bool> Destination { get; }
}
