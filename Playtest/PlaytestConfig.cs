namespace PlaytestHarness;

/// <summary>Per-port YAML config (LabAPI serializes this to config.yml in the plugin's config dir).</summary>
public sealed class PlaytestConfig
{
    /// <summary>Nickname prefix for harness-owned dummies; cleanup targets this prefix.</summary>
    public string NicknamePrefix { get; set; } = "PT-";

    /// <summary>Scenario to auto-run after server ready ('all' allowed). Empty = no auto-run.</summary>
    public string AutoRunScenario { get; set; } = string.Empty;

    /// <summary>Fidelity level for the auto-run: quick | standard | e2e.</summary>
    public string AutoRunLevel { get; set; } = "standard";

    /// <summary>Delay after server-ready (WaitingForPlayers) before the auto-run starts.</summary>
    public float AutoRunDelaySeconds { get; set; } = 10f;

    /// <summary>Destroy harness-owned dummies when the plugin is disabled.</summary>
    public bool CleanupDummiesOnDisable { get; set; } = true;

    /// <summary>
    /// TeleportMonitor: unordered per-tick moves above this many meters are violations. Tunable to
    /// diagnose false positives under server hitching (the monitor logs the tick delta).
    /// </summary>
    public float TeleportThresholdMeters { get; set; } = 1.5f;

    /// <summary>Fast-travel validation: max walkable-ground drop below a teleport target.</summary>
    public float FastTravelMaxDropMeters { get; set; } = 3f;

    /// <summary>Walk order timeout = base + per-meter * straight-line distance, clamped to max.</summary>
    public float WalkTimeoutBaseSeconds { get; set; } = 30f;

    /// <summary>See <see cref="WalkTimeoutBaseSeconds"/>.</summary>
    public float WalkTimeoutPerMeter { get; set; } = 4f;

    /// <summary>See <see cref="WalkTimeoutBaseSeconds"/>.</summary>
    public float WalkTimeoutMaxSeconds { get; set; } = 240f;

    /// <summary>Settle verb defaults: max allowed drop and grounding timeout.</summary>
    public float SettleMaxDropMeters { get; set; } = 3f;

    /// <summary>See <see cref="SettleMaxDropMeters"/>.</summary>
    public float SettleTimeoutSeconds { get; set; } = 6f;

    /// <summary>Soak verb: seconds between ground/bounds samples.</summary>
    public float SoakSampleIntervalSeconds { get; set; } = 0.5f;

    /// <summary>Actor.HoldForViewing: spectatable post-assertion window before cleanup.</summary>
    public float ViewingHoldSeconds { get; set; } = 10f;

    /// <summary>LookAt/aim: acceptable camera-to-target angular error in degrees.</summary>
    public float AngularToleranceDegrees { get; set; } = 4f;

    /// <summary>Attack verb: overall aim+fire+hit-confirm timeout.</summary>
    public float AttackTimeoutSeconds { get; set; } = 45f;

    /// <summary>MonitorHost: seconds between monitor Tick() calls.</summary>
    public float MonitorSampleIntervalSeconds { get; set; } = 0.25f;
}
