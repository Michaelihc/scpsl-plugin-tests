using System.Collections.Generic;
using LabApi.Features.Console;

namespace BehaviorTestHarness.Harness;

public sealed class BehaviorTestLog
{
    private readonly List<string> _entries = [];

    public int Count => _entries.Count;

    public IReadOnlyList<string> Entries => _entries;

    public void Info(string message)
    {
        _entries.Add(message);
        WriteInfo(message);
    }

    public void Error(string message)
    {
        _entries.Add("ERROR " + message);
        Logger.Error("[BehaviorTest] " + message);
    }

    public static void WriteInfo(string message)
    {
        Logger.Info("[BehaviorTest] " + message);
    }
}
