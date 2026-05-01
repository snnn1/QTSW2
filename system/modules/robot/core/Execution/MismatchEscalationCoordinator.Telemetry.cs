using System;
using System.Linq;
using QTSW2.Robot.Core;

namespace QTSW2.Robot.Core.Execution;

public sealed partial class MismatchEscalationCoordinator
{
    /// <summary>Test harness: counts for gate RESULT emission vs quiet suppression.</summary>
    internal int TestGateResultEmitCount { get; private set; }

    internal int TestGateResultSuppressCount { get; private set; }

    private int _mismatchDetectedCount;
    private int _mismatchPersistentCount;
    private int _mismatchFailClosedCount;
    private int _mismatchClearedCount;
    private int _mismatchBrokerAheadCount;
    private int _mismatchJournalAheadCount;
    private int _mismatchNetPositionCount;
    private int _mismatchGrossDivergenceCount;
    private int _mismatchStructuralMultiIntentCount;
    private int _mismatchHedgedNetFlatCount;
    private int _mismatchRegistryMissingCount;
    private int _mismatchProtectiveDivergenceCount;

    private void IncrementTypeMetric(MismatchType type)
    {
        switch (type)
        {
            case MismatchType.BROKER_AHEAD: _mismatchBrokerAheadCount++; break;
            case MismatchType.JOURNAL_AHEAD: _mismatchJournalAheadCount++; break;
            case MismatchType.NET_POSITION_MISMATCH: _mismatchNetPositionCount++; break;
            case MismatchType.GROSS_POSITION_DIVERGENCE: _mismatchGrossDivergenceCount++; break;
            case MismatchType.STRUCTURAL_MULTI_INTENT: _mismatchStructuralMultiIntentCount++; break;
            case MismatchType.HEDGED_NET_FLAT_GROSS_OPEN: _mismatchHedgedNetFlatCount++; break;
            case MismatchType.ORDER_REGISTRY_MISSING: _mismatchRegistryMissingCount++; break;
            case MismatchType.WORKING_ORDER_COUNT_CONVERGENCE: break;
            case MismatchType.PROTECTIVE_STATE_DIVERGENCE: _mismatchProtectiveDivergenceCount++; break;
        }
    }

    internal void ResetTestGateTelemetryCounters()
    {
        TestGateResultEmitCount = 0;
        TestGateResultSuppressCount = 0;
    }

    public void EmitMetrics(DateTimeOffset utcNow)
    {
        var instruments = _stateByInstrument
            .Where(kv => kv.Value.EscalationState != MismatchEscalationState.NONE || kv.Value.GateLifecyclePhase != GateLifecyclePhase.None)
            .Select(kv => new
            {
                instrument = kv.Key,
                mismatch_type = kv.Value.MismatchType.ToString(),
                escalation_state = kv.Value.EscalationState.ToString(),
                gate_phase = kv.Value.GateLifecyclePhase.ToString(),
                blocked = kv.Value.Blocked,
                persistence_ms = kv.Value.PersistenceMs,
                retry_count = kv.Value.RetryCount,
                first_detected_utc = kv.Value.FirstDetectedUtc.ToString("o"),
                last_detected_utc = kv.Value.LastDetectedUtc.ToString("o"),
                block_reason = kv.Value.BlockReason
            })
            .ToList();

        _log?.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "RECONCILIATION_MISMATCH_METRICS", state: "ENGINE",
            new
            {
                mismatch_detected_count = _mismatchDetectedCount,
                mismatch_persistent_count = _mismatchPersistentCount,
                mismatch_fail_closed_count = _mismatchFailClosedCount,
                mismatch_cleared_count = _mismatchClearedCount,
                mismatch_broker_ahead_count = _mismatchBrokerAheadCount,
                mismatch_journal_ahead_count = _mismatchJournalAheadCount,
                mismatch_net_position_count = _mismatchNetPositionCount,
                mismatch_gross_divergence_count = _mismatchGrossDivergenceCount,
                mismatch_structural_multi_intent_count = _mismatchStructuralMultiIntentCount,
                mismatch_hedged_net_flat_gross_open_count = _mismatchHedgedNetFlatCount,
                mismatch_registry_missing_count = _mismatchRegistryMissingCount,
                mismatch_protective_divergence_count = _mismatchProtectiveDivergenceCount,
                last_audit_utc = _lastAuditUtc != DateTimeOffset.MinValue ? _lastAuditUtc.ToString("o") : null,
                instruments
            }));
    }
}
