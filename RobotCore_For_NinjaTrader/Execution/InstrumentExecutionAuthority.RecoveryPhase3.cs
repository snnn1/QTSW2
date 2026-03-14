#if NINJATRADER
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using QTSW2.Robot.Core;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Phase 3: Deterministic recovery and incident-state control.
/// Instrument-scoped recovery state model. Normal mode and recovery mode are explicitly separated.
/// </summary>
public sealed partial class InstrumentExecutionAuthority
{
    /// <summary>Recovery states. Valid transitions enforced. Phase 4 bootstrap states precede NORMAL in lifecycle.</summary>
    public enum RecoveryState
    {
        NORMAL,
        BOOTSTRAP_PENDING,
        SNAPSHOTTING,
        BOOTSTRAP_DECIDING,
        BOOTSTRAP_ADOPTING,
        RECOVERY_PENDING,
        RECONSTRUCTING,
        RECOVERY_ACTION_REQUIRED,
        FLATTENING,
        HALTED,
        RESOLVED
    }

    /// <summary>Deterministic recovery decision. One per recovery pass.</summary>
    public enum RecoveryDecision
    {
        RESUME,
        ADOPT,
        FLATTEN,
        HALT
    }

    private RecoveryState _recoveryState = RecoveryState.NORMAL;
    private readonly object _recoveryStateLock = new object();
    private string _recoveryReason = "";
    private readonly List<string> _recoveryReasons = new();
    private DateTimeOffset _recoveryTriggeredUtc;
    private ReconstructionClassification? _lastClassification;
    private RecoveryDecision? _lastDecision;
    private long _recoveryTriggersTotal;
    private long _recoveryResolvedTotal;
    private long _recoveryHaltedTotal;
    private long _recoveryFlattenTotal;
    private long _recoveryAdoptTotal;
    private long _recoveryResumeTotal;

    /// <summary>Callback invoked when recovery is requested. Caller gathers broker/journal/registry/runtime data, runs reconstruction, calls ProcessReconstructionResult.</summary>
    private Action<string, string, object, DateTimeOffset>? _onRecoveryRequestedCallback;

    /// <summary>Callback to execute flatten (e.g. enqueue NtFlattenInstrumentCommand). Called when decision is FLATTEN.</summary>
    private Action<string, string, string, DateTimeOffset>? _onRecoveryFlattenRequestedCallback;

    /// <summary>Current recovery state.</summary>
    public RecoveryState CurrentRecoveryState
    {
        get { lock (_recoveryStateLock) return _recoveryState; }
    }

    /// <summary>True if instrument is in any non-NORMAL recovery state. Use to suppress normal management. Includes Phase 4 bootstrap states.</summary>
    public bool IsInRecovery
    {
        get
        {
            var s = CurrentRecoveryState;
            return s != RecoveryState.NORMAL;
        }
    }

    /// <summary>True if instrument is in bootstrap lifecycle (BOOTSTRAP_PENDING, SNAPSHOTTING, BOOTSTRAP_DECIDING, BOOTSTRAP_ADOPTING).</summary>
    public bool IsInBootstrap
    {
        get
        {
            var s = CurrentRecoveryState;
            return s == RecoveryState.BOOTSTRAP_PENDING || s == RecoveryState.SNAPSHOTTING ||
                   s == RecoveryState.BOOTSTRAP_DECIDING || s == RecoveryState.BOOTSTRAP_ADOPTING;
        }
    }

    /// <summary>Set callback for recovery request. Caller must gather data and call ProcessReconstructionResult.</summary>
    internal void SetOnRecoveryRequestedCallback(Action<string, string, object, DateTimeOffset>? callback) =>
        _onRecoveryRequestedCallback = callback;

    /// <summary>Set callback to execute flatten when recovery decides FLATTEN.</summary>
    internal void SetOnRecoveryFlattenRequestedCallback(Action<string, string, string, DateTimeOffset>? callback) =>
        _onRecoveryFlattenRequestedCallback = callback;

