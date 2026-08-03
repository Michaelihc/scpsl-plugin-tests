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
/// Verifies the GOC surface detonation DEVICE spawns (the exposed caged-core console that replaced the
/// cube). Arms the silo charge via GocNukeState, then calls SurfaceActivationButtonService.RefreshButton
/// (both reflected, since the types are internal) and asserts it spawned (IsSpawned). Starts the round
/// first if needed (the device seats at the surface activation-panel world position). Watch the server
/// log for "[GocNuke:SurfaceButton] Spawned GOC surface detonation device" and the ABSENCE of
/// "Device model is empty" (which would mean the embedded goc-device.mer.json failed to load).
/// </summary>
public sealed class GocDeviceScenario : IBehaviorScenario
{
    public string Name => "goc-device";

    public string Description => "The GOC surface detonation device model spawns when the silo charge is armed.";

    public IReadOnlyCollection<string> Aliases => ["device", "gocdevice"];

    public BehaviorTestResult Run(BehaviorScenarioContext context)
    {
        object? plugin = ResolvePlugin(out string reason);
        context.Require(plugin != null, $"could not resolve GocNukePlugin: {reason}");

        if (!Round.IsRoundStarted)
        {
            Round.Start();
            context.Info("goc-device round not started; started it, spawning the device in 15s after generation (watch for '[goc-device] RESULT=').");
            object capturedPlugin = plugin!;
            Timing.CallDelayed(15f, () =>
            {
                try
                {
                    RunCore(context, capturedPlugin);
                }
                catch (Exception ex)
                {
                    Logger.Error($"[goc-device] RESULT=FAIL error={ex.GetBaseException().Message}");
                }
            });
            return context.Pass();
        }

        RunCore(context, plugin!);
        return context.Pass();
    }

    private static void RunCore(BehaviorScenarioContext context, object plugin)
    {
        Type pluginType = plugin.GetType();

        object? state = pluginType.GetProperty("State", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(plugin)
            ?? throw new BehaviorAssertionException("GocNuke State unavailable");
        PropertyInfo armed = state.GetType().GetProperty("SiloChargeArmed", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new BehaviorAssertionException("SiloChargeArmed property not found");
        armed.SetValue(state, true);
        context.Info("goc-device armed silo charge.");

        object? surface = pluginType.GetProperty("SurfaceButton", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(plugin)
            ?? throw new BehaviorAssertionException("SurfaceButton service unavailable");

        MethodInfo refresh = surface.GetType().GetMethod("RefreshButton", BindingFlags.Instance | BindingFlags.Public)
            ?? throw new BehaviorAssertionException("RefreshButton(out string) not found");
        object?[] args = { null };
        bool ok = refresh.Invoke(surface, args) is true;
        string response = args[0] as string ?? string.Empty;
        context.Info($"goc-device RefreshButton ok={ok} response='{response}'");
        context.Require(ok, $"RefreshButton failed: {response}");

        bool spawned = surface.GetType().GetProperty("IsSpawned", BindingFlags.Instance | BindingFlags.Public)?.GetValue(surface) is true;
        context.Require(spawned, "device reports not spawned after RefreshButton");

        Logger.Info($"[goc-device] RESULT=PASS device spawned ok={ok} isSpawned={spawned}. response='{response}'");
    }

    private static object? ResolvePlugin(out string reason)
    {
        reason = string.Empty;
        Type? pluginType = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType("GocNuke.GocNukePlugin", throwOnError: false))
            .FirstOrDefault(t => t != null);
        if (pluginType == null)
        {
            reason = "GocNuke plugin type not found (is GocNuke deployed?)";
            return null;
        }

        object? instance = pluginType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        if (instance == null)
        {
            reason = "GocNuke is not enabled";
        }

        return instance;
    }
}
