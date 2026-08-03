using System.Collections.Generic;
using System.Collections.ObjectModel;
using PlaytestHarness.Telemetry;

namespace PlaytestHarness.Monitors;

/// <summary>A rule violation observed by a monitor during a run.</summary>
public sealed class Violation
{
    private static int _nextSequence;

    public Violation(string monitor, string actor, string detail, double atSeconds)
    {
        Monitor = monitor;
        Actor = actor;
        Detail = detail;
        AtSeconds = atSeconds;
        Sequence = System.Threading.Interlocked.Increment(ref _nextSequence);
    }

    public string Monitor { get; }

    public string Actor { get; }

    public string Detail { get; }

    public double AtSeconds { get; }

    /// <summary>
    /// Process-wide monotonic creation order. The merged AllViolations list sorts by (AtSeconds,
    /// Sequence) so two equal-timestamp violations from different sources can never interleave
    /// differently between snapshots — the runner's per-scenario delta relies on a stable prefix.
    /// </summary>
    public int Sequence { get; }

    public override string ToString() => $"[{Monitor}] actor={Actor} t={AtSeconds:0.##}s {Detail}";
}

/// <summary>
/// Read-only violation surface exposed to scenario code (<c>ctx.Monitors</c>). Every query returns
/// an immutable snapshot. The runtime object behind this interface is a separate facade, never the
/// live monitor host, and therefore has no lifecycle or move-authorization capabilities.
/// </summary>
public interface IViolationSource
{
    /// <summary>Immutable snapshot of all violations recorded so far, time-ordered.</summary>
    IReadOnlyList<Violation> AllViolations();

    /// <summary>Total violation count at the instant queried.</summary>
    int ViolationCount { get; }
}

/// <summary>
/// Capability-limited facade handed to scenario assemblies. It deliberately does not implement
/// <see cref="System.IDisposable"/> and exposes no host, monitor, tick, start/stop, or ordered-move
/// APIs. Holding the internal host reference here is safe because it never crosses a public member.
/// </summary>
public sealed class ViolationView : IViolationSource
{
    private readonly MonitorHost _host;

    internal ViolationView(MonitorHost host) => _host = host;

    public IReadOnlyList<Violation> AllViolations()
    {
        List<Violation> snapshot = _host.AllViolations();
        return new ReadOnlyCollection<Violation>(snapshot);
    }

    public int ViolationCount => _host.ViolationCount;
}

/// <summary>
/// Harness-internal monitor seam. Monitor implementations are lifecycle-owned exclusively by the
/// runner; scenario assemblies receive only <see cref="IViolationSource"/>.
/// </summary>
internal interface IRoundMonitor
{
    string Name { get; }

    /// <summary>Called for every telemetry event emitted during the run.</summary>
    void OnEvent(TelemetryEvent e);

    /// <summary>Called periodically (a few Hz) while a run is active.</summary>
    void Tick();

    IReadOnlyList<Violation> Violations { get; }
}
