// Phase 8: EPA adapter preflight (kill switch + instrument block) must match RiskGate semantics for order submits.
// Run: dotnet run --project RobotCore_For_NinjaTrader/SiblingProtectiveCancelQueue.Test/SiblingProtectiveCancelQueue.Test.csproj -- EPA_ADAPTER_PREFLIGHT

using QTSW2.Robot.Core.Execution;

namespace QTSW2.Robot.Core.Tests;

public static class EpaAdapterPreflightTests
{
    private const string PathEntry = "SUBMIT_ENTRY_STOP";

    public static (bool Pass, string? Error) RunAll()
    {
        if (ExecutionPermissionAuthority.TryAdapterOrderSubmitPreflight(() => true, null, (_, _) => false, "MES", PathEntry, out var d1) ||
            d1 != "GLOBAL_KILL_SWITCH_ACTIVE")
            return (false, "Expected deny GLOBAL_KILL_SWITCH_ACTIVE when kill callback true");

        if (ExecutionPermissionAuthority.TryAdapterOrderSubmitPreflight(() => false, _ => true, (_, _) => false, "MES", PathEntry, out var d2) ||
            d2 != "MISMATCH_EXECUTION_BLOCK")
            return (false, "Expected deny MISMATCH_EXECUTION_BLOCK when mismatch callback true");

        if (ExecutionPermissionAuthority.TryAdapterOrderSubmitPreflight(() => false, _ => false, (_, _) => true, "MES", PathEntry, out var d3) ||
            d3 != "INSTRUMENT_FROZEN_OR_EPA_BLOCKED")
            return (false, "Expected deny INSTRUMENT_FROZEN_OR_EPA_BLOCKED when frozen callback true");

        if (!ExecutionPermissionAuthority.TryAdapterOrderSubmitPreflight(null, null, null, "MES", PathEntry, out var d4) || d4 != "")
            return (false, "Expected allow when all instrument callbacks null");

        if (!ExecutionPermissionAuthority.TryAdapterOrderSubmitPreflight(() => false, _ => false, (_, _) => false, "MES", PathEntry, out var d5) ||
            d5 != "")
            return (false, "Expected allow when callbacks deny false");

        if (!ExecutionPermissionAuthority.TryAdapterOrderSubmitPreflight(() => false, null, (inst, _) => inst == "MES", "NQ", PathEntry, out var d6) ||
            d6 != "")
            return (false, "Expected allow when frozen callback false for this instrument");

        if (!ExecutionPermissionAuthority.TryAdapterOrderSubmitPreflight(() => false, _ => true, (_, _) => false, "MES", PathEntry, out var d7,
                skipMismatchExecutionBlock: true) ||
            d7 != "")
            return (false, "Expected allow when skipMismatchExecutionBlock despite mismatch callback true");

        // Engine pattern: protective latch may block RI/RC but must not deny SUBMIT_PROTECTIVE_STOP at EPA preflight.
        if (!ExecutionPermissionAuthority.TryAdapterOrderSubmitPreflight(() => false, _ => false,
                (inst, path) => string.Equals(path, "SUBMIT_PROTECTIVE_STOP", System.StringComparison.Ordinal) ? false : true,
                "MES", "SUBMIT_PROTECTIVE_STOP", out var d8) || d8 != "")
            return (false, "Expected allow protective submit when delegate exempts SUBMIT_PROTECTIVE_STOP");

        if (ExecutionPermissionAuthority.TryAdapterOrderSubmitPreflight(() => false, _ => false,
                (inst, path) => string.Equals(path, "SUBMIT_PROTECTIVE_STOP", System.StringComparison.Ordinal) ? false : true,
                "MES", PathEntry, out var d9) || d9 != "INSTRUMENT_FROZEN_OR_EPA_BLOCKED")
            return (false, "Expected deny entry when same delegate blocks non-protective path");

        return (true, null);
    }
}
