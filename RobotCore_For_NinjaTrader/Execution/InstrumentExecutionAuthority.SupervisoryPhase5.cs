#if NINJATRADER
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using QTSW2.Robot.Core.Diagnostics;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Phase 5: Operational Control and Supervisory Policy.
/// Instrument-scoped supervisory state, cooldown, suspension, halt, operator acknowledgement.
/// </summary>
public sealed partial class InstrumentExecutionAuthority
{
    private SupervisoryState _supervisoryState = SupervisoryState.ACTIVE;
    private readonly object _supervisoryLock = new object();
    private DateTimeOffset _cooldownStartedUtc;
    private DateTimeOffset _haltReasonUtc;
    private string _supervisoryReason = "";
    private readonly List<string> _supervisoryReasons = new();
    private bool _operatorAckRequired;
    private string? _operatorAckReason;
    private DateTimeOffset? _lastOperatorAckUtc;
    private string? _lastOperatorContext;

    private const int COOLDOWN_DURATION_SECONDS = 60;
    private const int ROLLING_WINDOW_MINUTES = 5;
    private const int COOLDOWN_THRESHOLD = 2;
    private const int SUSPEND_THRESHOLD = 3;
    private const int HALT_THRESHOLD = 4;
    /// <summary>Phase 5 hardening: Minimum seconds in ACTIVE before allowing re-escalation to COOLDOWN. Reduces flapping.</summary>
    private const int MIN_DWELL_BEFORE_COOLDOWN_SECONDS = 120;

    private DateTimeOffset? _lastResumeToActiveUtc; // Set when COOLDOWN→ACTIVE; used for dwell hysteresis

    private readonly Queue<(DateTimeOffset Utc, string Reason)> _incidentWindow = new();
    private int _cooldownCountInWindow;
    private int _flattenCountInWindow;
    private int _recoveryHaltCountInWindow;

    private long _supervisoryTriggeredTotal;
    private long _supervisoryCooldownTotal;
    private long _supervisorySuspendedTotal;
    private long _supervisoryHaltedTotal;
    private long _operatorAckReceivedTotal;

    private Action<string, string, object>? _onSupervisoryCriticalCallback;

    private static readonly HashSet<(SupervisoryState From, SupervisoryState To)> ValidSupervisoryTransitions = new()
    {
        (SupervisoryState.ACTIVE, SupervisoryState.COOLDOWN),
        (SupervisoryState.ACTIVE, SupervisoryState.SUSPENDED),
        (SupervisoryState.ACTIVE, SupervisoryState.HALTED),
        (SupervisoryState.ACTIVE, SupervisoryState.AWAITING_OPERATOR_ACK),
        (SupervisoryState.ACTIVE, SupervisoryState.DISABLED),
        (SupervisoryState.COOLDOWN, SupervisoryState.ACTIVE),
        (SupervisoryState.COOLDOWN, SupervisoryState.SUSPENDED),
        (SupervisoryState.COOLDOWN, SupervisoryState.HALTED),
        (SupervisoryState.SUSPENDED, SupervisoryState.ACTIVE),
        (SupervisoryState.SUSPENDED, SupervisoryState.HALTED),
        (SupervisoryState.SUSPENDED, SupervisoryState.AWAITING_OPERATOR_ACK),
        (SupervisoryState.HALTED, SupervisoryState.ACTIVE),
        (SupervisoryState.HALTED, SupervisoryState.AWAITING_OPERATOR_ACK),
        (SupervisoryState.AWAITING_OPERATOR_ACK, SupervisoryState.ACTIVE),
        (SupervisoryState.AWAITING_OPERATOR_ACK, SupervisoryState.HALTED),
        (SupervisoryState.DISABLED, SupervisoryState.ACTIVE),
    };

    /// <summary>Current supervisory state.</summary>
    public SupervisoryState CurrentSupervisoryState
    {
        get { lock (_supervisoryLock) return _supervisoryState; }
    }

    /// <summary>True if new entries and normal trade management are blocked.</summary>
    public bool IsSupervisorilyBlocked
    {
        get
        {
            var s = CurrentSupervisoryState;
            return s == SupervisoryState.SUSPENDED || s == SupervisoryState.HALTED ||
                   s == SupervisoryState.AWAITING_OPERATOR_ACK || s == SupervisoryState.DISABLED;
        }
    }

    /// <summary>True if in cooldown (no new entries, recovery allowed).</summary>
    public bool IsInCooldown => CurrentSupervisoryState == SupervisoryState.COOLDOWN;

