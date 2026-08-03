using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MEC;
using LabApi.Features;
using LabApi.Loader;
using LabApi.Loader.Features.Plugins;
using PlaytestHarness.Core;
using PlaytestHarness.Telemetry;
using ServerEvents = LabApi.Events.Handlers.ServerEvents;

namespace PlaytestHarness;

/// <summary>
/// Playtest Harness v2 (phase A core skeleton). Coroutine-native scenario runner with fidelity
/// tiers, JSONL telemetry, and the 'ptest' command. Plan of record:
/// .tests\docs\part1-harness-v2-plan.html.
/// </summary>
public sealed class PlaytestPlugin : Plugin<PlaytestConfig>
{
    private CoroutineHandle _autoRunHandle;

    internal static PlaytestPlugin? Instance { get; private set; }

    internal Catalog? Catalog { get; private set; }

    internal ScenarioRunner? Runner { get; private set; }

    public override string Name => "PlaytestHarness";

    public override string Description => "Coroutine-native tiered playtest harness (quick/standard/e2e).";

    public override string Author => "metarepo";

    public override Version Version => new(0, 1, 0);

    public override Version RequiredApiVersion => new(LabApiProperties.CompiledVersion);

    /// <summary>Runs directory under the per-port plugin config dir: ...\configs\<port>\PlaytestHarness\runs.</summary>
    internal string RunsDirectory
    {
        get
        {
            string dir = Path.Combine(this.GetConfigDirectory().FullName, "runs");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public override void Enable()
    {
        Instance = this;
        Catalog = new Catalog();
        int count = Catalog.Reload();

        // Movement seam: reflection-bound to the spike BotOrders if its plugin is on this port;
        // otherwise Available=false and walk-requiring scenarios SKIP with an explicit reason.
        Movement.BotOrdersProvider movement = new();
        Runner = new ScenarioRunner(Config!, RunsDirectory, movement);

        ServerEvents.WaitingForPlayers += OnWaitingForPlayers;
        ServerEvents.RoundRestarted += OnRoundRestarted;

        EventLog.Line("HARNESS", $"enabled; {count} scenario(s) discovered; run 'ptest list' from RA or the game console.");
    }

    public override void Disable()
    {
        ServerEvents.WaitingForPlayers -= OnWaitingForPlayers;
        ServerEvents.RoundRestarted -= OnRoundRestarted;
        Timing.KillCoroutines(_autoRunHandle);

        // Lifecycle cancellation: never leave the run coroutine alive past the plugin instance —
        // a re-enable would otherwise create a second runner beside a still-running old one.
        Runner?.AbortRun("plugin disabled");

        if (Config?.CleanupDummiesOnDisable == true && Runner != null)
        {
            Runner.CleanupOwnedDummies();
        }

        Runner = null;
        Catalog = null;
        Instance = null;
    }

    /// <summary>Resolves 'name|all' + optional level and starts a run. Shared by the command and auto-run.</summary>
    internal bool TryStartRun(string scenarioName, Fidelity level, out string response)
    {
        if (Catalog == null || Runner == null)
        {
            response = "PlaytestHarness is not enabled.";
            return false;
        }

        if (Runner.IsRunning)
        {
            response = $"A run is already active (scenario={Runner.CurrentScenario ?? "(between scenarios)"}).";
            return false;
        }

        List<Scenario> selection;
        if (string.Equals(scenarioName, "all", StringComparison.OrdinalIgnoreCase))
        {
            // Deliberate-failure demos (IncludeInRunAll=false) run by name only.
            selection = Catalog.Scenarios.Where(s => s.IncludeInRunAll).ToList();
        }
        else if (Catalog.TryGet(scenarioName, out Scenario single))
        {
            selection = [single];
        }
        else if (Catalog.TryGetSuite(scenarioName, out IReadOnlyList<Scenario> suite))
        {
            selection = suite.ToList();
        }
        else
        {
            response = $"Unknown scenario or suite '{scenarioName}'. Known scenarios: {string.Join(", ", Catalog.Scenarios.Select(s => s.Name))}";
            return false;
        }

        if (selection.Count == 0)
        {
            response = "No scenarios discovered.";
            return false;
        }

        if (!Runner.TryStartRun(selection, level))
        {
            response = "A run is already active.";
            return false;
        }

        response = $"Started run: {selection.Count} scenario(s) at level={FidelityParser.Label(level)}. Watch for {EventLog.GrepPrefix} RUN_RESULT.";
        return true;
    }

    private void OnRoundRestarted()
    {
        // A mid-run round restart resets world state under the scenario; the run is meaningless
        // (and its seed snapshot stale). Abort so the next round starts from a clean runner.
        Timing.KillCoroutines(_autoRunHandle);
        Runner?.AbortRun("round restarted");
    }

    private void OnWaitingForPlayers()
    {
        if (string.IsNullOrWhiteSpace(Config?.AutoRunScenario))
        {
            return;
        }

        Timing.KillCoroutines(_autoRunHandle);
        _autoRunHandle = Timing.RunCoroutine(AutoRunCoroutine());
    }

    private IEnumerator<float> AutoRunCoroutine()
    {
        float delay = Math.Max(0.5f, Config!.AutoRunDelaySeconds);
        if (!FidelityParser.TryParse(Config.AutoRunLevel, out Fidelity level))
        {
            EventLog.Line("AUTORUN", $"invalid AutoRunLevel '{Config.AutoRunLevel}', defaulting to standard.", error: true);
        }

        EventLog.Line("AUTORUN", $"scheduled scenario={Config.AutoRunScenario} level={FidelityParser.Label(level)} delay={delay:0.#}s");
        yield return Timing.WaitForSeconds(delay);

        if (TryStartRun(Config.AutoRunScenario, level, out string response))
        {
            EventLog.Line("AUTORUN", response);
        }
        else
        {
            EventLog.Line("AUTORUN", $"failed to start: {response}", error: true);
        }
    }
}
