using System;

namespace PlaytestHarness.Core;

/// <summary>
/// The fidelity tier a run executes at. See .tests\docs\part1-harness-v2-plan.html for full tier semantics.
/// Quick = shortcuts legal (raw TP, direct damage). Standard = scoped playtest (fast-travel + pre-arranged
/// state only, everything else native). EndToEnd = full path, zero shortcuts.
/// </summary>
public enum Fidelity
{
    Quick = 0,
    Standard = 1,
    EndToEnd = 2,
}

/// <summary>Inclusive range of fidelity tiers a scenario supports.</summary>
public readonly struct FidelityRange
{
    public Fidelity Min { get; }

    public Fidelity Max { get; }

    public FidelityRange(Fidelity min, Fidelity max)
    {
        if (max < min)
        {
            throw new ArgumentException($"FidelityRange max ({max}) must be >= min ({min}).");
        }

        Min = min;
        Max = max;
    }

    /// <summary>Quick..EndToEnd — supports every tier.</summary>
    public static FidelityRange All => new(Fidelity.Quick, Fidelity.EndToEnd);

    public static FidelityRange Only(Fidelity level) => new(level, level);

    public bool Contains(Fidelity level) => level >= Min && level <= Max;

    public override string ToString() => Min == Max ? Min.ToString() : $"{Min}..{Max}";
}

/// <summary>Parsing helpers for tier names used by the ptest command and config.</summary>
public static class FidelityParser
{
    /// <summary>Parses a level alias: quick | standard | std | e2e | endtoend (case-insensitive).</summary>
    public static bool TryParse(string? text, out Fidelity level)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "quick":
            case "q":
                level = Fidelity.Quick;
                return true;
            case "standard":
            case "std":
                level = Fidelity.Standard;
                return true;
            case "e2e":
            case "endtoend":
            case "end-to-end":
                level = Fidelity.EndToEnd;
                return true;
            default:
                level = Fidelity.Standard;
                return false;
        }
    }

    /// <summary>Short stable label used in file names and RUN_RESULT lines.</summary>
    public static string Label(Fidelity level) => level switch
    {
        Fidelity.Quick => "quick",
        Fidelity.Standard => "standard",
        Fidelity.EndToEnd => "e2e",
        _ => level.ToString().ToLowerInvariant(),
    };
}
