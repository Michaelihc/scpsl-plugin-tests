using LabApi.Features.Wrappers;

namespace BehaviorTestHarness.Harness;

public sealed class BehaviorScenarioContext
{
    internal BehaviorScenarioContext(
        BehaviorTestHarnessConfig config,
        DummyRegistry dummies,
        ExperienceLedger experience,
        UiCaptureRecorder uiCapture,
        BehaviorTestLog log,
        string scenarioName)
    {
        Config = config;
        Dummies = dummies;
        Experience = experience;
        UiCapture = uiCapture;
        Log = log;
        ScenarioName = scenarioName;
    }

    public BehaviorTestHarnessConfig Config { get; }

    public DummyRegistry Dummies { get; }

    public ExperienceLedger Experience { get; }

    internal UiCaptureRecorder UiCapture { get; }

    public BehaviorTestLog Log { get; }

    public string ScenarioName { get; }

    public void Info(string message)
    {
        Log.Info(message);
    }

    public Player SpawnDummy(string label)
    {
        return Dummies.Spawn(label, Log);
    }

    public void Require(bool condition, string failure)
    {
        if (!condition)
        {
            throw new BehaviorAssertionException(failure);
        }
    }

    public string? CaptureUiScreenshot(string label = "manual")
    {
        return UiCapture.RenderLatest(label, Log);
    }

    public BehaviorTestResult Pass()
    {
        Log.Info($"scenario passed name={ScenarioName}");
        return new BehaviorTestResult(ScenarioName, true, Log.Count);
    }

    public BehaviorTestResult Fail(string failure)
    {
        Log.Error($"scenario failed name={ScenarioName} reason={failure}");
        return new BehaviorTestResult(ScenarioName, false, Log.Count, failure);
    }
}
