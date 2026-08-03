using System.Collections.Generic;

namespace BehaviorTestHarness.Harness;

public interface IBehaviorScenario
{
    string Name { get; }

    string Description { get; }

    IReadOnlyCollection<string> Aliases { get; }

    BehaviorTestResult Run(BehaviorScenarioContext context);
}
