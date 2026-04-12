using System;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Non–NinjaTrader harness: registry/latch/dedup helpers that live in NT partials when building RobotCore_For_NinjaTrader.
/// </summary>
public sealed partial class InstrumentExecutionAuthority
{
    internal void CheckFlattenLatchTimeouts()
    {
    }

    internal int RunRegistryCleanup(DateTimeOffset utcNow, Func<string, bool>? intentIsActive = null) => 0;

    internal void VerifyRegistryIntegrity(DateTimeOffset utcNow)
    {
    }

    internal void EmitRegistryMetrics(DateTimeOffset utcNow)
    {
    }

    internal void EmitExecutionOrderingMetrics(DateTimeOffset utcNow)
    {
    }

    private void EvictDedupEntries()
    {
    }
}
