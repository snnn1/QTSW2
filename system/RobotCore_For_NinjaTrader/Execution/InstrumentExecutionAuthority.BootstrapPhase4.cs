#if NINJATRADER
using System;
using System.Linq;
using System.Threading;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Phase 4: Crash/Disconnect Determinism. Bootstrap entry points, snapshot epoch, CanCompleteBootstrap.
/// </summary>
public sealed partial class InstrumentExecutionAuthority
{
    private long _bootstrapSnapshotEpoch;
    private DateTimeOffset _bootstrapStartedUtc;
    private volatile bool _bootstrapSnapshotStale;
    private long _bootstrapStartedTotal;
    private long _bootstrapResumedTotal;
    private long _bootstrapHaltedTotal;
    private long _bootstrapAdoptTotal;
    private long _bootstrapFlattenTotal;

    /// <summary>Callback invoked when bootstrap snapshot is requested. Caller gathers four views, classifies, decides, calls ProcessBootstrapResult.</summary>
    private Action<string, BootstrapReason, DateTimeOffset>? _onBootstrapSnapshotRequestedCallback;

    /// <summary>Set callback for bootstrap snapshot. Caller must gather data and call ProcessBootstrapResult.</summary>
    internal void SetOnBootstrapSnapshotRequestedCallback(Action<string, BootstrapReason, DateTimeOffset>? callback) =>
        _onBootstrapSnapshotRequestedCallback = callback;

    /// <summary>Begin bootstrap for instrument. Transitions to BOOTSTRAP_PENDING, emits BOOTSTRAP_STARTED, invokes snapshot callback.</summary>
    public void BeginBootstrapForInstrument(string instrument, BootstrapReason reason, DateTimeOffset utcNow)
    {
        lock (_recoveryStateLock)
        {
            var current = _recoveryState;
            if (current != RecoveryState.NORMAL)
            {
                Log?.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "BOOTSTRAP_ALREADY_ACTIVE", state: "ENGINE", new
                {
                    instrument,
                    reason = reason.ToString(),
                    current_state = current.ToString(),
                    iea_instance_id = InstanceId
                }));
                return;
            }

            if (!TryTransition(RecoveryState.NORMAL, RecoveryState.BOOTSTRAP_PENDING, utcNow))
                return;

            _recoveryState = RecoveryState.BOOTSTRAP_PENDING;
            _recoveryReason = $"BOOTSTRAP:{reason}";
            _recoveryReasons.Clear();
            _recoveryReasons.Add(_recoveryReason);
            _recoveryTriggeredUtc = utcNow;
            _bootstrapStartedUtc = utcNow;
            _bootstrapSnapshotStale = false;
            Interlocked.Increment(ref _bootstrapStartedTotal);

