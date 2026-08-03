using System;
using System.Linq;

namespace BehaviorTestHarness.Harness;

internal sealed class BehaviorTestRunner
{
    private readonly BehaviorTestHarnessConfig _config;
    private readonly DummyRegistry _dummies;
    private readonly ExperienceLedger _experience;
    private readonly UiCaptureRecorder _uiCapture;
    private BehaviorScenarioCatalog _scenarios;

    public BehaviorTestRunner(BehaviorTestHarnessConfig config, DummyRegistry dummies, ExperienceLedger experience, UiCaptureRecorder uiCapture)
    {
        _config = config;
        _dummies = dummies;
        _experience = experience;
        _uiCapture = uiCapture;
        _scenarios = BehaviorScenarioCatalog.DiscoverLoadedAssemblies();
    }

    public BehaviorTestResult Run(string scenarioName)
    {
        if (!_scenarios.TryGet(scenarioName, out IBehaviorScenario scenario))
        {
            return new BehaviorTestResult(scenarioName, false, 0, "unknown scenario");
        }

        return RunScenario(scenario);
    }

    public BehaviorTestResult[] RunAll()
    {
        return _scenarios.Scenarios.Select(RunScenario).ToArray();
    }

    public string[] ListScenarioNames()
    {
        return _scenarios.Scenarios.Select(static scenario => scenario.Name).ToArray();
    }

    public int ReloadScenarios()
    {
        _scenarios = BehaviorScenarioCatalog.DiscoverLoadedAssemblies();
        return _scenarios.Scenarios.Count;
    }

    private BehaviorTestResult RunScenario(IBehaviorScenario scenario)
    {
        BehaviorTestLog log = new();
        log.Info($"scenario started name={scenario.Name}");
        _uiCapture.BeginScenario(scenario.Name, log);

        try
        {
            BehaviorScenarioContext context = new(_config, _dummies, _experience, _uiCapture, log, scenario.Name);
            return scenario.Run(context);
        }
        catch (BehaviorAssertionException ex)
        {
            return Fail(scenario.Name, log, ex.Message);
        }
        catch (Exception ex)
        {
            string message = ex.GetBaseException().Message;
            log.Error($"scenario failed name={scenario.Name} error={message}");
            return Fail(scenario.Name, log, message);
        }
        finally
        {
            _uiCapture.EndScenario(log);
            if (_config.CleanupDummiesAfterRun)
            {
                _dummies.DestroyOwnedDummies(log);
            }
        }
    }

    private static BehaviorTestResult Fail(string scenarioName, BehaviorTestLog log, string failure)
    {
        log.Error($"scenario failed name={scenarioName} reason={failure}");
        return new BehaviorTestResult(scenarioName, false, log.Count, failure);
    }
}
