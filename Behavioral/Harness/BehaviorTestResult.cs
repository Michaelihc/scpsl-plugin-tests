namespace BehaviorTestHarness.Harness;

public sealed class BehaviorTestResult
{
    public BehaviorTestResult(string scenarioName, bool passed, int eventCount, string? failure = null)
    {
        ScenarioName = scenarioName;
        Passed = passed;
        EventCount = eventCount;
        Failure = failure;
    }

    public string ScenarioName { get; }

    public bool Passed { get; }

    public int EventCount { get; }

    public string? Failure { get; }

    public string CommandResponse => Passed
        ? $"Behavior scenario '{ScenarioName}' passed with {EventCount} log event(s)."
        : $"Behavior scenario '{ScenarioName}' failed: {Failure}";
}
