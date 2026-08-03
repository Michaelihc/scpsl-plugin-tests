using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using PlaytestHarness.Core;
using Logger = LabApi.Features.Console.Logger;

namespace PlaytestHarness.Telemetry;

/// <summary>
/// Per-run telemetry sink. Writes one JSON line per event to
/// &lt;port config dir&gt;\PlaytestHarness\runs\&lt;timestamp&gt;-&lt;level&gt;.jsonl and mirrors human-readable
/// server-log lines with stable grep prefixes: [Playtest] SCENARIO/STEP/RESULT/RUN_RESULT.
/// </summary>
public sealed class EventLog : IDisposable
{
    public const string GrepPrefix = "[Playtest]";

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _sync = new();
    private StreamWriter? _writer;

    public string JsonlPath { get; }

    public EventLog(string runsDirectory, Fidelity level, DateTime startedUtc)
    {
        Directory.CreateDirectory(runsDirectory);
        string stamp = startedUtc.ToLocalTime().ToString("yyyyMMdd-HHmmss");
        string baseName = $"{stamp}-{FidelityParser.Label(level)}";

        // Same-second runs must never clobber earlier artifacts: probe for a free base name
        // (jsonl AND its sibling summary) and create the jsonl atomically (CreateNew).
        for (int attempt = 0; ; attempt++)
        {
            string candidate = attempt == 0 ? baseName : $"{baseName}-{attempt + 1}";
            string jsonlPath = Path.Combine(runsDirectory, candidate + ".jsonl");
            string summaryPath = Path.Combine(runsDirectory, candidate + ".summary.json");
            if (File.Exists(jsonlPath) || File.Exists(summaryPath))
            {
                continue;
            }

            try
            {
                FileStream stream = new(jsonlPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                JsonlPath = jsonlPath;
                _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
                {
                    AutoFlush = true,
                };
                return;
            }
            catch (IOException) when (File.Exists(jsonlPath))
            {
                // Lost a create race; try the next suffix.
            }
        }
    }

    public double ElapsedSeconds => _clock.Elapsed.TotalSeconds;

    /// <summary>Observers (MonitorHost) notified of every emitted event. Exceptions are the observer's problem.</summary>
    public event Action<TelemetryEvent>? Emitted;

    /// <summary>Writes the event as a JSONL line. Does not mirror to the server log (callers pick what to mirror).</summary>
    public void Emit(TelemetryEvent evt)
    {
        evt.T = Math.Round(ElapsedSeconds, 3);
        List<KeyValuePair<string, object?>> pairs = new(5)
        {
            new("t", evt.T),
            new("ev", evt.Ev),
        };
        if (!string.IsNullOrEmpty(evt.Scenario))
        {
            pairs.Add(new("scenario", evt.Scenario));
        }

        if (evt.Msg != null)
        {
            pairs.Add(new("msg", evt.Msg));
        }

        if (evt.Data != null)
        {
            pairs.Add(new("data", evt.Data));
        }

        StringBuilder sb = new(192);
        Json.WriteObject(sb, pairs);
        lock (_sync)
        {
            _writer?.WriteLine(sb.ToString());
        }

        Emitted?.Invoke(evt);
    }

    /// <summary>Mirrored human-readable server-log line with the stable grep prefix.</summary>
    public static void Line(string tag, string message, bool error = false)
    {
        string text = $"{GrepPrefix} {tag} {message}";
        if (error)
        {
            Logger.Error(text);
        }
        else
        {
            Logger.Info(text);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}
