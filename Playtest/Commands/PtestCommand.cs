using System;
using System.Linq;
using CommandSystem;
using PlaytestHarness.Core;
using PlaytestHarness.Telemetry;

namespace PlaytestHarness.Commands;

/// <summary>
/// ptest — RA + game console entry point for the playtest harness.
/// Subcommands: run &lt;name|all&gt; [quick|standard|e2e] (default standard), list, reload, status, cleanup, report.
/// </summary>
[CommandHandler(typeof(GameConsoleCommandHandler))]
[CommandHandler(typeof(RemoteAdminCommandHandler))]
public sealed class PtestCommand : ICommand, IUsageProvider
{
    public string Command => "ptest";

    public string[] Aliases => ["playtest"];

    public string Description => "Runs tiered playtest scenarios (PlaytestHarness v2).";

    public string[] Usage => ["run <name|suite|all> [quick|standard|e2e]", "list", "reload", "status", "cleanup", "report"];

    public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
    {
        PlaytestPlugin? plugin = PlaytestPlugin.Instance;
        if (plugin?.Runner == null || plugin.Catalog == null)
        {
            response = "PlaytestHarness is not enabled.";
            return false;
        }

        string action = GetArgument(arguments, 0).ToLowerInvariant();
        switch (action)
        {
            case "run":
            {
                string name = GetArgument(arguments, 1);
                if (string.IsNullOrWhiteSpace(name))
                {
                    response = "Usage: ptest run <name|suite|all> [quick|standard|e2e]";
                    return false;
                }

                Fidelity level = Fidelity.Standard;
                string levelArg = GetArgument(arguments, 2);
                if (!string.IsNullOrWhiteSpace(levelArg) && !FidelityParser.TryParse(levelArg, out level))
                {
                    response = $"Unknown level '{levelArg}'. Use quick, standard/std, or e2e/endtoend.";
                    return false;
                }

                return plugin.TryStartRun(name, level, out response);
            }

            case "list":
            {
                if (plugin.Catalog.Scenarios.Count == 0)
                {
                    response = "No scenarios discovered.";
                    return true;
                }

                response = "Scenarios:\n" + string.Join("\n", plugin.Catalog.Scenarios.Select(s =>
                    $"  {s.Name} [{s.Supported}]" +
                    $"{(s.Aliases.Length > 0 ? " (aliases: " + string.Join(", ", s.Aliases) + ")" : string.Empty)}" +
                    $"{(s.Suites.Length > 0 ? " (suites: " + string.Join(", ", s.Suites) + ")" : string.Empty)} - {s.Description}"));
                return true;
            }

            case "reload":
            {
                if (plugin.Runner.IsRunning)
                {
                    response = "A run is active; catalog reload is only allowed while idle.";
                    return false;
                }

                int count = plugin.Catalog.Reload();
                response = $"Reloaded scenario catalog: {count} scenario(s), {plugin.Catalog.Problems.Count} problem(s).";
                return plugin.Catalog.Problems.Count == 0;
            }

            case "status":
            {
                ScenarioRunner runner = plugin.Runner;
                response = runner.IsRunning
                    ? $"Run ACTIVE level={(runner.CurrentLevel is { } lvl ? FidelityParser.Label(lvl) : "?")} scenario={runner.CurrentScenario ?? "(between scenarios)"}"
                    : "No run active.";
                return true;
            }

            case "cleanup":
            {
                if (plugin.Runner.IsRunning)
                {
                    response = "A run is active; cleanup between scenarios is automatic. Wait for RUN_RESULT.";
                    return false;
                }

                int removed = plugin.Runner.CleanupOwnedDummies();
                response = $"Cleanup removed {removed} harness dummy player(s).";
                return true;
            }

            case "report":
            {
                RunSummary? last = plugin.Runner.LastSummary;
                if (last == null)
                {
                    response = "No completed run this session.";
                    return true;
                }

                response = $"Last run: {last.Digest()} summary={last.SummaryPath ?? "(not written)"}";
                return true;
            }

            default:
                response = "Usage: ptest <run <name|suite|all> [quick|standard|e2e] | list | reload | status | cleanup | report>";
                return false;
        }
    }

    private static string GetArgument(ArraySegment<string> arguments, int index)
    {
        return arguments.Array != null && index >= 0 && index < arguments.Count
            ? arguments.Array[arguments.Offset + index]
            : string.Empty;
    }
}
