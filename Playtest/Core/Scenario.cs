using System;
using System.Collections.Generic;

namespace PlaytestHarness.Core;

/// <summary>
/// Base class for all playtest scenarios. Scenarios are MEC coroutines: yield real waits, never fake
/// them. A scenario PASSES only by running to completion; there is no early Pass(). Failure paths:
/// <see cref="ScenarioContext.Require"/> throws <see cref="RequireException"/> (= FAIL), any other
/// exception = ERROR, exceeding <see cref="TimeoutSeconds"/> = FAIL (runner kills the coroutine).
/// </summary>
public abstract class Scenario
{
    /// <summary>Unique scenario name (used by 'ptest run &lt;name&gt;'). Must be unique across all loaded assemblies.</summary>
    public abstract string Name { get; }

    /// <summary>Alternative names accepted by 'ptest run'.</summary>
    public virtual string[] Aliases => Array.Empty<string>();

    /// <summary>
    /// Named multi-scenario batteries accepted by 'ptest run'. Unlike aliases, multiple scenarios may
    /// intentionally share a suite name and run together in stable catalog order.
    /// </summary>
    public virtual string[] Suites => Array.Empty<string>();

    public abstract string Description { get; }

    /// <summary>Inclusive fidelity range this scenario supports. Requesting a level outside it yields SKIP.</summary>
    public abstract FidelityRange Supported { get; }

    /// <summary>
    /// False excludes the scenario from 'ptest run all' (it still runs by name). For
    /// deliberate-failure demos (tier-contrast proofs) that would otherwise pollute run-all counts.
    /// </summary>
    public virtual bool IncludeInRunAll => true;

    /// <summary>
    /// Per-scenario timeout, tier-scaled: higher tiers do everything natively (full charge times,
    /// real walking) so they get more headroom. Override for scenarios with known long native waits.
    /// </summary>
    public virtual float TimeoutSeconds => Level switch
    {
        Fidelity.Quick => 60f,
        Fidelity.Standard => 180f,
        Fidelity.EndToEnd => 420f,
        _ => 180f,
    };

    /// <summary>The tier the current run executes at. Set by the runner before Run() is created.</summary>
    protected Fidelity Level { get; private set; }

    internal void SetLevel(Fidelity level) => Level = level;

    /// <summary>The scenario body. Yield MEC wait values (use ctx.Wait / ctx.WaitUntil helpers).</summary>
    public abstract IEnumerator<float> Run(ScenarioContext ctx);
}

/// <summary>Thrown by ScenarioContext.Require when an assertion fails. Runner records the scenario as FAIL.</summary>
public sealed class RequireException : Exception
{
    public RequireException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Thrown by ScenarioContext.Skip when a scenario cannot run in this environment (e.g. movement
/// provider absent). Runner records the scenario as SKIP — never a crash or silent pass.
/// </summary>
public sealed class SkipException : Exception
{
    public SkipException(string message)
        : base(message)
    {
    }
}
