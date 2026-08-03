using System.Collections.Generic;

namespace PlaytestHarness.Core;

/// <summary>Shared coroutine plumbing for fulfiller verb bodies.</summary>
internal static class Coroutines
{
    /// <summary>
    /// Dispose-through pump (PT-016). The runner only owns/disposes the OUTERMOST verb enumerator
    /// on timeout/abort; a nested body pumped bare would never run its own finally blocks (event
    /// unsubscribes, StopLocal/provider Cancel, key releases). foreach guarantees the nested body
    /// is disposed when the outer iterator is disposed mid-pump. Usage:
    /// <c>foreach (float wait in Coroutines.Pump(inner)) yield return wait;</c>
    /// </summary>
    internal static IEnumerable<float> Pump(IEnumerator<float> body)
    {
        try
        {
            while (body.MoveNext())
            {
                yield return body.Current;
            }
        }
        finally
        {
            body.Dispose();
        }
    }
}
