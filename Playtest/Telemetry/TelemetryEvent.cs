using System.Collections.Generic;

namespace PlaytestHarness.Telemetry;

/// <summary>
/// One telemetry event — serialized as a single JSONL line:
/// {"t":12.41,"ev":"step","scenario":"smoke","msg":"...","data":{...}}
/// Keep names short and stable; part 2 (AI rounds) consumes the same stream.
/// </summary>
public sealed class TelemetryEvent
{
    /// <summary>Seconds since run start. Filled by the EventLog when emitted.</summary>
    public double T { get; internal set; }

    /// <summary>Event kind, e.g. "run_start", "scenario_start", "step", "require", "result", "run_result".</summary>
    public string Ev { get; }

    /// <summary>Scenario name the event belongs to (empty for run-level events).</summary>
    public string Scenario { get; internal set; }

    /// <summary>Human-readable message.</summary>
    public string? Msg { get; }

    /// <summary>Optional structured payload. Values may be string, bool, numeric, or nested IDictionary/IEnumerable.</summary>
    public IReadOnlyDictionary<string, object?>? Data { get; }

    public TelemetryEvent(string ev, string? msg = null, IReadOnlyDictionary<string, object?>? data = null)
    {
        Ev = ev;
        Msg = msg;
        Data = data;
        Scenario = string.Empty;
    }
}
