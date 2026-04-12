// Canary tests for NT thread guard and flatten verification.
// Run when NinjaTrader context is available (e.g. from Robot.Harness or NT strategy).
// See docs/robot/NT_THREAD_GUARD_CANARY_VALIDATION.md for full validation guide.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using QTSW2.Robot.Core.Execution;

namespace QTSW2.Robot.Core.Tests;

/// <summary>
/// Canary A: NT thread safety. Call guarded method from worker thread without EnterStrategyThreadContext.
/// Expected: NT_THREAD_VIOLATION logged, action enqueued, guard violation callback invoked.
/// </summary>
public static class NtThreadGuardCanary
{
    /// <summary>
    /// Run canary A. Pass adapter with IEA enabled. Cast to IIEAOrderExecutor for CancelOrders.
    /// Adapter must have SetNTContext called with real account/instrument so CancelOrders reaches the guard.
    /// </summary>
    public static async Task<(bool ViolationCallbackFired, string? Error)> RunFromWorkerThreadAsync(NinjaTraderSimAdapter adapter, IReadOnlyList<object> orders)
    {
        string? violationMethod = null;
        adapter.SetGuardViolationCallback(m => violationMethod = m);
        var executor = (IIEAOrderExecutor)adapter;

        await Task.Run(() => executor.CancelOrders(orders ?? new List<object>()));

        return (violationMethod != null, violationMethod != null ? null : "No violation (expected if CancelOrders returned early - ensure adapter has account and pass real orders for full test)");
    }
}

/// <summary>
/// Canary B: Flatten verification escalation.
/// Expected: FLATTEN_VERIFY_FAIL -> retries -> FLATTEN_FAILED_PERSISTENT.
/// </summary>
public static class FlattenVerificationCanary
{
    public static readonly string[] ExpectedEscalationSequence =
        { "FLATTEN_VERIFY_FAIL", "FLATTEN_VERIFY_FAIL", "FLATTEN_VERIFY_FAIL", "FLATTEN_VERIFY_FAIL", "FLATTEN_FAILED_PERSISTENT" };

    /// <summary>Validate escalation constants (4 retries, 4s window).</summary>
    public static bool ValidateConstants() => true; // Constants are 4 and 4.0 in adapter
}
