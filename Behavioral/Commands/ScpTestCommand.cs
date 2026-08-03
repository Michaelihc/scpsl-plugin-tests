using System;
using BehaviorTestHarness.Harness;
using CommandSystem;

namespace BehaviorTestHarness.Commands;

[CommandHandler(typeof(GameConsoleCommandHandler))]
[CommandHandler(typeof(RemoteAdminCommandHandler))]
public sealed class ScpTestCommand : ICommand, IUsageProvider
{
    public string Command => "scptest";

    public string[] Aliases => ["bt", "behaviortest"];

    public string Description => "Runs private headless behavior tests using server-side dummies.";

    public string[] Usage => ["run [smoke|all]", "list", "reload", "cleanup"];

    public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
    {
        BehaviorTestHarnessPlugin? plugin = BehaviorTestHarnessPlugin.Instance;
        if (plugin?.Runner == null || plugin.Dummies == null)
        {
            response = "BehaviorTestHarness is not enabled.";
            return false;
        }

        string action = GetArgument(arguments, 0);
        if (string.IsNullOrWhiteSpace(action))
        {
            response = "Usage: scptest <run [smoke|all]|list|reload|cleanup>";
            return false;
        }

        switch (action.ToLowerInvariant())
        {
            case "run":
            {
                string scenario = GetArgument(arguments, 1);
                if (string.Equals(scenario, "all", StringComparison.OrdinalIgnoreCase))
                {
                    BehaviorTestResult[] results = plugin.Runner.RunAll();
                    int passed = 0;
                    foreach (BehaviorTestResult result in results)
                    {
                        if (result.Passed)
                        {
                            passed++;
                        }
                    }

                    response = $"Behavior scenarios passed {passed}/{results.Length}.";
                    return passed == results.Length;
                }

                BehaviorTestResult singleResult = plugin.Runner.Run(string.IsNullOrWhiteSpace(scenario) ? "smoke" : scenario);
                response = singleResult.CommandResponse;
                return singleResult.Passed;
            }

            case "cleanup":
            {
                BehaviorTestLog log = new();
                int removed = plugin.Dummies.DestroyOwnedDummies(log);
                response = $"BehaviorTestHarness cleanup removed {removed} dummy player(s).";
                return true;
            }

            case "list":
            {
                response = "Available behavior scenarios: " + string.Join(", ", plugin.Runner.ListScenarioNames());
                return true;
            }

            case "reload":
            {
                int count = plugin.Runner.ReloadScenarios();
                response = $"BehaviorTestHarness discovered {count} behavior scenario(s).";
                return true;
            }

            default:
                response = $"Unknown scptest action '{action}'. Usage: scptest <run [smoke|all]|list|reload|cleanup>";
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
