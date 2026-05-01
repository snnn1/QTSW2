using System;
using System.Threading;

namespace QTSW2.Robot.Core.Execution;

public sealed partial class MismatchEscalationCoordinator
{
    /// <summary>Wire the ledger snapshot provider for Phase 8b ledger-aware mismatch detection.</summary>
    public void SetLedgerSnapshotProvider(Func<string, InstrumentOwnershipSnapshot?>? provider) => _getLedgerSnapshot = provider;

    /// <summary>Wakes the mismatch audit on the active cadence (execution activity, order updates, etc.).</summary>
    public void NotifyReconciliationAuditWake()
    {
        try
        {
            _auditTimer.Change(0, Timeout.Infinite);
        }
        catch
        {
            // Timer disposed or host shutting down.
        }
    }

    public bool IsInstrumentBlockedByMismatch(string instrument)
    {
        if (string.IsNullOrWhiteSpace(instrument)) return false;
        return _stateByInstrument.TryGetValue(instrument.Trim(), out var s) && s.Blocked;
    }

    public bool IsSubmitBlockedByMismatch(string instrument, string? submitPath)
    {
        if (string.IsNullOrWhiteSpace(instrument))
            return false;

        if (!_stateByInstrument.TryGetValue(instrument.Trim(), out var state) || !state.Blocked)
            return false;

        return !CanBypassMismatchExecutionBlockForSubmit(instrument.Trim(), state, submitPath, DateTimeOffset.UtcNow);
    }

    public string? GetBlockReason(string instrument)
    {
        if (string.IsNullOrWhiteSpace(instrument)) return null;
        if (!_stateByInstrument.TryGetValue(instrument.Trim(), out var s) || !s.Blocked) return null;
        return string.IsNullOrEmpty(s.BlockReason) ? MismatchEscalationPolicy.BLOCK_REASON_PERSISTENT_MISMATCH : s.BlockReason;
    }

    /// <summary>P1.5: Current gate phase for diagnostics/tests.</summary>
    public GateLifecyclePhase GetGateLifecyclePhase(string instrument)
    {
        if (string.IsNullOrWhiteSpace(instrument)) return GateLifecyclePhase.None;
        return _stateByInstrument.TryGetValue(instrument.Trim(), out var s) ? s.GateLifecyclePhase : GateLifecyclePhase.None;
    }

    /// <summary>
    /// Call from execution fills / order updates. Resets the per-burst expensive counter on structured state changes
    /// (intent, fill delta, position qty change, entry-to-protectives); time-gap fallback only when <paramref name="details"/> is default.
    /// </summary>
    public void NotifyExecutionTrigger(string instrument, DateTimeOffset utcNow,
        MismatchExecutionTriggerDetails details = default)
    {
        if (string.IsNullOrWhiteSpace(instrument)) return;
        var inst = instrument.Trim();

        if (ShouldArmConvergenceFromExecutionTriggerDetails(details))
            ArmConvergenceCore(inst, BuildExecutionTriggerConvergenceCause(details), utcNow);

        if (!_stateByInstrument.TryGetValue(inst, out var state)) return;
        if (_isInstrumentInEngineScope != null && !_isInstrumentInEngineScope(inst) &&
            state.GateLifecyclePhase == GateLifecyclePhase.None)
            return;
        if (state.GateLifecyclePhase == GateLifecyclePhase.None) return;
        var gp = state.GateProgress;

        var resetBurst = false;
        if (details.EntryToProtectivesTransition)
            resetBurst = true;
        if (!string.IsNullOrEmpty(details.IntentId))
        {
            if (!string.IsNullOrEmpty(gp.LastTriggerIntentId) &&
                !string.Equals(details.IntentId, gp.LastTriggerIntentId, StringComparison.OrdinalIgnoreCase))
                resetBurst = true;
            gp.LastTriggerIntentId = details.IntentId;
        }

        if (details.FillDelta != 0)
            resetBurst = true;

        if (details.InstrumentPositionQty.HasValue)
        {
            if (gp.LastTriggerPositionQty.HasValue &&
                gp.LastTriggerPositionQty.Value != details.InstrumentPositionQty.Value)
                resetBurst = true;
            gp.LastTriggerPositionQty = details.InstrumentPositionQty.Value;
        }

        if (!resetBurst && details.Equals(default) && gp.LastExecutionTriggerUtc != default &&
            (utcNow - gp.LastExecutionTriggerUtc).TotalMilliseconds >
            MismatchEscalationPolicy.GATE_EXECUTION_TRIGGER_RESET_GAP_MS)
            resetBurst = true;

        if (resetBurst)
            gp.ReconciliationCyclesThisExecution = 0;

        gp.LastExecutionTriggerUtc = utcNow;
    }
}
