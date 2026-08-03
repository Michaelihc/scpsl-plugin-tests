using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BehaviorTestHarness.Harness;
using LabApi.Features.Wrappers;
using MEC;
using Logger = LabApi.Features.Console.Logger;

namespace BehaviorTestHarness.Scenarios;

/// <summary>
/// Arms the GOC silo charge by flipping GocNukeState.SiloChargeArmed true (the same state change
/// the dropped-charge and `armsilo` paths make), firing the armed event GocNuke's
/// NukeArmEffectService hooks. Deterministically starts the round first if needed (the Alpha
/// Warhead nuke panel only exists once HCZ is generated) and arms after a short gen delay via an
/// MEC callback. Read the server log for "[GocNuke:ArmFx] Nuke armed ... flashing N room light
/// controller(s)" and "[goc-nukearm] armed".
/// </summary>
public sealed class GocNukeArmScenario : IBehaviorScenario
{
    public string Name => "goc-nukearm";

    public string Description => "Arming the silo charge plays the orange nuke-room light flash + spark burst.";

    public IReadOnlyCollection<string> Aliases => ["nukearm"];

    public BehaviorTestResult Run(BehaviorScenarioContext context)
    {
        if (!Round.IsRoundStarted)
        {
            Round.Start();
            context.Info("goc-nukearm round was not started; started it, arming in 15s after facility generation (watch for '[goc-nukearm] armed').");
            Timing.CallDelayed(15f, () => DoArm("delayed-after-round-start"));
            return context.Pass();
        }

        bool armed = DoArm("immediate");
        context.Require(armed, "could not flip SiloChargeArmed true (see [goc-nukearm] log)");
        context.Info("goc-nukearm armed immediately; check log for '[GocNuke:ArmFx] Nuke armed ...'.");
        return context.Pass();
    }

    private static bool DoArm(string phase)
    {
        object? state = GetGocState(out string reason);
        if (state == null)
        {
            Logger.Error($"[goc-nukearm] {phase}: could not access GocNuke state: {reason}");
            return false;
        }

        PropertyInfo? armed = state.GetType().GetProperty("SiloChargeArmed", BindingFlags.Public | BindingFlags.Instance);
        if (armed == null)
        {
            Logger.Error($"[goc-nukearm] {phase}: SiloChargeArmed property not found");
            return false;
        }

        if (armed.GetValue(state) is true)
        {
            armed.SetValue(state, false);
        }

        armed.SetValue(state, true);
        bool latched = armed.GetValue(state) is true;
        Logger.Info($"[goc-nukearm] {phase}: SiloChargeArmed flipped -> {latched} (roundStarted={Round.IsRoundStarted}).");
        return latched;
    }

    private static object? GetGocState(out string reason)
    {
        reason = string.Empty;
        Type? pluginType = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType("GocNuke.GocNukePlugin", throwOnError: false))
            .FirstOrDefault(t => t != null);
        if (pluginType == null)
        {
            reason = "GocNuke plugin type not found";
            return null;
        }

        object? instance = pluginType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        if (instance == null)
        {
            reason = "GocNuke not enabled";
            return null;
        }

        object? state = pluginType.GetProperty("State", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(instance);
        if (state == null)
        {
            reason = "GocNuke State unavailable";
        }

        return state;
    }
}
