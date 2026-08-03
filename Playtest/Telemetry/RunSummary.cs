using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using PlaytestHarness.Core;

namespace PlaytestHarness.Telemetry;

/// <summary>Per-scenario outcome recorded by the runner.</summary>
public enum ScenarioOutcome
{
    Pass,
    Fail,
    Error,
    Skip,
}

public sealed class ScenarioResult
{
    public ScenarioResult(string name, ScenarioOutcome outcome, double durationSeconds, string? message)
    {
        Name = name;
        Outcome = outcome;
        DurationSeconds = durationSeconds;
        Message = message;
    }

    public string Name { get; }

    public ScenarioOutcome Outcome { get; }

    public double DurationSeconds { get; }

    public string? Message { get; }
}

/// <summary>
/// Machine-readable end-of-run artifact (seed, level, per-scenario results, durations, artifact
/// paths). Written next to the JSONL file as &lt;jsonl-name&gt;.summary.json. The future dashboard reads these.
/// </summary>
public sealed class RunSummary
{
    public RunSummary(Fidelity level, int seed, DateTime startedUtc)
    {
        Level = level;
        Seed = seed;
        StartedUtc = startedUtc;
    }

    public Fidelity Level { get; }

    public int Seed { get; }

    public DateTime StartedUtc { get; }

    public List<ScenarioResult> Results { get; } = new();

    /// <summary>Monitor + role-watchdog violations captured during the run.</summary>
    public List<string> Violations { get; } = new();

    public string? JsonlPath { get; set; }

    public string? SummaryPath { get; private set; }

    public int Passed => Results.Count(r => r.Outcome == ScenarioOutcome.Pass);

    public int Failed => Results.Count(r => r.Outcome is ScenarioOutcome.Fail or ScenarioOutcome.Error);

    public int Skipped => Results.Count(r => r.Outcome == ScenarioOutcome.Skip);

    public string Digest()
    {
        return $"level={FidelityParser.Label(Level)} passed={Passed} failed={Failed} skipped={Skipped}";
    }

    /// <summary>Serializes and writes the summary JSON next to the JSONL file. Returns the path.</summary>
    public string Write(double totalDurationSeconds)
    {
        if (JsonlPath == null)
        {
            throw new InvalidOperationException("JsonlPath must be set before writing the summary.");
        }

        SummaryPath = Path.ChangeExtension(JsonlPath, null) + ".summary.json";

        List<object?> results = new(Results.Count);
        foreach (ScenarioResult result in Results)
        {
            results.Add(new Dictionary<string, object?>
            {
                ["name"] = result.Name,
                ["outcome"] = result.Outcome.ToString().ToLowerInvariant(),
                ["duration_s"] = Math.Round(result.DurationSeconds, 3),
                ["message"] = result.Message,
            });
        }

        Dictionary<string, object?> root = new()
        {
            ["harness"] = "PlaytestHarness",
            ["level"] = FidelityParser.Label(Level),
            ["seed"] = Seed,
            ["started_utc"] = StartedUtc.ToString("o"),
            ["duration_s"] = Math.Round(totalDurationSeconds, 3),
            ["passed"] = Passed,
            ["failed"] = Failed,
            ["skipped"] = Skipped,
            ["results"] = results,
            ["violations"] = Violations,
            ["artifacts"] = new Dictionary<string, object?>
            {
                ["jsonl"] = JsonlPath,
                ["summary"] = SummaryPath,
            },
        };

        File.WriteAllText(SummaryPath, Json.Serialize(root), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return SummaryPath;
    }
}