            Log?.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "BOOTSTRAP_STARTED", state: "ENGINE", new
            {
                instrument,
                reason = reason.ToString(),
                iea_instance_id = InstanceId
            }));
        }

        _onBootstrapSnapshotRequestedCallback?.Invoke(instrument, reason, utcNow);
    }

    /// <summary>Begin reconnect recovery. Same as BeginBootstrapForInstrument with reason CONNECTION_RECOVERED.</summary>
    public void BeginReconnectRecovery(string instrument, DateTimeOffset utcNow)
    {
        BeginBootstrapForInstrument(instrument, BootstrapReason.CONNECTION_RECOVERED, utcNow);
    }

    /// <summary>Process bootstrap result. Classifies, decides, transitions state.</summary>
    public BootstrapDecision ProcessBootstrapResult(BootstrapSnapshot snapshot, DateTimeOffset utcNow)
    {
        var instrument = snapshot.Instrument ?? ExecutionInstrumentKey;

        lock (_recoveryStateLock)
        {
            var current = _recoveryState;
            if (current != RecoveryState.BOOTSTRAP_PENDING && current != RecoveryState.SNAPSHOTTING && current != RecoveryState.BOOTSTRAP_DECIDING)
            {
                Log?.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "BOOTSTRAP_RESULT_IGNORED", state: "ENGINE", new
                {
                    instrument,
                    current_state = current.ToString(),
                    iea_instance_id = InstanceId
                }));
                return BootstrapDecision.HALT;
            }

            if (_bootstrapSnapshotStale)
            {
                Log?.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "BOOTSTRAP_SNAPSHOT_STALE_RERUN", state: "ENGINE", new { instrument, iea_instance_id = InstanceId }));
                _bootstrapSnapshotStale = false;
                _onBootstrapSnapshotRequestedCallback?.Invoke(instrument, BootstrapReason.PLATFORM_RESTART, utcNow);
                return BootstrapDecision.HALT;
            }

            if (current == RecoveryState.BOOTSTRAP_PENDING || current == RecoveryState.SNAPSHOTTING)
            {
                if (!TryTransition(current, RecoveryState.BOOTSTRAP_DECIDING, utcNow))
                    return BootstrapDecision.HALT;
                _recoveryState = RecoveryState.BOOTSTRAP_DECIDING;
            }

            var classification = BootstrapClassifier.Classify(snapshot);
            var decision = BootstrapDecider.Decide(classification, snapshot);

            var report = new StartupReconciliationReport
            {
                Instrument = instrument,
                BrokerQty = snapshot.BrokerPositionQty,
                JournalQty = snapshot.JournalQty,
                WorkingOrderCount = snapshot.BrokerWorkingOrderCount,
                ProtectiveStatus = snapshot.ProtectiveStatus,
                ReconstructedLifecycleState = "",
                ReconciliationStatus = classification.ToString(),
                ActionTaken = decision.ToString(),
                Classification = classification.ToString(),
                Decision = decision.ToString(),
                ReportUtc = utcNow
            };
            Log?.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "STARTUP_RECONCILIATION_REPORT", state: "ENGINE", new
            {
                broker_qty = report.BrokerQty,
                journal_qty = report.JournalQty,
                working_order_count = report.WorkingOrderCount,
                protective_status = report.ProtectiveStatus.ToString(),
                reconstructed_lifecycle_state = report.ReconstructedLifecycleState,
                reconciliation_status = report.ReconciliationStatus,
                action_taken = report.ActionTaken,
                classification = report.Classification,
                decision = report.Decision,
                iea_instance_id = InstanceId
            }));

            Log?.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "BOOTSTRAP_CLASSIFIED", state: "ENGINE", new
            {
                instrument,
                classification = classification.ToString(),
                decision = decision.ToString(),
                broker_position_qty = snapshot.BrokerPositionQty,
                journal_qty = snapshot.JournalQty,
                unowned_live_count = snapshot.UnownedLiveOrderCount,
                iea_instance_id = InstanceId
            }));

            switch (decision)
            {
                case BootstrapDecision.RESUME:
                    Interlocked.Increment(ref _bootstrapResumedTotal);
                    // Observability: log when RESUME chosen with no broker orders visible (delayed broker risk)
                    if (snapshot.BrokerWorkingOrderCount == 0)
                    {
                        Log?.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "BOOTSTRAP_RESUME_NO_BROKER_ORDERS", state: "ENGINE", new
                        {
                            instrument,
                            broker_working_count = 0,
                            classification = classification.ToString(),
                            note = "Bootstrap chose RESUME with no broker orders visible. If broker reports orders later, reconciliation/adoption will handle.",
                            iea_instance_id = InstanceId
                        }));
                    }
                    if (TryTransition(RecoveryState.BOOTSTRAP_DECIDING, RecoveryState.RESOLVED, utcNow))
                    {
                        _recoveryState = RecoveryState.RESOLVED;
                        Interlocked.Increment(ref _recoveryResolvedTotal);
                        Log?.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "BOOTSTRAP_DECISION_RESUME", state: "ENGINE", new { instrument, iea_instance_id = InstanceId }));
                        Log?.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "BOOTSTRAP_READY_TO_RESUME", state: "ENGINE", new { instrument, iea_instance_id = InstanceId }));
                    }
                    break;
                case BootstrapDecision.ADOPT:
                    Interlocked.Increment(ref _bootstrapAdoptTotal);
                    if (TryTransition(RecoveryState.BOOTSTRAP_DECIDING, RecoveryState.BOOTSTRAP_ADOPTING, utcNow))
                    {
                        _recoveryState = RecoveryState.BOOTSTRAP_ADOPTING;
                        Log?.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "BOOTSTRAP_DECISION_ADOPT", state: "ENGINE", new { instrument, iea_instance_id = InstanceId }));
                    }
                    break;
                case BootstrapDecision.FLATTEN_THEN_RECONSTRUCT:
                case BootstrapDecision.RECONCILE_THEN_RESUME:
                    Interlocked.Increment(ref _bootstrapFlattenTotal);
                    if (TryTransition(RecoveryState.BOOTSTRAP_DECIDING, RecoveryState.RECOVERY_ACTION_REQUIRED, utcNow))
                    {
                        _recoveryState = RecoveryState.RECOVERY_ACTION_REQUIRED;
                        Log?.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "BOOTSTRAP_DECISION_FLATTEN", state: "ENGINE", new { instrument, iea_instance_id = InstanceId }));
                        ExecuteRecoveryFlatten(instrument, "BOOTSTRAP_FLATTEN_THEN_RECONSTRUCT", "BootstrapPhase4", utcNow);
                    }
                    break;
                case BootstrapDecision.HALT:
                    Interlocked.Increment(ref _bootstrapHaltedTotal);
                    RecordBootstrapHalt(instrument, utcNow);
                    if (TryTransition(RecoveryState.BOOTSTRAP_DECIDING, RecoveryState.HALTED, utcNow))
                    {
                        _recoveryState = RecoveryState.HALTED;
                        Log?.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "BOOTSTRAP_DECISION_HALT", state: "ENGINE", new { instrument, iea_instance_id = InstanceId }));
                        Log?.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "BOOTSTRAP_HALTED", state: "ENGINE", new { instrument, iea_instance_id = InstanceId }));
                        Log?.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "BOOTSTRAP_OPERATOR_ACTION_REQUIRED", state: "ENGINE", new { instrument, iea_instance_id = InstanceId }));
                        RequestSupervisoryAction(instrument, SupervisoryTriggerReason.REPEATED_BOOTSTRAP_HALTS, SupervisorySeverity.HIGH, new { decision = "HALT" }, utcNow);
                    }
                    break;
            }

            return decision;
        }
    }

    /// <summary>Mark bootstrap adoption completed. Call when ScanAndAdoptExistingOrders finishes for ADOPT path.</summary>
    public void OnBootstrapAdoptionCompleted(string instrument, DateTimeOffset utcNow)
    {
        lock (_recoveryStateLock)
        {
            if (_recoveryState != RecoveryState.BOOTSTRAP_ADOPTING) return;
            if (!TryTransition(RecoveryState.BOOTSTRAP_ADOPTING, RecoveryState.RESOLVED, utcNow)) return;
            _recoveryState = RecoveryState.RESOLVED;
            Interlocked.Increment(ref _recoveryResolvedTotal);
            Log?.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "BOOTSTRAP_ADOPTION_COMPLETED", state: "ENGINE", new { instrument, iea_instance_id = InstanceId }));
            Log?.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "BOOTSTRAP_READY_TO_RESUME", state: "ENGINE", new { instrument, iea_instance_id = InstanceId }));
        }
    }

    /// <summary>Mark bootstrap snapshot stale. Call when critical event arrives during bootstrap. Triggers re-run of snapshot callback.</summary>
    internal void MarkBootstrapSnapshotStale(DateTimeOffset utcNow)
    {
        if (!IsInBootstrap) return;
        _bootstrapSnapshotStale = true;
        Log?.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "BOOTSTRAP_SNAPSHOT_STALE", state: "ENGINE", new { iea_instance_id = InstanceId }));
        _onBootstrapSnapshotRequestedCallback?.Invoke(ExecutionInstrumentKey, BootstrapReason.PLATFORM_RESTART, utcNow);
    }

    /// <summary>Transition to SNAPSHOTTING. Call before running snapshot collection. Allows BOOTSTRAP_DECIDING when rerunning on stale.</summary>
    internal bool TryTransitionToSnapshooting(DateTimeOffset utcNow)
    {
        lock (_recoveryStateLock)
        {
            if (_recoveryState == RecoveryState.BOOTSTRAP_PENDING)
            {
                if (!TryTransition(RecoveryState.BOOTSTRAP_PENDING, RecoveryState.SNAPSHOTTING, utcNow)) return false;
                _recoveryState = RecoveryState.SNAPSHOTTING;
                _bootstrapSnapshotEpoch = Interlocked.Increment(ref _bootstrapSnapshotEpoch);
                return true;
            }
            if (_recoveryState == RecoveryState.BOOTSTRAP_DECIDING && _bootstrapSnapshotStale)
            {
                if (!TryTransition(RecoveryState.BOOTSTRAP_DECIDING, RecoveryState.SNAPSHOTTING, utcNow)) return false;
                _recoveryState = RecoveryState.SNAPSHOTTING;
                _bootstrapSnapshotStale = false;
                _bootstrapSnapshotEpoch = Interlocked.Increment(ref _bootstrapSnapshotEpoch);
                return true;
            }
            return false;
        }
    }

    /// <summary>Returns true when: broker position classified, live orders ownership-resolved, no unowned live orders, registry/broker consistent, no snapshot stale, no flatten in progress, no halt, adoption completed if required.</summary>
    public bool CanCompleteBootstrap(string instrument)
    {
        if (CurrentRecoveryState != RecoveryState.RESOLVED) return false;
        if (_bootstrapSnapshotStale) return false;
        if (_flattenLatchByInstrument.ContainsKey(instrument)) return false;
        var workingUnowned = _orderRegistry.GetAllEntries().Where(e => e.LifecycleState == OrderLifecycleState.WORKING &&
                                                                        (e.OwnershipStatus == OrderOwnershipStatus.UNOWNED ||
                                                                         e.OwnershipStatus == OrderOwnershipStatus.RECOVERABLE_ROBOT_OWNED)).ToList();
        if (workingUnowned.Count > 0) return false;
        return true;
    }

    /// <summary>Emit bootstrap metrics.</summary>
    internal void EmitBootstrapMetrics(DateTimeOffset utcNow)
    {
        Log?.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "BOOTSTRAP_METRICS", state: "ENGINE", new
        {
            instruments_in_bootstrap = IsInBootstrap ? 1 : 0,
            bootstrap_state = CurrentRecoveryState.ToString(),
            bootstrap_started_total = Interlocked.Read(ref _bootstrapStartedTotal),
            bootstrap_resumed_total = Interlocked.Read(ref _bootstrapResumedTotal),
            bootstrap_halted_total = Interlocked.Read(ref _bootstrapHaltedTotal),
            bootstrap_adopt_total = Interlocked.Read(ref _bootstrapAdoptTotal),
            bootstrap_flatten_total = Interlocked.Read(ref _bootstrapFlattenTotal),
            iea_instance_id = InstanceId
        }));
    }
}
#endif
