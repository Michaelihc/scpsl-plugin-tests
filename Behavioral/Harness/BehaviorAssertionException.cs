using System;

namespace BehaviorTestHarness.Harness;

public sealed class BehaviorAssertionException : Exception
{
    public BehaviorAssertionException(string message)
        : base(message)
    {
    }
}