    /// <summary>Valid transitions. Invalid transitions emit RECOVERY_STATE_TRANSITION_INVALID. Phase 4 bootstrap transitions included.</summary>
    private static readonly HashSet<(RecoveryState From, RecoveryState To)> ValidTransitions = new()
    {
        (RecoveryState.NORMAL, RecoveryState.BOOTSTRAP_PENDING),
        (RecoveryState.BOOTSTRAP_PENDING, RecoveryState.SNAPSHOTTING),
        (RecoveryState.SNAPSHOTTING, RecoveryState.BOOTSTRAP_DECIDING),
        (RecoveryState.BOOTSTRAP_DECIDING, RecoveryState.RECOVERY_ACTION_REQUIRED),
        (RecoveryState.BOOTSTRAP_DECIDING, RecoveryState.RESOLVED),
        (RecoveryState.BOOTSTRAP_DECIDING, RecoveryState.HALTED),
        (RecoveryState.BOOTSTRAP_DECIDING, RecoveryState.BOOTSTRAP_ADOPTING),
        (RecoveryState.BOOTSTRAP_DECIDING, RecoveryState.SNAPSHOTTING), // Rerun on stale
        (RecoveryState.BOOTSTRAP_ADOPTING, RecoveryState.RESOLVED),
        (RecoveryState.NORMAL, RecoveryState.RECOVERY_PENDING),
        (RecoveryState.RECOVERY_PENDING, RecoveryState.RECONSTRUCTING),
        (RecoveryState.RECOVERY_PENDING, RecoveryState.RECOVERY_ACTION_REQUIRED),
        (RecoveryState.RECONSTRUCTING, RecoveryState.RECOVERY_ACTION_REQUIRED),
        (RecoveryState.RECOVERY_ACTION_REQUIRED, RecoveryState.FLATTENING),
        (RecoveryState.RECOVERY_ACTION_REQUIRED, RecoveryState.HALTED),
        (RecoveryState.RECOVERY_ACTION_REQUIRED, RecoveryState.RESOLVED),
        (RecoveryState.FLATTENING, RecoveryState.RECONSTRUCTING),
        (RecoveryState.FLATTENING, RecoveryState.HALTED),
        (RecoveryState.FLATTENING, RecoveryState.RESOLVED),
        (RecoveryState.RESOLVED, RecoveryState.NORMAL),
        (RecoveryState.RESOLVED, RecoveryState.RECONSTRUCTING),
        (RecoveryState.HALTED, RecoveryState.RECONSTRUCTING),
    };

    private bool TryTransition(RecoveryState from, RecoveryState to, DateTimeOffset utcNow)
    {
        if (ValidTransitions.Contains((from, to))) return true;
        Log?.Write(RobotEvents.EngineBase(utcNow, "", ExecutionInstrumentKey, "RECOVERY_STATE_TRANSITION_INVALID", new
        {
            from_state = from.ToString(),
            to_state = to.ToString(),
            iea_instance_id = InstanceId,
            note = "Invalid recovery state transition rejected"
        }));
        return false;
    }

    /// <summary>Request recovery. Sets RECOVERY_PENDING, attaches reason, invokes callback for reconstruction.</summary>
    public void RequestRecovery(string instrument, string reason, object context, DateTimeOffset utcNow)
    {
        lock (_recoveryStateLock)
        {
            var current = _recoveryState;
            if (current == RecoveryState.NORMAL)
            {
                if (!TryTransition(current, RecoveryState.RECOVERY_PENDING, utcNow)) return;
                _recoveryState = RecoveryState.RECOVERY_PENDING;
                _recoveryReason = reason;
                _recoveryReasons.Clear();
                _recoveryReasons.Add(reason);
                _recoveryTriggeredUtc = utcNow;
                Interlocked.Increment(ref _recoveryTriggersTotal);

                Log?.Write(RobotEvents.EngineBase(utcNow, "", instrument, "RECOVERY_REQUEST_RECEIVED", new
                {
                    instrument,
                    reason,
                    iea_instance_id = InstanceId
                }));
            }
            else
            {
                Log?.Write(RobotEvents.EngineBase(utcNow, "", instrument, "RECOVERY_ALREADY_ACTIVE", new
                {
                    instrument,
                    new_reason = reason,
                    current_state = current.ToString(),
                    existing_reasons = string.Join(";", _recoveryReasons),
                    iea_instance_id = InstanceId
                }));
                _recoveryReasons.Add(reason);
                Log?.Write(RobotEvents.EngineBase(utcNow, "", instrument, "RECOVERY_ESCALATED", new
                {
                    instrument,
                    reason,
                    iea_instance_id = InstanceId
                }));
                _onRecoveryRequestedCallback?.Invoke(instrument, reason, context, utcNow);
                return;
            }
        }

        _onRecoveryRequestedCallback?.Invoke(instrument, reason, context, utcNow);
    }

