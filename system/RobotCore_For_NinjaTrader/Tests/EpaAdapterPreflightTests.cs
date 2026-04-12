// Phase 8: EPA adapter preflight (kill switch + instrument block) must match RiskGate semantics for order submits.
// Run: dotnet run --project RobotCore_For_NinjaTrader/SiblingProtectiveCancelQueue.Test/SiblingProtectiveCancelQueue.Test.csproj -- EPA_ADAPTER_PREFLIGHT

using QTSW2.Robot.Core.Execution;

namespace QTSW2.Robot.Core.Tests;

public static class EpaAdapterPreflightTests
{
    public static (bool Pass, string? Error) RunAll()
    {
        if (ExecutionPermissionAuthority.TryAdapterOrderSubmitPreflight(() => true, null, _ => false, "MES", out var d1) ||
            d1 != "GLOBAL_KILL_SWITCH_ACTIVE")
            return (false, "Expected deny GLOBAL_KILL_SWITCH_ACTIVE when kill callback true");

        if (ExecutionPermissionAuthority.TryAdapterOrderSubmitPreflight(() => false, _ => true, _ => false, "MES", out var d2) ||
            d2 != "MISMATCH_EXECUTION_BLOCK")
            return (false, "Expected deny MISMATCH_EXECUTION_BLOCK when mismatch callback true");

        if (ExecutionPermissionAuthority.TryAdapterOrderSubmitPreflight(() => false, _ => false, _ => true, "MES", out var d3) ||
            d3 != "INSTRUMENT_FROZEN_OR_EPA_BLOCKED")
            return (false, "Expected deny INSTRUMENT_FROZEN_OR_EPA_BLOCKED when frozen callback true");

        if (!ExecutionPermissionAuthority.TryAdapterOrderSubmitPreflight(null, null, null, "MES", out var d4) || d4 != "")
            return (false, "Expected allow when all instrument callbacks null");

        if (!ExecutionPermissionAuthority.TryAdapterOrderSubmitPreflight(() => false, _ => false, _ => false, "MES", out var d5) ||
            d5 != "")
            return (false, "Expected allow when callbacks deny false");

        if (!ExecutionPermissionAuthority.TryAdapterOrderSubmitPreflight(() => false, null, inst => inst == "MES", "NQ",
                out var d6) ||
            d6 != "")
            return (false, "Expected allow when frozen callback false for this instrument");

        return (true, null);
    }
}