    /// <summary>Request supervisory action. Updates state per policy, applies cooldowns, emits events.</summary>
    public void RequestSupervisoryAction(string instrument, SupervisoryTriggerReason reason, SupervisorySeverity severity, object? context, DateTimeOffset utcNow)
    {
        lock (_supervisoryLock)
        {
            if (reason != SupervisoryTriggerReason.REPEATED_FLATTEN_ACTIONS && reason != SupervisoryTriggerReason.REPEATED_RECOVERY_HALT && reason != SupervisoryTriggerReason.REPEATED_BOOTSTRAP_HALTS)
                RecordIncident(utcNow, reason.ToString());
            var current = _supervisoryState;

            if (current != SupervisoryState.ACTIVE && current != SupervisoryState.COOLDOWN)
            {
                Log?.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "SUPERVISORY_ALREADY_ACTIVE", state: "ENGINE", new
                {
                    instrument,
                    reason = reason.ToString(),
                    current_state = current.ToString(),
                    iea_instance_id = InstanceId
                }));
                _supervisoryReasons.Add(reason.ToString());
                return;
            }

            Interlocked.Increment(ref _supervisoryTriggeredTotal);

            SupervisoryState target;
            if (reason == SupervisoryTriggerReason.MANUAL_OPERATOR_HALT || reason == SupervisoryTriggerReason.INSTRUMENT_KILL_SWITCH ||
                reason == SupervisoryTriggerReason.GLOBAL_KILL_SWITCH || severity == SupervisorySeverity.CRITICAL)
            {
                target = SupervisoryState.HALTED;
            }
            else if (reason == SupervisoryTriggerReason.MANUAL_OPERATOR_SUSPEND || _cooldownCountInWindow >= SUSPEND_THRESHOLD)
            {
                target = SupervisoryState.SUSPENDED;
            }
            else if (reason == SupervisoryTriggerReason.MANUAL_OPERATOR_DISABLE)
            {
                target = SupervisoryState.DISABLED;
            }
            else if (_recoveryHaltCountInWindow >= HALT_THRESHOLD || _flattenCountInWindow >= HALT_THRESHOLD)
            {
                target = SupervisoryState.AWAITING_OPERATOR_ACK;
                _operatorAckRequired = true;
                _operatorAckReason = $"REPEATED_INCIDENTS:{reason}";
            }
            else
            {
                // Hysteresis: require minimum dwell in ACTIVE before re-escalating to COOLDOWN (reduces flapping)
                if (SupervisoryPolicy.ShouldSuppressCooldownEscalation(_lastResumeToActiveUtc, utcNow, MIN_DWELL_BEFORE_COOLDOWN_SECONDS))
                {
                    var dwellSeconds = _lastResumeToActiveUtc.HasValue ? (utcNow - _lastResumeToActiveUtc.Value).TotalSeconds : 0;
                    Log?.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "SUPERVISORY_TRIGGER_DWELL_SUPPRESSED", state: "ENGINE", new
                    {
                        instrument,
                        reason = reason.ToString(),
                        dwell_seconds = dwellSeconds,
                        min_dwell_seconds = MIN_DWELL_BEFORE_COOLDOWN_SECONDS,
                        iea_instance_id = InstanceId
                    }));
                    return;
                }
                target = SupervisoryState.COOLDOWN;
                _cooldownStartedUtc = utcNow;
                Interlocked.Increment(ref _supervisoryCooldownTotal);
            }

            // Already in target (e.g. COOLDOWN + repeated cooldown escalation): ValidSupervisoryTransitions
            // does not include (COOLDOWN, COOLDOWN) — skip rather than emitting SUPERVISORY_STATE_TRANSITION_INVALID.
            if (current == target)
                return;

            if (TrySupervisoryTransition(current, target, instrument, utcNow))
            {
                _supervisoryState = target;
                _supervisoryReason = reason.ToString();
                _supervisoryReasons.Add(reason.ToString());

                Log?.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "SUPERVISORY_POLICY_APPLIED", state: "ENGINE", new
                {
                    instrument,
                    reason = reason.ToString(),
                    severity = severity.ToString(),
                    from_state = current.ToString(),
                    to_state = target.ToString(),
                    iea_instance_id = InstanceId
                }));

                if (target == SupervisoryState.COOLDOWN)
                    Log?.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "INSTRUMENT_COOLDOWN_STARTED", state: "ENGINE", new { instrument, duration_seconds = COOLDOWN_DURATION_SECONDS, iea_instance_id = InstanceId }));
                else if (target == SupervisoryState.SUSPENDED)
                {
                    Interlocked.Increment(ref _supervisorySuspendedTotal);
                    Log?.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "INSTRUMENT_SUSPENDED", state: "ENGINE", new { instrument, reason = reason.ToString(), iea_instance_id = InstanceId }));
                }
                else if (target == SupervisoryState.HALTED)
                {
                    Interlocked.Increment(ref _supervisoryHaltedTotal);
                    _haltReasonUtc = utcNow;
                    var payload = new Dictionary<string, object> { ["instrument"] = instrument, ["reason"] = reason.ToString(), ["iea_instance_id"] = InstanceId };
                    Log?.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "INSTRUMENT_HALTED", state: "ENGINE", payload));
                    _onSupervisoryCriticalCallback?.Invoke("INSTRUMENT_HALTED", instrument, payload);
                }
                else if (target == SupervisoryState.AWAITING_OPERATOR_ACK)
                {
                    var payload = new Dictionary<string, object> { ["instrument"] = instrument, ["reason"] = _operatorAckReason ?? "", ["iea_instance_id"] = InstanceId };
                    Log?.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "OPERATOR_ACK_REQUIRED", state: "ENGINE", payload));
                    _onSupervisoryCriticalCallback?.Invoke("OPERATOR_ACK_REQUIRED", instrument, payload);
                }
            }
        }
    }

    /// <summary>Record incident for rolling window. Thread-safe.</summary>
    private void RecordIncident(DateTimeOffset utcNow, string reason)
    {
        lock (_supervisoryLock)
        {
            var cutoff = utcNow.AddMinutes(-ROLLING_WINDOW_MINUTES);
            while (_incidentWindow.Count > 0 && _incidentWindow.Peek().Utc < cutoff)
                _incidentWindow.Dequeue();

            _incidentWindow.Enqueue((utcNow, reason));
            var entries = _incidentWindow.ToArray();
            _cooldownCountInWindow = entries.Count(e => e.Reason.IndexOf("COOLDOWN", StringComparison.OrdinalIgnoreCase) >= 0);
            _flattenCountInWindow = entries.Count(e => e.Reason.IndexOf("FLATTEN", StringComparison.OrdinalIgnoreCase) >= 0 || e.Reason.IndexOf("RECOVERY", StringComparison.OrdinalIgnoreCase) >= 0);
            _recoveryHaltCountInWindow = entries.Count(e => e.Reason.IndexOf("HALT", StringComparison.OrdinalIgnoreCase) >= 0);
        }
    }

    /// <summary>Record recovery halt for escalation.</summary>
    internal void RecordRecoveryHalt(string instrument, DateTimeOffset utcNow)
    {
        RecordIncident(utcNow, "RECOVERY_HALT");
    }

    /// <summary>Record flatten for escalation.</summary>
    internal void RecordFlatten(string instrument, DateTimeOffset utcNow)
    {
        RecordIncident(utcNow, "FLATTEN");
    }

    /// <summary>Record bootstrap halt for escalation.</summary>
    internal void RecordBootstrapHalt(string instrument, DateTimeOffset utcNow)
    {
        RecordIncident(utcNow, "BOOTSTRAP_HALT");
    }

    private bool TrySupervisoryTransition(SupervisoryState from, SupervisoryState to, string instrument, DateTimeOffset utcNow)
    {
        if (ValidSupervisoryTransitions.Contains((from, to))) return true;
        Log?.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "SUPERVISORY_STATE_TRANSITION_INVALID", state: "ENGINE", new
        {
            from_state = from.ToString(),
            to_state = to.ToString(),
            iea_instance_id = InstanceId
        }));
        RuntimeAuditHubRef.Active?.NotifySupervisoryStateTransitionInvalid(utcNow);
        return false;
    }

    /// <summary>Acknowledge instrument. Clears AWAITING_OPERATOR_ACK only if policy allows.</summary>
    public bool AcknowledgeInstrument(string instrument, string reason, string? operatorContext, DateTimeOffset utcNow)
    {
        lock (_supervisoryLock)
        {
            if (_supervisoryState != SupervisoryState.AWAITING_OPERATOR_ACK)
            {
                Log?.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "OPERATOR_ACK_REJECTED", state: "ENGINE", new
                {
                    instrument,
                    reason = "NOT_AWAITING_ACK",
                    current_state = _supervisoryState.ToString(),
                    iea_instance_id = InstanceId
                }));
                return false;
            }

            if (!CanResumeSupervisoryActive(instrument))
            {
                Log?.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "OPERATOR_ACK_REJECTED", state: "ENGINE", new
                {
                    instrument,
                    reason = "RESUME_CRITERIA_NOT_MET",
                    iea_instance_id = InstanceId
                }));
                return false;
            }

            _supervisoryState = SupervisoryState.ACTIVE;
            _operatorAckRequired = false;
            _operatorAckReason = null;
            _lastOperatorAckUtc = utcNow;
            _lastOperatorContext = operatorContext;
            _lastResumeToActiveUtc = utcNow; // Hysteresis: start dwell timer for re-escalation suppression
            Interlocked.Increment(ref _operatorAckReceivedTotal);

            Log?.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "OPERATOR_ACK_RECEIVED", state: "ENGINE", new
            {
                instrument,
                reason,
                operator_context = operatorContext ?? "",
                iea_instance_id = InstanceId
            }));
            return true;
        }
    }

    /// <summary>Activate per-instrument kill switch. Sets HALTED.</summary>
    public void ActivateInstrumentKillSwitch(string instrument, string reason, DateTimeOffset utcNow)
    {
        RequestSupervisoryAction(instrument, SupervisoryTriggerReason.INSTRUMENT_KILL_SWITCH, SupervisorySeverity.CRITICAL, new { reason }, utcNow);
        Log?.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "INSTRUMENT_KILL_SWITCH_ACTIVATED", state: "ENGINE", new { instrument, reason, iea_instance_id = InstanceId }));
    }

    /// <summary>Check if instrument may return to ACTIVE. All resume criteria must pass.</summary>
    public bool CanResumeSupervisoryActive(string instrument)
    {
        if (IsInRecovery) return false;
        if (_flattenLatchByInstrument.ContainsKey(instrument)) return false;
        var workingUnowned = _orderRegistry.GetAllEntries().Where(e => e.LifecycleState == OrderLifecycleState.WORKING &&
                                                                       (e.OwnershipStatus == OrderOwnershipStatus.UNOWNED ||
                                                                        e.OwnershipStatus == OrderOwnershipStatus.RECOVERABLE_ROBOT_OWNED)).ToList();
        if (workingUnowned.Count > 0) return false;

        if (_supervisoryState == SupervisoryState.COOLDOWN)
        {
            var elapsed = (DateTimeOffset.UtcNow - _cooldownStartedUtc).TotalSeconds;
            if (elapsed < COOLDOWN_DURATION_SECONDS) return false;
            Log?.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, "", instrument, "INSTRUMENT_COOLDOWN_EXPIRED", new { instrument, iea_instance_id = InstanceId }));
        }

        return true;
    }

    /// <summary>Try transition from COOLDOWN to ACTIVE when cooldown expired and criteria pass.</summary>
    public bool TryExpireCooldown(string instrument, DateTimeOffset utcNow)
    {
        lock (_supervisoryLock)
        {
            if (_supervisoryState != SupervisoryState.COOLDOWN) return false;
            if (!CanResumeSupervisoryActive(instrument)) return false;
            if (!TrySupervisoryTransition(SupervisoryState.COOLDOWN, SupervisoryState.ACTIVE, instrument, utcNow)) return false;
            _supervisoryState = SupervisoryState.ACTIVE;
            Log?.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "SUPERVISORY_RESUME_ALLOWED", state: "ENGINE", new { instrument, from_state = "COOLDOWN", iea_instance_id = InstanceId }));
            return true;
        }
    }

    /// <summary>Set callback for supervisory critical events (INSTRUMENT_HALTED, OPERATOR_ACK_REQUIRED).</summary>
    internal void SetOnSupervisoryCriticalCallback(Action<string, string, object>? callback) => _onSupervisoryCriticalCallback = callback;

    /// <summary>Emit supervisory metrics.</summary>
    internal void EmitSupervisoryMetrics(DateTimeOffset utcNow)
    {
        Log?.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "SUPERVISORY_METRICS", state: "ENGINE", new
        {
            instrument = ExecutionInstrumentKey,
            supervisory_state = CurrentSupervisoryState.ToString(),
            is_blocked = IsSupervisorilyBlocked,
            is_cooldown = IsInCooldown,
            operator_ack_required = _operatorAckRequired,
            supervisory_triggered_total = Interlocked.Read(ref _supervisoryTriggeredTotal),
            supervisory_cooldown_total = Interlocked.Read(ref _supervisoryCooldownTotal),
            supervisory_suspended_total = Interlocked.Read(ref _supervisorySuspendedTotal),
            supervisory_halted_total = Interlocked.Read(ref _supervisoryHaltedTotal),
            operator_ack_received_total = Interlocked.Read(ref _operatorAckReceivedTotal),
            iea_instance_id = InstanceId
        }));
    }
}
#endif
