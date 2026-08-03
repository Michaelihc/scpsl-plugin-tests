using System;
using System.Collections.Generic;
using System.Reflection;
using MEC;
using PlaytestHarness.Core;
using PlaytestHarness.Telemetry;
using UserSettings.ServerSpecific;

namespace PlaytestHarness.Actors;

/// <summary>Native Server-Specific-Settings input verbs.</summary>
internal static class InputFulfiller
{
    private static readonly PropertyInfo? PressedProperty = typeof(SSKeybindSetting).GetProperty(
        nameof(SSKeybindSetting.SyncIsPressed), BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    private static readonly MethodInfo? ProcessClientResponseMethod = typeof(ServerSpecificSettingsSync).GetMethod(
        "ServerProcessClientResponseMsg",
        BindingFlags.Static | BindingFlags.NonPublic);

    internal static IEnumerator<float> PressServerSetting(
        ScenarioContext ctx,
        Actor actor,
        int settingId,
        float holdSeconds)
    {
        if (settingId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(settingId));
        }

        if (holdSeconds < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(holdSeconds));
        }

        if (PressedProperty?.GetSetMethod(nonPublic: true) == null || ProcessClientResponseMethod == null)
        {
            throw new RequireException(
                "PressServerSetting: native SSS response bindings are unavailable on this game build");
        }

        SSKeybindSetting setting = new(settingId, "PlaytestHarness native input");
        bool pressed = false;
        bool heldToCompletion = false;
        try
        {
            Dispatch(actor, setting, pressed: true);
            pressed = true;
            ctx.Emit(new TelemetryEvent("sss_input", "press", new Dictionary<string, object?>
            {
                ["actor"] = actor.Label,
                ["setting_id"] = settingId,
                ["pressed"] = true,
            }));
            EventLog.Line("STEP", $"native SSS press actor={actor.Label} setting={settingId} hold={holdSeconds:0.###}s");

            if (holdSeconds > 0f)
            {
                yield return Timing.WaitForSeconds(holdSeconds);
            }
            else
            {
                yield return Timing.WaitForOneFrame;
            }

            heldToCompletion = true;
        }
        finally
        {
            if (pressed && !heldToCompletion)
            {
                // Failure/abort path (in-flight exception or runner Dispose): a throw here would
                // REPLACE the original failure with a confusing "release rejected" message — and an
                // exception escaping an iterator Dispose is unhandled — so log-and-continue.
                try
                {
                    Dispatch(actor, setting, pressed: false);
                    EventLog.Line("STEP", $"native SSS release (cleanup) actor={actor.Label} setting={settingId}");
                }
                catch (Exception e)
                {
                    EventLog.Line("STEP",
                        $"native SSS release FAILED actor={actor.Label} setting={settingId}: {e.Message} — emulated keybind may remain pressed",
                        error: true);
                }
            }
        }

        // Happy path: a rejected release IS the verb failing — throw normally (nothing to mask).
        Dispatch(actor, setting, pressed: false);
        ctx.Emit(new TelemetryEvent("sss_input", "release", new Dictionary<string, object?>
        {
            ["actor"] = actor.Label,
            ["setting_id"] = settingId,
            ["pressed"] = false,
        }));
        EventLog.Line("STEP", $"native SSS release actor={actor.Label} setting={settingId}");
    }

    private static void Dispatch(Actor actor, SSKeybindSetting setting, bool pressed)
    {
        if (actor.Hub.connectionToClient == null)
        {
            throw new RequireException(
                $"PressServerSetting: {actor.Label} has no server connection for native SSS processing");
        }

        PressedProperty!.SetValue(setting, pressed);
        SSSClientResponse response = new(setting);
        try
        {
            ProcessClientResponseMethod!.Invoke(
                null,
                new object[] { actor.Hub.connectionToClient, response });
        }
        catch (TargetInvocationException exception)
        {
            throw new RequireException(
                $"PressServerSetting: native SSS processing failed for setting {setting.SettingId}: " +
                exception.GetBaseException().Message);
        }

        if (!ServerSpecificSettingsSync.TryGetSettingOfUser(
                actor.Hub,
                setting.SettingId,
                out SSKeybindSetting received) ||
            received.SyncIsPressed != pressed)
        {
            throw new RequireException(
                $"PressServerSetting: native SSS validation rejected setting {setting.SettingId} " +
                $"({(pressed ? "press" : "release")}) for {actor.Label}");
        }
    }
}