    /// <summary>Process reconstruction result. Chooses deterministic decision and updates state.</summary>
    public RecoveryDecision ProcessReconstructionResult(ReconstructionResult result, DateTimeOffset utcNow)
    {
        var instrument = result.Instrument ?? ExecutionInstrumentKey;
        var classification = result.Classification;

        lock (_recoveryStateLock)
        {
            var current = _recoveryState;
            if (current != RecoveryState.RECOVERY_PENDING && current != RecoveryState.RECONSTRUCTING && current != RecoveryState.RECOVERY_ACTION_REQUIRED)
            {
                Log?.Write(RobotEvents.EngineBase(utcNow, "", instrument, "RECOVERY_RECONSTRUCTION_IGNORED", new
                {
                    instrument,
                    current_state = current.ToString(),
                    classification = classification.ToString(),
                    iea_instance_id = InstanceId
                }));
                return RecoveryDecision.HALT;
            }

            if (current == RecoveryState.RECOVERY_PENDING || current == RecoveryState.RECONSTRUCTING)
            {
                if (!TryTransition(current, RecoveryState.RECOVERY_ACTION_REQUIRED, utcNow)) return RecoveryDecision.HALT;
                _recoveryState = RecoveryState.RECOVERY_ACTION_REQUIRED;
            }

            _lastClassification = classification;

            var decision = ChooseRecoveryDecision(classification, result, utcNow);
            _lastDecision = decision;

            Log?.Write(RobotEvents.EngineBase(utcNow, "", instrument, "RECOVERY_RECONSTRUCTION_RESULT", new
            {
                instrument,
                classification = classification.ToString(),
                decision = decision.ToString(),
                broker_position_qty = result.BrokerPositionQty,
                journal_qty = result.JournalQty,
                unowned_live_count = result.UnownedLiveOrderCount,
                iea_instance_id = InstanceId
            }));

            switch (decision)
            {
                case RecoveryDecision.RESUME:
                    Interlocked.Increment(ref _recoveryResumeTotal);
                    if (TryTransition(RecoveryState.RECOVERY_ACTION_REQUIRED, RecoveryState.RESOLVED, utcNow))
                    {
                        _recoveryState = RecoveryState.RESOLVED;
                        Interlocked.Increment(ref _recoveryResolvedTotal);
                        Log?.Write(RobotEvents.EngineBase(utcNow, "", instrument, "RECOVERY_DECISION_RESUME", new { instrument, iea_instance_id = InstanceId }));
                    }
                    break;
                case RecoveryDecision.ADOPT:
                    Interlocked.Increment(ref _recoveryAdoptTotal);
                    if (TryTransition(RecoveryState.RECOVERY_ACTION_REQUIRED, RecoveryState.RESOLVED, utcNow))
                    {
                        _recoveryState = RecoveryState.RESOLVED;
                        Interlocked.Increment(ref _recoveryResolvedTotal);
                        Log?.Write(RobotEvents.EngineBase(utcNow, "", instrument, "RECOVERY_DECISION_ADOPT", new { instrument, iea_instance_id = InstanceId }));
                    }
                    break;
                case RecoveryDecision.FLATTEN:
                    Interlocked.Increment(ref _recoveryFlattenTotal);
                    if (TryTransition(RecoveryState.RECOVERY_ACTION_REQUIRED, RecoveryState.FLATTENING, utcNow))
                    {
                        _recoveryState = RecoveryState.FLATTENING;
                        Log?.Write(RobotEvents.EngineBase(utcNow, "", instrument, "RECOVERY_DECISION_FLATTEN", new { instrument, iea_instance_id = InstanceId }));
                    }
                    break;
                case RecoveryDecision.HALT:
                    Interlocked.Increment(ref _recoveryHaltedTotal);
                    RecordRecoveryHalt(instrument, utcNow);
                    if (TryTransition(RecoveryState.RECOVERY_ACTION_REQUIRED, RecoveryState.HALTED, utcNow))
                    {
                        _recoveryState = RecoveryState.HALTED;
                        Log?.Write(RobotEvents.EngineBase(utcNow, "", instrument, "RECOVERY_DECISION_HALT", new { instrument, iea_instance_id = InstanceId }));
                        Log?.Write(RobotEvents.EngineBase(utcNow, "", instrument, "RECOVERY_HALTED", new { instrument, iea_instance_id = InstanceId }));
                        Log?.Write(RobotEvents.EngineBase(utcNow, "", instrument, "RECOVERY_OPERATOR_ACTION_REQUIRED", new { instrument, iea_instance_id = InstanceId }));
                        RequestSupervisoryAction(instrument, SupervisoryTriggerReason.REPEATED_RECOVERY_HALT, SupervisorySeverity.HIGH, new { classification = classification.ToString() }, utcNow);
                    }
                    break;
            }

            return decision;
        }
    }

