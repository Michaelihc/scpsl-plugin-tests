using System;
using System.Collections.Generic;
using LabApi.Features.Wrappers;
using MEC;
using PlaytestHarness.Telemetry;

namespace PlaytestHarness.Core;

/// <summary>Scenario-scoped round lifecycle verbs.</summary>
internal static class RoundFulfiller
{
    internal static IEnumerator<float> EnsureStarted(ScenarioContext ctx, float timeoutSeconds)
    {
        if (Round.IsRoundStarted)
        {
            yield break;
        }

        double deadline = ctx.ElapsedSeconds + timeoutSeconds;
        Exception? last = null;
        while (!Round.IsRoundStarted)
        {
            try
            {
                Round.Start();
                last = null;
            }
            catch (Exception exception)
            {
                last = exception.GetBaseException();
            }

            if (Round.IsRoundStarted)
            {
                break;
            }

            if (ctx.ElapsedSeconds >= deadline)
            {
                throw new RequireException(
                    $"EnsureRoundStarted: round did not start within {timeoutSeconds:0.#}s" +
                    (last != null ? $"; last error={last.GetType().Name}: {last.Message}" : string.Empty));
            }

            yield return Timing.WaitForSeconds(1f);
        }

        EventLog.Line("STEP", "round started for active scenario");
        ctx.Emit(new TelemetryEvent("round_started", "scenario-scoped EnsureRoundStarted"));
        yield return Timing.WaitForSeconds(0.5f);
    }
}