    private RecoveryDecision ChooseRecoveryDecision(ReconstructionClassification classification, ReconstructionResult result, DateTimeOffset utcNow)
    {
        return classification switch
        {
            ReconstructionClassification.CLEAN => RecoveryDecision.RESUME,
            ReconstructionClassification.ADOPTION_POSSIBLE => result.BrokerPositionQty != 0 ? RecoveryDecision.ADOPT : RecoveryDecision.RESUME,
            ReconstructionClassification.POSITION_ONLY_MISMATCH => result.UnownedLiveOrderCount > 0 ? RecoveryDecision.FLATTEN : RecoveryDecision.RESUME,
            ReconstructionClassification.LIVE_ORDER_OWNERSHIP_MISMATCH => RecoveryDecision.FLATTEN,
            ReconstructionClassification.JOURNAL_BROKER_MISMATCH => RecoveryDecision.FLATTEN,
            ReconstructionClassification.TERMINALITY_MISMATCH => RecoveryDecision.FLATTEN,
            ReconstructionClassification.MANUAL_INTERVENTION_DETECTED => RecoveryDecision.FLATTEN,
            ReconstructionClassification.UNSAFE_AMBIGUOUS_STATE => RecoveryDecision.HALT,
            _ => RecoveryDecision.HALT
        };
    }

    /// <summary>Execute recovery flatten via canonical path. Call when ProcessReconstructionResult returns FLATTEN.</summary>
    public void ExecuteRecoveryFlatten(string instrument, string reason, string callerContext, DateTimeOffset utcNow)
    {
        RecordFlatten(instrument, utcNow);
        RequestSupervisoryAction(instrument, SupervisoryTriggerReason.REPEATED_FLATTEN_ACTIONS, SupervisorySeverity.MEDIUM, new { reason, callerContext }, utcNow);
        Log?.Write(RobotEvents.EngineBase(utcNow, "", instrument, "RECOVERY_FLATTEN_REQUESTED", new { instrument, reason, iea_instance_id = InstanceId }));

        if (_onRecoveryFlattenRequestedCallback != null)
        {
            _onRecoveryFlattenRequestedCallback.Invoke(instrument, reason, callerContext, utcNow);
            return;
        }

        var res = RequestFlatten(instrument, reason, callerContext, utcNow);
        if (res.Success)
            Log?.Write(RobotEvents.EngineBase(utcNow, "", instrument, "RECOVERY_FLATTEN_RESOLVED", new { instrument, iea_instance_id = InstanceId }));
    }

    /// <summary>Mark recovery flatten resolved. Call when flatten fill received or account flat. May transition to RESOLVED or run reconstruction again.</summary>
    public void OnRecoveryFlattenResolved(string instrument, DateTimeOffset utcNow)
    {
        lock (_recoveryStateLock)
        {
            if (_recoveryState != RecoveryState.FLATTENING) return;
            _recoveryState = RecoveryState.RESOLVED;
            Interlocked.Increment(ref _recoveryResolvedTotal);
            Log?.Write(RobotEvents.EngineBase(utcNow, "", instrument, "RECOVERY_FLATTEN_RESOLVED", new { instrument, iea_instance_id = InstanceId }));
        }
    }

    /// <summary>Attempt transition from RESOLVED to NORMAL when CanResumeNormalExecution returns true.</summary>
    public bool TryResolveToNormal(string instrument, DateTimeOffset utcNow)
    {
        if (!CanResumeNormalExecution(instrument)) return false;

        lock (_recoveryStateLock)
        {
            if (_recoveryState != RecoveryState.RESOLVED) return false;
            if (!TryTransition(RecoveryState.RESOLVED, RecoveryState.NORMAL, utcNow)) return false;
            _recoveryState = RecoveryState.NORMAL;
            _recoveryReason = "";
            _recoveryReasons.Clear();
            Log?.Write(RobotEvents.EngineBase(utcNow, "", instrument, "RECOVERY_RESOLVED", new { instrument, iea_instance_id = InstanceId }));
            return true;
        }
    }

    /// <summary>Check if instrument may return to NORMAL. All resume criteria must pass.</summary>
    public bool CanResumeNormalExecution(string instrument)
    {
        if (_recoveryState != RecoveryState.RESOLVED) return false;
        if (_flattenLatchByInstrument.ContainsKey(instrument)) return false;
        var workingUnowned = _orderRegistry.GetAllEntries().Where(e => e.LifecycleState == OrderLifecycleState.WORKING && e.OwnershipStatus == OrderOwnershipStatus.UNOWNED).ToList();
        if (workingUnowned.Count > 0) return false;
        return true;
    }

    /// <summary>Guard: block normal management when in recovery. Emit BOOTSTRAP_GUARD_BLOCKED_NORMAL_MANAGEMENT or RECOVERY_GUARD_BLOCKED_NORMAL_MANAGEMENT and return false.</summary>
    public bool GuardNormalManagement(string operation, DateTimeOffset utcNow)
    {
        if (!IsInRecovery) return true;
        var eventType = IsInBootstrap ? "BOOTSTRAP_GUARD_BLOCKED_NORMAL_MANAGEMENT" : "RECOVERY_GUARD_BLOCKED_NORMAL_MANAGEMENT";
        Log?.Write(RobotEvents.EngineBase(utcNow, "", ExecutionInstrumentKey, eventType, new
        {
            operation,
            recovery_state = CurrentRecoveryState.ToString(),
            iea_instance_id = InstanceId
        }));
        return false;
    }

    /// <summary>Get registry snapshot for reconstruction.</summary>
    internal RecoveryRegistrySnapshot GetRegistrySnapshotForRecovery()
    {
        var entries = _orderRegistry.GetAllEntries();
        var working = entries.Where(e => e.LifecycleState == OrderLifecycleState.WORKING).ToList();
        var unownedLive = working.Count(e => e.OwnershipStatus == OrderOwnershipStatus.UNOWNED);
        return new RecoveryRegistrySnapshot
        {
            WorkingOrderIds = working.Select(e => e.BrokerOrderId).ToList(),
            UnownedLiveCount = unownedLive,
            OwnedCount = working.Count(e => e.OwnershipStatus == OrderOwnershipStatus.OWNED),
            AdoptedCount = working.Count(e => e.OwnershipStatus == OrderOwnershipStatus.ADOPTED)
        };
    }

    /// <summary>Get runtime intent snapshot for reconstruction.</summary>
    internal RecoveryRuntimeIntentSnapshot GetRuntimeIntentSnapshotForRecovery()
    {
        var activeIntents = new List<string>();
        foreach (var kvp in IntentMap)
        {
            if (OrderMap.TryGetValue(kvp.Key, out var oi) && (oi.State == "WORKING" || oi.State == "ACCEPTED" || oi.State == "SUBMITTED"))
                activeIntents.Add(kvp.Key);
        }
        return new RecoveryRuntimeIntentSnapshot { ActiveIntentIds = activeIntents };
    }

    /// <summary>Emit recovery metrics.</summary>
    internal void EmitRecoveryMetrics(DateTimeOffset utcNow)
    {
        Log?.Write(RobotEvents.EngineBase(utcNow, "", ExecutionInstrumentKey, "RECOVERY_METRICS", new
        {
            instruments_in_recovery = IsInRecovery ? 1 : 0,
            recovery_state = CurrentRecoveryState.ToString(),
            recovery_triggers_total = Interlocked.Read(ref _recoveryTriggersTotal),
            recovery_resolved_total = Interlocked.Read(ref _recoveryResolvedTotal),
            recovery_halted_total = Interlocked.Read(ref _recoveryHaltedTotal),
            recovery_flatten_total = Interlocked.Read(ref _recoveryFlattenTotal),
            recovery_adopt_total = Interlocked.Read(ref _recoveryAdoptTotal),
            recovery_resume_total = Interlocked.Read(ref _recoveryResumeTotal),
            iea_instance_id = InstanceId
        }));
    }
}
#endif
