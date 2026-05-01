// NinjaTrader-specific implementation using real NT APIs
// This file is compiled only when NINJATRADER is defined (inside NT Strategy context)

// CRITICAL: Define NINJATRADER for NinjaTrader's compiler
// NinjaTrader compiles to tmp folder and may not respect .csproj DefineConstants
#define NINJATRADER

#if NINJATRADER
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.CSharp.RuntimeBinder;
using NinjaTrader.Cbi;
using NinjaTrader.NinjaScript;
using QTSW2.Robot.Contracts;
using QTSW2.Robot.Core;
using QTSW2.Robot.Core.Diagnostics;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Real NinjaTrader API implementation for SIM adapter.
/// This partial class provides real NT API calls when running inside NinjaTrader.
/// </summary>
public sealed partial class NinjaTraderSimAdapter
{
    /// <summary>Compute protective status from broker orders. Broker-truth based.</summary>
    private ProtectiveStatus ComputeProtectiveStatusFromBroker(Account? account, string instrument, int brokerPositionQty)
    {
        if (brokerPositionQty == 0) return ProtectiveStatus.NONE;
        var orders = SnapshotAccountOrders(account);
        if (orders.Count == 0) return ProtectiveStatus.NONE;
        var instRoot = (instrument ?? "").Split(' ').FirstOrDefault() ?? instrument;
        int stopCount = 0, targetCount = 0, totalProtectiveQty = 0;
        foreach (var o in orders)
        {
            if (o.OrderState != OrderState.Working && o.OrderState != OrderState.Accepted) continue;
            if (!ExecutionInstrumentResolver.IsSameInstrument(o.Instrument?.MasterInstrument?.Name ?? o.Instrument?.FullName ?? "", instRoot)) continue;
            var tag = GetOrderTag(o);
            if (string.IsNullOrEmpty(tag) || !tag.StartsWith("QTSW2:", StringComparison.OrdinalIgnoreCase)) continue;
            if (tag.EndsWith(":STOP", StringComparison.OrdinalIgnoreCase)) { stopCount++; totalProtectiveQty += o.Quantity; }
            else if (tag.EndsWith(":TARGET", StringComparison.OrdinalIgnoreCase)) { targetCount++; totalProtectiveQty += o.Quantity; }
        }
        if (stopCount == 0 && targetCount == 0) return ProtectiveStatus.NONE;
        if (stopCount > 0 && targetCount == 0) return ProtectiveStatus.STOP_ONLY;
        if (stopCount == 0 && targetCount > 0) return ProtectiveStatus.TARGET_ONLY;
        if (totalProtectiveQty < brokerPositionQty) return ProtectiveStatus.QTY_MISMATCH;
        return ProtectiveStatus.BOTH_PRESENT;
    }

    /// <summary>Phase 4: Bootstrap snapshot callback. Gathers four views, builds BootstrapSnapshot, classifies, processes result.</summary>
    private void OnBootstrapSnapshotRequested(string instrument, BootstrapReason reason, DateTimeOffset utcNow)
    {
        if (_iea == null) return;
        var execInst = _iea.ExecutionInstrumentKey;
        int brokerQty = 0, brokerWorkingCount = 0;
        Account? account = null;
        try
        {
            if (!_iea.TryTransitionToSnapshooting(utcNow)) return;
            var exposure = ((IIEAOrderExecutor)this).GetBrokerCanonicalExposure(instrument);
            brokerQty = exposure.ReconciliationAbsQuantityTotal;
            account = _ntAccount as Account;
            var orders = SnapshotAccountOrders(account);
            if (orders.Count > 0)
            {
                var instRoot = (instrument ?? "").Split(' ').FirstOrDefault() ?? instrument;
                brokerWorkingCount = orders.Count(o =>
                    (o.OrderState == OrderState.Working || o.OrderState == OrderState.Accepted) &&
                    ExecutionInstrumentResolver.IsSameInstrument(o.Instrument?.MasterInstrument?.Name ?? o.Instrument?.FullName ?? "", instRoot));
            }
        }
        catch (Exception ex)
        {
            _log.Write(RobotEvents.EngineBase(utcNow, "", "RECOVERY_RECONSTRUCTION_ERROR", "ENGINE",
                new { instrument, error = ex.Message, iea_instance_id = _iea.InstanceId }));
            _iea.ProcessBootstrapResult(new BootstrapSnapshot { Instrument = instrument, UnownedLiveOrderCount = 999 }, utcNow);
            return;
        }
        var journalQty = SumOpenJournalForInstrument(instrument, execInst);
        var registry = _iea.GetRegistrySnapshotForRecovery();
        var runtime = _iea.GetRuntimeIntentSnapshotForRecovery();
        var protectiveStatus = ComputeProtectiveStatusFromBroker(account, instrument, brokerQty);
        var slotJournalShowsEntryStops = _hasSlotJournalWithEntryStopsForInstrumentCallback?.Invoke(instrument) ?? false;
        var snapshot = new BootstrapSnapshot
        {
            Instrument = instrument,
            BrokerPositionQty = brokerQty,
            BrokerWorkingOrderCount = brokerWorkingCount,
            JournalQty = journalQty,
            UnownedLiveOrderCount = registry.UnownedLiveCount,
            RegistrySnapshot = registry,
            JournalSnapshot = null,
            RuntimeIntentSnapshot = runtime,
            CaptureUtc = utcNow,
            SnapshotEpoch = 0,
            ProtectiveStatus = protectiveStatus,
            SlotJournalShowsEntryStopsExpected = slotJournalShowsEntryStops
        };
        _log.Write(RobotEvents.EngineBase(utcNow, "", "BOOTSTRAP_SNAPSHOT_CAPTURED", "ENGINE", new
        {
            instrument,
            broker_position_qty = brokerQty,
            broker_working_count = brokerWorkingCount,
            journal_qty = journalQty,
            unowned_live_count = registry.UnownedLiveCount,
            slot_journal_shows_entry_stops_expected = slotJournalShowsEntryStops,
            iea_instance_id = _iea.InstanceId
        }));
        var decision = _iea.ProcessBootstrapResult(snapshot, utcNow);
        if (decision == BootstrapDecision.ADOPT)
            _iea.RunBootstrapAdoption(instrument, utcNow);
    }

    /// <summary>P2 Phase 1: populate broker working rows for ownership attribution.</summary>
    private void EnrichRecoveryAttributionBrokerOrders(RecoveryAttributionSnapshot snap, string instrument)
    {
        if (_iea == null) return;
        var account = _ntAccount as Account;
        var orders = SnapshotAccountOrders(account);
        if (orders.Count == 0) return;
        var instRoot = (instrument ?? "").Split(' ').FirstOrDefault() ?? instrument ?? "";
        foreach (var o in orders)
        {
            if (o.OrderState != OrderState.Working && o.OrderState != OrderState.Accepted) continue;
            if (!ExecutionInstrumentResolver.IsSameInstrument(o.Instrument?.MasterInstrument?.Name ?? o.Instrument?.FullName ?? "", instRoot))
                continue;
            var id = o.OrderId.ToString() ?? "";
            var tag = GetOrderTag(o) ?? "";
            var row = new RecoveryAttributionBrokerOrderRow { BrokerOrderId = id, IsAggregatedTag = RobotOrderIds.IsAggregatedTag(tag) };
            if (row.IsAggregatedTag)
            {
                var agg = RobotOrderIds.DecodeAggregatedIntentIds(tag);
                if (agg != null) row.AggregatedIntentIds.AddRange(agg);
            }
            else if (!string.IsNullOrEmpty(tag) && tag.StartsWith("QTSW2:", StringComparison.OrdinalIgnoreCase))
                row.TagIntentId = RobotOrderIds.DecodeIntentId(tag);
            if (_iea.TryResolveByBrokerOrderId(id, out var re) && re != null)
                row.RegistryIntentId = re.IntentId;
            snap.BrokerWorking.Add(row);
        }
    }

    /// <summary>P2.6: Rebuild recovery policy inputs when a flatten command lacks enqueue seal (legacy / defensive).</summary>
    private bool TryBuildRecoveryDestructivePolicyInputFromLiveState(
        string instrument,
        string reason,
        object? context,
        DateTimeOffset utcNow,
        out DestructiveActionPolicyInput? policyInput,
        out StateOwnershipAttributionResult? attributionOut,
        out RecoveryAttributionSnapshot? snapOut)
    {
        policyInput = null;
        attributionOut = null;
        snapOut = null;
        if (_iea == null) return false;
        var execInst = _iea.ExecutionInstrumentKey;
        int brokerQty = 0, brokerWorkingCount = 0;
        try
        {
            var exposure = ((IIEAOrderExecutor)this).GetBrokerCanonicalExposure(instrument);
            brokerQty = exposure.ReconciliationAbsQuantityTotal;
            var account = _ntAccount as Account;
            var orders = SnapshotAccountOrders(account);
            if (orders.Count > 0)
            {
                var instRoot = (instrument ?? "").Split(' ').FirstOrDefault() ?? instrument;
                brokerWorkingCount = orders.Count(o =>
                    (o.OrderState == OrderState.Working || o.OrderState == OrderState.Accepted) &&
                    ExecutionInstrumentResolver.IsSameInstrument(o.Instrument?.MasterInstrument?.Name ?? o.Instrument?.FullName ?? "", instRoot));
            }
        }
        catch
        {
            return false;
        }
        var journalQty = SumOpenJournalForInstrument(instrument, execInst);
        var registry = _iea.GetRegistrySnapshotForRecovery();
        var runtime = _iea.GetRuntimeIntentSnapshotForRecovery();
        var result = RecoveryReconstructor.Reconstruct(instrument, brokerQty, brokerWorkingCount, journalQty, registry, runtime, reason);
        var actionKind = RecoveryPhase3DecisionRules.GetActionKind(result.Classification, result);
        if (actionKind != RecoveryActionKind.Flatten)
        {
            policyInput = new DestructiveActionPolicyInput
            {
                Source = DestructiveActionSource.RECOVERY,
                RecoveryReasonString = reason,
                ReconstructionActionKind = actionKind,
                ExecutionInstrumentKey = execInst,
                BrokerPositionQty = brokerQty,
                JournalOpenQtySum = journalQty
            };
            return true;
        }
        if (RecoveryOwnershipAttributionEvaluator.IsEmergencyInstrumentRecoveryTrigger(reason))
        {
            policyInput = new DestructiveActionPolicyInput
            {
                Source = DestructiveActionSource.RECOVERY,
                RecoveryReasonString = reason,
                ExplicitTrigger = DestructiveTriggerParser.TryParseStrictPrefix(reason),
                ReconstructionActionKind = actionKind,
                ExecutionInstrumentKey = execInst,
                BrokerPositionQty = brokerQty,
                JournalOpenQtySum = journalQty
            };
            return true;
        }
        string? triggerIntent = null;
        string? triggerBroker = null;
        try
        {
            if (context != null)
            {
                dynamic d = context;
                triggerIntent = d.intent_id?.ToString();
                triggerBroker = d.broker_order_id?.ToString();
            }
        }
        catch { /* best-effort */ }
        var gateEngaged = _instrumentMismatchGateEngaged?.Invoke(instrument) == true
                          || _instrumentMismatchGateEngaged?.Invoke(execInst) == true;
        var snap = _iea.BuildRecoveryAttributionSnapshot(brokerQty, brokerWorkingCount, journalQty, reason, triggerIntent, triggerBroker, gateEngaged);
        EnrichRecoveryAttributionBrokerOrders(snap, instrument);
        var attribution = RecoveryOwnershipAttributionEvaluator.EvaluateOwnershipAttributionForRecovery(snap);
        attributionOut = attribution;
        snapOut = snap;
        policyInput = new DestructiveActionPolicyInput
        {
            Source = DestructiveActionSource.RECOVERY,
            RecoveryReasonString = reason,
            ReconstructionActionKind = actionKind,
            GateEngagedForSymbol = gateEngaged,
            ExecutionInstrumentKey = execInst,
            BrokerPositionQty = brokerQty,
            JournalOpenQtySum = journalQty,
            Attribution = attribution,
            AttributionSnapshot = snap
        };
        return true;
    }

    /// <summary>P2.6.6: Shared template for pre-cancel policy and RequestFlatten final gate (broker qty refreshed in RequestFlatten).</summary>
    private bool TryBuildDestructivePolicyInputForFlattenCommand(NtFlattenInstrumentCommand cmd, DateTimeOffset utcNow, out DestructiveActionPolicyInput? input)
    {
        input = null;
        var execKey = _iea?.ExecutionInstrumentKey ?? "";
        if (cmd.HasRecoveryPolicySeal)
        {
            input = new DestructiveActionPolicyInput
            {
                Source = cmd.DestructiveSource,
                RecoveryReasonString = cmd.Reason,
                ExplicitTrigger = cmd.ExplicitPolicyTrigger,
                ExecutionInstrumentKey = execKey,
                RecoveryEnqueuePolicySealValid = true,
                RecoveryEnqueueAllowInstrument = cmd.RecoveryPolicySealAllowInstrument,
                RecoveryEnqueueReasonCode = cmd.RecoveryPolicySealCode,
                ReconstructionActionKind = RecoveryActionKind.Flatten
            };
            return true;
        }

        if (cmd.DestructiveSource != DestructiveActionSource.RECOVERY)
        {
            var built = new DestructiveActionPolicyInput
            {
                Source = cmd.DestructiveSource,
                RecoveryReasonString = cmd.Reason,
                ExplicitTrigger = cmd.ExplicitPolicyTrigger,
                ExecutionInstrumentKey = execKey,
                ReconstructionActionKind = RecoveryActionKind.Flatten
            };
            if (cmd.DestructiveSource == DestructiveActionSource.BOOTSTRAP)
                built.BootstrapAdministrativeFlatten = true;
            else if (cmd.DestructiveSource is DestructiveActionSource.MANUAL or DestructiveActionSource.COMMAND)
                built.ManualInstrumentFlatten = true;
            else if (cmd.DestructiveSource == DestructiveActionSource.FAIL_CLOSED)
                built.ExplicitTrigger = DestructiveTriggerReason.FAIL_CLOSED;
            else if (cmd.DestructiveSource == DestructiveActionSource.EMERGENCY)
                built.ExplicitTrigger = DestructiveTriggerReason.IEA_ENQUEUE_FAILURE;
            input = built;
            return true;
        }

        if (!TryBuildRecoveryDestructivePolicyInputFromLiveState(cmd.Instrument, cmd.Reason, null, utcNow, out var rebuilt, out _, out _))
        {
            input = new DestructiveActionPolicyInput
            {
                Source = DestructiveActionSource.RECOVERY,
                RecoveryReasonString = cmd.Reason,
                ExecutionInstrumentKey = execKey,
                ReconstructionActionKind = RecoveryActionKind.Halt
            };
            return true;
        }

        input = rebuilt;
        return true;
    }

    /// <summary>Phase 3: Recovery callback. Gathers broker/journal/registry/runtime data, runs reconstruction, processes result.</summary>
    private void OnRecoveryRequested(string instrument, string reason, object context, DateTimeOffset utcNow)
    {
        if (_iea == null) return;
        var instTrim = (instrument ?? "").Trim();
        var execInst = _iea.ExecutionInstrumentKey;
        if (!string.IsNullOrEmpty(instTrim) &&
            (ExecutionSafetyGate.IsInstrumentUnsafeLocked(instTrim) ||
             HardFailClosedExecutionModel.IsHardExecutionLocked(instTrim) ||
             !string.IsNullOrEmpty(execInst) && (ExecutionSafetyGate.IsInstrumentUnsafeLocked(execInst) ||
                                                 HardFailClosedExecutionModel.IsHardExecutionLocked(execInst))))
        {
            _log.Write(RobotEvents.EngineBase(utcNow, "", "RECOVERY_BLOCKED_INSTRUMENT_LOCKED", "ENGINE",
                new
                {
                    instrument = instTrim,
                    execution_instrument_key = execInst,
                    reason,
                    phase = "on_recovery_requested",
                    note = "Unmapped-fill hard lock active — use UnlockInstrument after operator review"
                }));
            return;
        }
        int brokerQty = 0, brokerWorkingCount = 0;
        try
        {
            var exposure = ((IIEAOrderExecutor)this).GetBrokerCanonicalExposure(instrument);
            brokerQty = exposure.ReconciliationAbsQuantityTotal;
            var account = _ntAccount as Account;
            var orders = SnapshotAccountOrders(account);
            if (orders.Count > 0)
            {
                var instRoot = (instrument ?? "").Split(' ').FirstOrDefault() ?? instrument;
                brokerWorkingCount = orders.Count(o =>
                    (o.OrderState == OrderState.Working || o.OrderState == OrderState.Accepted) &&
                    ExecutionInstrumentResolver.IsSameInstrument(o.Instrument?.MasterInstrument?.Name ?? o.Instrument?.FullName ?? "", instRoot));
            }
        }
        catch (Exception ex)
        {
            _log.Write(RobotEvents.EngineBase(utcNow, "", "RECOVERY_RECONSTRUCTION_ERROR", "ENGINE",
                new { instrument, error = ex.Message, iea_instance_id = _iea.InstanceId }));
            _iea.ProcessReconstructionResult(new ReconstructionResult { Instrument = instrument, Classification = ReconstructionClassification.UNSAFE_AMBIGUOUS_STATE }, utcNow);
            return;
        }
        var journalQty = SumOpenJournalForInstrument(instrument, execInst);
        var registry = _iea.GetRegistrySnapshotForRecovery();
        var runtime = _iea.GetRuntimeIntentSnapshotForRecovery();
        var result = RecoveryReconstructor.Reconstruct(instrument, brokerQty, brokerWorkingCount, journalQty, registry, runtime, reason);
        var actionKind = RecoveryPhase3DecisionRules.GetActionKind(result.Classification, result);

        string? triggerIntent = null;
        string? triggerBroker = null;
        try
        {
            if (context != null)
            {
                dynamic d = context;
                triggerIntent = d.intent_id?.ToString();
                triggerBroker = d.broker_order_id?.ToString();
            }
        }
        catch { /* best-effort context parse */ }

        var gateEngaged = _instrumentMismatchGateEngaged?.Invoke(instrument) == true
                          || _instrumentMismatchGateEngaged?.Invoke(execInst) == true;

        StateOwnershipAttributionResult? p2Attribution = null;
        if (actionKind == RecoveryActionKind.Flatten && !RecoveryOwnershipAttributionEvaluator.IsEmergencyInstrumentRecoveryTrigger(reason))
        {
            var snap = _iea.BuildRecoveryAttributionSnapshot(brokerQty, brokerWorkingCount, journalQty, reason, triggerIntent, triggerBroker, gateEngaged);
            EnrichRecoveryAttributionBrokerOrders(snap, instrument);
            var attribution = RecoveryOwnershipAttributionEvaluator.EvaluateOwnershipAttributionForRecovery(snap);
            p2Attribution = attribution;
            _log.Write(RobotEvents.EngineBase(utcNow, "", instrument, "STREAM_OWNERSHIP_AMBIGUITY_DETECTED", new
            {
                execution_instrument_key = execInst,
                implicated_streams = attribution.ImplicatedStreams,
                implicated_intent_ids = attribution.ImplicatedIntentIds,
                unattributed_order_ids = attribution.UnattributedBrokerOrderIds,
                attributable_scope = attribution.AttributableScope.ToString(),
                gate_state = gateEngaged ? "engaged" : "not_engaged",
                broker_position_qty = brokerQty,
                broker_working_count = brokerWorkingCount,
                iea_instance_id = _iea.InstanceId
            }));
            var recoveryPolicyInput = new DestructiveActionPolicyInput
            {
                Source = DestructiveActionSource.RECOVERY,
                RecoveryReasonString = reason,
                ReconstructionActionKind = RecoveryActionKind.Flatten,
                GateEngagedForSymbol = gateEngaged,
                ExecutionInstrumentKey = execInst,
                BrokerPositionQty = brokerQty,
                JournalOpenQtySum = journalQty,
                Attribution = attribution,
                AttributionSnapshot = snap
            };
            var recoveryPolicyDecision = DestructiveActionPolicy.EvaluateDestructiveActionPolicy(recoveryPolicyInput);
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "DESTRUCTIVE_ACTION_DECISION", state: "ENGINE", new
            {
                allowed = recoveryPolicyDecision.AllowInstrumentScope,
                reason = recoveryPolicyDecision.ReasonCode,
                scope = recoveryPolicyDecision.CancelScopeMode,
                policy_path = recoveryPolicyDecision.PolicyPath,
                phase = "on_recovery_requested_instrument_escalation",
                iea_instance_id = _iea.InstanceId
            }));
            if (!recoveryPolicyDecision.AllowInstrumentScope)
            {
                _log.Write(RobotEvents.EngineBase(utcNow, "", instrument, "INSTRUMENT_SCOPED_RECOVERY_BLOCKED_BY_ATTRIBUTION", new
                {
                    execution_instrument_key = execInst,
                    summary = attribution.Summary,
                    recommended_scope = attribution.RecommendedRecoveryScope.ToString(),
                    sibling_streams_preserved = true,
                    destructive_action_blocked = true,
                    policy_reason_code = recoveryPolicyDecision.ReasonCode,
                    iea_instance_id = _iea.InstanceId
                }));
                _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "DESTRUCTIVE_ACTION_BLOCKED", state: "ENGINE", new
                {
                    execution_instrument_key = execInst,
                    reason_code = recoveryPolicyDecision.ReasonCode,
                    phase = "on_recovery_requested_stream_containment",
                    iea_instance_id = _iea.InstanceId
                }));
                _iea.ProcessReconstructionResult(result, utcNow, new P2PostReconstructionDirective
                {
                    UseStreamContainmentInsteadOfInstrumentFlatten = true,
                    Attribution = attribution
                });
                return;
            }
            if (attribution.IsContradictory)
                _log.Write(RobotEvents.EngineBase(utcNow, "", instrument, "CROSS_STREAM_CONTRADICTION_DETECTED", new
                {
                    execution_instrument_key = execInst,
                    contradictions = attribution.Contradictions,
                    iea_instance_id = _iea.InstanceId
                }));
            _log.Write(RobotEvents.EngineBase(utcNow, "", instrument, "INSTRUMENT_ESCALATION_APPROVED", new
            {
                attribution_scope = attribution.AttributableScope.ToString(),
                reason,
                gate_engaged = gateEngaged,
                broker_position_qty = brokerQty,
                journal_open_qty = journalQty,
                execution_instrument_key = execInst,
                iea_instance_id = _iea.InstanceId
            }));
            _log.Write(RobotEvents.EngineBase(utcNow, "", instrument, "INSTRUMENT_SCOPED_RECOVERY_ALLOWED", new
            {
                execution_instrument_key = execInst,
                summary = attribution.Summary,
                attributable_scope = attribution.AttributableScope.ToString(),
                iea_instance_id = _iea.InstanceId
            }));
        }

        var decision = _iea.ProcessReconstructionResult(result, utcNow, null);
        if (decision == InstrumentExecutionAuthority.RecoveryDecision.FLATTEN)
        {
            var emergRec = RecoveryOwnershipAttributionEvaluator.IsEmergencyInstrumentRecoveryTrigger(reason);
            var meta = new RecoveryFlattenNtMetadata
            {
                HasSeal = true,
                SealAllowInstrument = true,
                SealReasonCode = emergRec ? "EMERGENCY_RECOVERY" : "recovery_attribution_allows_instrument",
                AttributionScope = emergRec ? "" : (p2Attribution?.AttributableScope.ToString() ?? ""),
                DestructiveSource = DestructiveActionSource.RECOVERY
            };
            EnqueueRecoveryFlattenNtCommand(instrument, reason, "RECOVERY_PHASE3", utcNow, meta);
        }
    }

    /// <summary>IEA recovery flatten callback (fixed arity). Consumes staged P2.6 metadata from IEA.</summary>
    private void OnRecoveryFlattenRequestedFromIeCallback(string instrument, string reason, string callerContext, DateTimeOffset utcNow)
    {
        RecoveryFlattenNtMetadata? meta = null;
        _iea?.TryConsumeRecoveryFlattenNtMetadata(out meta);
        EnqueueRecoveryFlattenNtCommand(instrument, reason, callerContext, utcNow, meta);
    }

    /// <summary>Phase 3: Enqueue NtFlattenInstrumentCommand with P2.6 policy seal / source.</summary>
    private void EnqueueRecoveryFlattenNtCommand(string instrument, string reason, string callerContext, DateTimeOffset utcNow, RecoveryFlattenNtMetadata? meta)
    {
        var src = meta?.DestructiveSource ?? DestructiveActionSource.RECOVERY;
        var prefix = src == DestructiveActionSource.BOOTSTRAP ? "BOOTSTRAP" : "RECOVERY";
        var cid = $"{prefix}:{instrument}:{utcNow:yyyyMMddHHmmssfff}";
        var hasSeal = meta?.HasSeal ?? false;
        var sealAllow = meta?.SealAllowInstrument ?? false;
        var cmd = new NtFlattenInstrumentCommand(
            cid,
            null,
            instrument,
            reason,
            utcNow,
            destructiveSource: src,
            explicitPolicyTrigger: null,
            allowAccountWideCancelFallback: false,
            hasRecoveryPolicySeal: hasSeal,
            recoveryPolicySealAllowInstrument: sealAllow,
            recoveryPolicySealCode: meta?.SealReasonCode,
            recoveryPolicySealAttributionScope: meta?.AttributionScope);
        EnqueueNtActionInternal(cmd);
    }

    void INtActionExecutor.ExecuteFlattenInstrument(NtFlattenInstrumentCommand cmd)
    {
        var utcNow = cmd.UtcNow;
        var execKey = _iea?.ExecutionInstrumentKey ?? "";
        var coordInst = GetFlattenCoordinationInstanceId();
        var coordAcct = GetCoordinationAccountName();
        var canonicalKey = BrokerPositionResolver.NormalizeCanonicalKey(cmd.Instrument);
        if (!string.IsNullOrEmpty(canonicalKey) &&
            !FlattenCoordinationTracker.Shared.IsActiveFlattenOwner(coordAcct, cmd.Instrument, coordInst))
        {
            FlattenCoordinationTracker.Shared.TryPeekKey(coordAcct, cmd.Instrument, out var ow, out var epi, out _);
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "FLATTEN_SECONDARY_INSTANCE_SKIPPED", state: "ENGINE",
                new
                {
                    account = coordAcct,
                    canonical_broker_key = canonicalKey,
                    owner_instance_id = ow,
                    current_instance_id = coordInst,
                    episode_id = epi,
                    host_chart_instrument = GetHostChartInstrumentName(),
                    correlation_id = cmd.CorrelationId,
                    reason = "execute_phase_not_coordination_owner",
                    critical_flatten = IsCriticalFlattenCommand(cmd)
                }));
            if (IsCriticalFlattenCommand(cmd))
            {
                _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "FLATTEN_COORDINATION_SKIP_ESCALATION", state: "CRITICAL",
                    new
                    {
                        account = coordAcct,
                        canonical_broker_key = canonicalKey,
                        correlation_id = cmd.CorrelationId,
                        instrument = cmd.Instrument,
                        phase = "execute_flatten_instrument",
                        note = "Non-owner skipped critical flatten execute — escalating block"
                    }));
                _blockInstrumentCallback?.Invoke(cmd.Instrument ?? "", utcNow, "FLATTEN_EXECUTE_NON_OWNER_CRITICAL");
                ScheduleCriticalFlattenCoordinationRetryIfNeeded(cmd, "execute_flatten_instrument");
            }
            return;
        }

        if (!TryExecutionSafetyFlattenGuard(cmd.Instrument ?? "", cmd.IntentId, utcNow, "EXECUTE_FLATTEN_INSTRUMENT",
                cmd.CorrelationId, out _))
        {
            FlattenCoordinationTracker.Shared.NotifyFlattenAborted(coordAcct, cmd.Instrument ?? "", coordInst, utcNow);
            return;
        }

        var preTrigger = DestructiveTriggerParser.Resolve(cmd.ExplicitPolicyTrigger, cmd.Reason);
        _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "DESTRUCTIVE_ACTION_REQUESTED", state: "ENGINE", new
        {
            execution_instrument_key = execKey,
            correlation_id = cmd.CorrelationId,
            source = cmd.DestructiveSource.ToString(),
            trigger = preTrigger.ToString(),
            instrument = cmd.Instrument,
            phase = "execute_flatten_instrument_entry"
        }));

        TryBuildDestructivePolicyInputForFlattenCommand(cmd, utcNow, out var policyTemplate);
        var decision = DestructiveActionPolicy.EvaluateDestructiveActionPolicy(policyTemplate!);
        _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "DESTRUCTIVE_ACTION_DECISION", state: "ENGINE", new
        {
            allowed = decision.AllowInstrumentScope,
            reason = decision.ReasonCode,
            scope = decision.CancelScopeMode,
            policy_path = decision.PolicyPath,
            phase = "pre_cancel_execute_flatten_instrument",
            correlation_id = cmd.CorrelationId
        }));

        int brokerQtySigned = 0, brokerQty = 0, journalQty = 0;
        try
        {
            var exposurePrecheck = ((IIEAOrderExecutor)this).GetBrokerCanonicalExposure(cmd.Instrument);
            brokerQty = exposurePrecheck.ReconciliationAbsQuantityTotal;
            brokerQtySigned = exposurePrecheck.Legs.Sum(l => l.SignedQuantity);
            journalQty = SumOpenJournalForInstrument(cmd.Instrument, execKey);
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "FLATTEN_BROKER_EXPOSURE_PRECHECK", state: "ENGINE",
                new
                {
                    correlation_id = cmd.CorrelationId,
                    execution_instrument_key = execKey,
                    logical_instrument = cmd.Instrument,
                    canonical_broker_key = exposurePrecheck.CanonicalKey,
                    reconciliation_abs_total = exposurePrecheck.ReconciliationAbsQuantityTotal,
                    broker_exposure_aggregated = exposurePrecheck.IsAggregatedMultipleRows,
                    broker_position_rows = BrokerPositionResolver.ToDiagnosticRows(exposurePrecheck),
                    host_chart_instrument = (_ntInstrument as Instrument)?.FullName,
                    note = "Canonical exposure (same model as reconciliation) before cancel+flatten"
                }));
        }
        catch { /* observability only */ }

        if (!decision.AllowInstrumentScope)
        {
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "DESTRUCTIVE_ACTION_BLOCKED", state: "ENGINE", new
            {
                execution_instrument_key = execKey,
                correlation_id = cmd.CorrelationId,
                trigger_source = cmd.DestructiveSource.ToString(),
                reason_code = decision.ReasonCode,
                policy_path = decision.PolicyPath,
                is_emergency = decision.IsEmergency,
                note = "P2.6: instrument flatten denied by centralized policy"
            }));
            FlattenCoordinationTracker.Shared.NotifyFlattenAborted(coordAcct, cmd.Instrument, coordInst, DateTimeOffset.UtcNow);
            throw new InvalidOperationException($"P2.6: destructive policy denied instrument flatten ({decision.ReasonCode})");
        }

        try
        {
            _log.Write(RobotEvents.ExecutionBase(utcNow, cmd.IntentId ?? "", cmd.Instrument, "FLATTEN_REQUESTED", new { correlation_id = cmd.CorrelationId, reason = cmd.Reason }));
            if (_keyEventWriter != null && _keyEventWriter.TryShouldEmitFlattenPhase("FLATTEN_REQUESTED", cmd.Instrument ?? "", cmd.CorrelationId))
            {
                _keyEventWriter.AppendKeyEvent(utcNow, "FLATTEN_REQUESTED", cmd.Instrument?.Trim(), null, cmd.Reason,
                    new Dictionary<string, object?> { ["correlation_id"] = cmd.CorrelationId });
            }
            // Cancel-then-flatten: cancel robot-owned working orders first (P2.6: scoped to instrument unless explicit set / fallback)
            CancelRobotOwnedWorkingOrdersReal(default, utcNow, cmd.Instrument, cmd.ExplicitCancelBrokerOrderIds, cmd.AllowAccountWideCancelFallback, cmd.CorrelationId);
            try
            {
                var journalAfterCancel = SumOpenJournalForInstrument(cmd.Instrument, execKey);
                if (journalAfterCancel != journalQty)
                {
                    _log.Write(RobotEvents.EngineBase(utcNow, "", cmd.Instrument, "DESTRUCTIVE_POLICY_PREPOST_DRIFT", new
                    {
                        drift_kind = "journal_open_qty_after_cancel_phase",
                        precheck_journal = journalQty,
                        post_cancel_journal = journalAfterCancel,
                        correlation_id = cmd.CorrelationId,
                        note = "P2.6.7: journal sum changed between pre-check and post-cancel (RequestFlatten still uses final broker read)"
                    }));
                }
            }
            catch { /* observability */ }

            FlattenResult result;
            if (_useInstrumentExecutionAuthority && _iea != null)
            {
                var flattenCtx = FlattenPolicyExecutionContext.FromDestructivePolicyInput(policyTemplate!);
                flattenCtx.PrecheckBrokerPositionQtyAbs = brokerQty;
                flattenCtx.PrecheckBrokerQtySigned = brokerQtySigned;
                flattenCtx.PrecheckJournalOpenQtySum = journalQty;
                flattenCtx.PrecheckCorrelationId = cmd.CorrelationId;
                flattenCtx.PrecheckPolicyReasonCode = decision.ReasonCode;
                flattenCtx.PrecheckAllowInstrument = decision.AllowInstrumentScope;
                result = _iea.RequestFlatten(cmd.Instrument, cmd.Reason, cmd.IntentId ?? "NT_FLATTEN", cmd.UtcNow, flattenCtx);
            }
            else
            {
                var token = NtDestructivePolicyAlreadyAppliedToken.ForNtFlattenCommand(cmd, decision.ReasonCode);
                result = FlattenIntentReal(cmd.IntentId ?? "NT_FLATTEN", cmd.Instrument, cmd.UtcNow, policyPrechecked: token, cmd.DestructiveSource, cmd.ExplicitPolicyTrigger);
            }
            _log.Write(RobotEvents.ExecutionBase(cmd.UtcNow, cmd.IntentId ?? "", cmd.Instrument, "FLATTEN_SUBMITTED", new { correlation_id = cmd.CorrelationId, success = result.Success }));
            if (_keyEventWriter != null && _keyEventWriter.TryShouldEmitFlattenPhase("FLATTEN_SUBMITTED", cmd.Instrument ?? "", cmd.CorrelationId))
            {
                _keyEventWriter.AppendKeyEvent(cmd.UtcNow, "FLATTEN_SUBMITTED", cmd.Instrument?.Trim(), null, null,
                    new Dictionary<string, object?> { ["correlation_id"] = cmd.CorrelationId, ["success"] = result.Success });
            }
            if (!result.Success) throw new InvalidOperationException($"Flatten failed: {result.ErrorMessage}");
            _log.Write(RobotEvents.EngineBase(cmd.UtcNow, tradingDate: "", eventType: "FLATTEN_ORDER_SUBMITTED", state: "ENGINE",
                new
                {
                    correlation_id = cmd.CorrelationId,
                    instrument = cmd.Instrument,
                    note = "Flatten path completed submit phase (per-leg submits may be logged in IEA); broker flat not confirmed until FLATTEN_BROKER_FLAT_CONFIRMED"
                }));
            FlattenCoordinationTracker.Shared.NotifyFlattenSubmitted(coordAcct, cmd.Instrument, coordInst, cmd.UtcNow);
            RegisterPendingFlattenVerification(cmd.Instrument, cmd.CorrelationId, cmd.UtcNow);
            ExecutionSafetyGate.RecordFlattenSubmitted(cmd.Instrument ?? "", brokerQty, cmd.UtcNow);
        }
        catch
        {
            FlattenCoordinationTracker.Shared.NotifyFlattenAborted(coordAcct, cmd.Instrument, coordInst, DateTimeOffset.UtcNow);
            throw;
        }
    }

    void INtActionExecutor.ExecuteSubmitEntryIntent(NtSubmitEntryIntentCommand ntCmd)
    {
        var cmd = ntCmd.Command;
        var utcNow = cmd.TimestampUtc;
        var instrument = cmd.Instrument ?? cmd.ExecutionInstrument ?? "";
        var execInst = cmd.ExecutionInstrument ?? instrument;
        var longIntentId = cmd.LongIntentId;
        var shortIntentId = cmd.ShortIntentId;
        var qty = cmd.Quantity ?? 1;
        var maxQty = cmd.MaxQuantity ?? qty;
        var canonical = cmd.CanonicalInstrument ?? instrument;
        var ocoGroup = cmd.OcoGroup;

        if (string.IsNullOrEmpty(longIntentId) || string.IsNullOrEmpty(shortIntentId) ||
            !cmd.BreakLong.HasValue || !cmd.BreakShort.HasValue ||
            !cmd.LongStopPrice.HasValue || !cmd.ShortStopPrice.HasValue)
        {
            _log.Write(RobotEvents.ExecutionBase(utcNow, "", instrument, "SUBMIT_ENTRY_INTENT_SKIPPED",
                new { reason = "Missing required fields", longIntentId, shortIntentId }));
            return;
        }

        var longIntent = new Intent(
            cmd.TradingDate ?? "",
            cmd.Stream ?? "",
            instrument,
            execInst,
            cmd.Session ?? "",
            cmd.SlotTimeChicago ?? "",
            "Long",
            cmd.BreakLong.Value,
            cmd.LongStopPrice.Value,
            cmd.LongTargetPrice ?? cmd.LongStopPrice.Value,
            cmd.LongBeTrigger ?? cmd.LongStopPrice.Value,
            utcNow,
            "SUBMIT_ENTRY_INTENT_LONG");

        var shortIntent = new Intent(
            cmd.TradingDate ?? "",
            cmd.Stream ?? "",
            instrument,
            execInst,
            cmd.Session ?? "",
            cmd.SlotTimeChicago ?? "",
            "Short",
            cmd.BreakShort.Value,
            cmd.ShortStopPrice.Value,
            cmd.ShortTargetPrice ?? cmd.ShortStopPrice.Value,
            cmd.ShortBeTrigger ?? cmd.ShortStopPrice.Value,
            utcNow,
            "SUBMIT_ENTRY_INTENT_SHORT");

        RegisterIntent(longIntent);
        RegisterIntent(shortIntent);
        RegisterIntentPolicy(longIntentId, qty, maxQty, canonical, execInst, "EXECUTION_COMMAND");
        RegisterIntentPolicy(shortIntentId, qty, maxQty, canonical, execInst, "EXECUTION_COMMAND");

        var longRes = SubmitStopEntryOrder(longIntentId, execInst, "Long", cmd.BreakLong.Value, qty, ocoGroup, utcNow);
        var shortRes = SubmitStopEntryOrder(shortIntentId, execInst, "Short", cmd.BreakShort.Value, qty, ocoGroup, utcNow);

        _log.Write(RobotEvents.ExecutionBase(utcNow, longIntentId, execInst, "SUBMIT_ENTRY_INTENT_COMPLETED",
            new { long_success = longRes.Success, short_success = shortRes.Success }));
    }

    void INtActionExecutor.ExecuteSubmitMarketReentry(NtSubmitMarketReentryCommand ntCmd)
    {
        var cmd = ntCmd.Command;
        var utcNow = cmd.TimestampUtc;
        var instrument = cmd.ExecutionInstrument ?? cmd.Instrument ?? "";
        var execInst = string.IsNullOrEmpty(instrument) ? "" : instrument.Trim().ToUpperInvariant();
        // Align with StreamStateMachine CheckMarketOpenReentry (originalEntry.Direction ?? "Long") so ComputeIntentId matches journal precompute.
        var direction = string.IsNullOrEmpty(cmd.Direction) ? "Long" : cmd.Direction!;
        var quantity = cmd.Quantity;

        if (string.IsNullOrEmpty(execInst) || quantity <= 0)
        {
            _log.Write(RobotEvents.ExecutionBase(utcNow, cmd.ReentryIntentId ?? "", execInst, "SUBMIT_MARKET_REENTRY_SKIPPED",
                new { reason = "Missing required fields", reentryIntentId = cmd.ReentryIntentId, execInst, direction, quantity }));
            ReleaseMarketReentryExecutionLatch(cmd.ReentryIntentId ?? "", execInst, utcNow, "SUBMIT_MARKET_REENTRY_INVALID", ntCmd.CorrelationId);
            _onReentrySubmitCompletedCallback?.Invoke(cmd.ReentryIntentId ?? "", utcNow, false, "Missing required fields");
            return;
        }

        var reentryIntent = new Intent(
            cmd.TradingDate ?? "",
            cmd.Stream ?? "",
            instrument,
            execInst,
            cmd.Session ?? "",
            cmd.SlotTimeChicago ?? "",
            direction,
            cmd.EntryPrice,
            cmd.StopPrice,
            cmd.TargetPrice,
            cmd.BeTrigger,
            utcNow,
            "SUBMIT_MARKET_REENTRY");
        var canonicalIntentId = reentryIntent.ComputeIntentId();
        if (!string.IsNullOrEmpty(cmd.ReentryIntentId) &&
            !string.Equals(cmd.ReentryIntentId, canonicalIntentId, StringComparison.Ordinal))
        {
            _log.Write(RobotEvents.ExecutionBase(utcNow, canonicalIntentId, execInst, "SUBMIT_MARKET_REENTRY_INTENT_ID_CANONICAL_OVERRIDE",
                new
                {
                    command_reentry_intent_id = cmd.ReentryIntentId,
                    canonical_intent_id = canonicalIntentId,
                    note = "Command/journal id did not match Intent.ComputeIntentId — using canonical id for execution"
                }));
        }

        RegisterIntent(reentryIntent);
        RegisterIntentPolicy(canonicalIntentId, quantity, quantity, instrument, execInst, "EXECUTION_COMMAND");

        var result = SubmitMarketReentryOrder(canonicalIntentId, execInst, direction, quantity, utcNow);
        if (!result.Success)
            ReleaseMarketReentryExecutionLatch(canonicalIntentId, execInst, utcNow, "SUBMIT_MARKET_REENTRY_FAILED", ntCmd.CorrelationId);
        _log.Write(RobotEvents.ExecutionBase(utcNow, canonicalIntentId, execInst, "SUBMIT_MARKET_REENTRY_COMPLETED",
            new { success = result.Success, error = result.ErrorMessage }));
        _onReentrySubmitCompletedCallback?.Invoke(canonicalIntentId, utcNow, result.Success, result.ErrorMessage);
    }

    private void RegisterPendingFlattenVerification(string instrumentKey, string correlationId, DateTimeOffset utcNow)
    {
        var deadline = utcNow.AddSeconds(FLATTEN_VERIFY_WINDOW_SEC);
        var coordAcct = GetCoordinationAccountName();
        var coordInst = GetFlattenCoordinationInstanceId();
        FlattenCoordinationTracker.Shared.TryPeekEpisodeForOwner(coordAcct, instrumentKey, coordInst, out var eid, out _, out _);
        _pendingFlattenVerifications[instrumentKey] = (utcNow, deadline, correlationId, eid);
    }

    private List<(string InstrumentKey, string CorrelationId, string? EpisodeId)> RetirePendingFlattenVerificationsForLateSessionClose(
        string exposureInstrument,
        string originalIntentId,
        BrokerCanonicalExposure exposure,
        DateTimeOffset utcNow)
    {
        var retired = new List<(string InstrumentKey, string CorrelationId, string? EpisodeId)>();
        if (string.IsNullOrWhiteSpace(exposureInstrument))
            return retired;

        var coordAcct = GetCoordinationAccountName();
        var coordInst = GetFlattenCoordinationInstanceId();
        var hostChart = (_ntInstrument as Instrument)?.FullName;

        foreach (var kvp in _pendingFlattenVerifications.ToArray())
        {
            var instrumentKey = kvp.Key;
            if (!ExecutionInstrumentResolver.IsSameInstrument(instrumentKey, exposureInstrument) &&
                !string.Equals(BrokerPositionResolver.NormalizeCanonicalKey(instrumentKey),
                    BrokerPositionResolver.NormalizeCanonicalKey(exposureInstrument), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!_pendingFlattenVerifications.TryRemove(instrumentKey, out var pending))
                continue;

            FlattenCoordinationTracker.Shared.ProcessVerifyWindow(coordAcct, instrumentKey, coordInst, 0, utcNow, out _, out var episodeAfter, out _);
            var episodeId = string.IsNullOrEmpty(episodeAfter) ? pending.EpisodeId : episodeAfter;
            retired.Add((instrumentKey, pending.CorrelationId, episodeId));

            _log.Write(RobotEvents.ExecutionBase(utcNow, originalIntentId, instrumentKey, "FLATTEN_VERIFY_RETIRED_LATE_SESSION_CLOSE_CONFIRMED",
                new
                {
                    correlation_id = pending.CorrelationId,
                    original_intent_id = originalIntentId,
                    logical_instrument = instrumentKey,
                    execution_instrument_key = exposureInstrument,
                    canonical_broker_key = exposure.CanonicalKey,
                    reconciliation_abs_remaining = exposure.ReconciliationAbsQuantityTotal,
                    broker_position_rows = BrokerPositionResolver.ToDiagnosticRows(exposure),
                    account = coordAcct,
                    owner_instance_id = coordInst,
                    current_instance_id = coordInst,
                    episode_id = episodeId,
                    host_chart_instrument = hostChart,
                    note = "Late session-close flatten has broker-flat proof; retiring stale verifier before market-open reentry can be mistaken as old exposure."
                }));

            TryAppendLateSessionCloseFlattenConfirmedKeyEvent(utcNow, instrumentKey, pending.CorrelationId, episodeId, originalIntentId);
            _flattenCompletionDecisionLog.Append(new FlattenCompletionDecisionRecord
            {
                Utc = utcNow,
                Instrument = instrumentKey,
                CanonicalBrokerKey = exposure.CanonicalKey,
                ReconciliationAbsRemaining = 0,
                Proof = FlattenCompletionDecisionRecord.ProofCanonicalAbsZero,
                CorrelationId = pending.CorrelationId,
                EpisodeId = episodeId,
                Source = "LATE_SESSION_CLOSE_CONFIRM"
            });
        }

        return retired;
    }

    private void TryAppendLateSessionCloseFlattenConfirmedKeyEvent(
        DateTimeOffset utcNow,
        string instrumentKey,
        string correlationId,
        string? episodeId,
        string originalIntentId)
    {
        if (_keyEventWriter == null || string.IsNullOrWhiteSpace(instrumentKey) || string.IsNullOrWhiteSpace(correlationId))
            return;

        if (!_keyEventWriter.TryShouldEmitFlattenPhase("FLATTEN_CONFIRMED", instrumentKey, correlationId))
            return;

        _keyEventWriter.AppendKeyEvent(utcNow, "FLATTEN_CONFIRMED", instrumentKey, null,
            "LATE_SESSION_CLOSE_CONFIRM",
            new Dictionary<string, object?>
            {
                ["correlation_id"] = correlationId,
                ["episode_id"] = episodeId,
                ["original_intent_id"] = originalIntentId
            });
    }

    partial void OnVerifyPendingFlattens()
    {
        var utcNow = DateTimeOffset.UtcNow;
        if (_ntAccount as Account == null) return;

        var coordAcct = GetCoordinationAccountName();
        var coordInst = GetFlattenCoordinationInstanceId();
        var hostChart = (_ntInstrument as Instrument)?.FullName;

        var toRemove = new List<string>();
        foreach (var kvp in _pendingFlattenVerifications.ToArray())
        {
            var instrumentKey = kvp.Key;
            var (requestedUtc, deadline, correlationId, pendingEpisodeId) = kvp.Value;
            if (utcNow < deadline) continue;

            var canonicalForKey = BrokerPositionResolver.NormalizeCanonicalKey(instrumentKey);
            if (!string.IsNullOrEmpty(canonicalForKey) &&
                !FlattenCoordinationTracker.Shared.IsActiveFlattenOwner(coordAcct, instrumentKey, coordInst))
            {
                FlattenCoordinationTracker.Shared.TryPeekKey(coordAcct, instrumentKey, out var ow, out var epi, out _);
                _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "FLATTEN_SECONDARY_INSTANCE_SKIPPED", state: "ENGINE",
                    new
                    {
                        account = coordAcct,
                        canonical_broker_key = canonicalForKey,
                        owner_instance_id = ow,
                        current_instance_id = coordInst,
                        episode_id = epi,
                        host_chart_instrument = hostChart,
                        correlation_id = correlationId,
                        reason = "verify_window_non_owner_pending_dropped"
                    }));
                toRemove.Add(instrumentKey);
                continue;
            }

            var exposure = ((IIEAOrderExecutor)this).GetBrokerCanonicalExposure(instrumentKey);
            var remainingAbs = exposure.ReconciliationAbsQuantityTotal;

            if (FlattenCompletionAuthority.IsOfficialFlattenComplete(exposure))
            {
                FlattenCoordinationTracker.Shared.TryPeekEpisodeForOwner(coordAcct, instrumentKey, coordInst, out _, out var vac0, out _);
                var pv0 = FlattenCoordinationTracker.Shared.ProcessVerifyWindow(coordAcct, instrumentKey, coordInst, 0, utcNow, out _, out _, out _);
                if (pv0 != FlattenVerifyProcessOutcome.ResolvedFlat)
                    continue;

                _log.Write(RobotEvents.ExecutionBase(utcNow, "", instrumentKey, "FLATTEN_VERIFY_PASS",
                    new
                    {
                        correlation_id = correlationId,
                        instrument = instrumentKey,
                        episode_id = pendingEpisodeId,
                        owner_instance_id = coordInst,
                        verify_attempt_count = vac0
                    }));
                _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "FLATTEN_BROKER_FLAT_CONFIRMED", state: "ENGINE",
                    new
                    {
                        account = coordAcct,
                        correlation_id = correlationId,
                        logical_instrument = instrumentKey,
                        canonical_broker_key = exposure.CanonicalKey,
                        reconciliation_abs_remaining = remainingAbs,
                        broker_position_rows = BrokerPositionResolver.ToDiagnosticRows(exposure),
                        host_chart_instrument = hostChart,
                        owner_instance_id = coordInst,
                        current_instance_id = coordInst,
                        episode_id = pendingEpisodeId,
                        flatten_completion_authority = nameof(FlattenCompletionAuthority)
                    }));
                if (_keyEventWriter != null && _keyEventWriter.TryShouldEmitFlattenPhase("FLATTEN_CONFIRMED", instrumentKey, correlationId))
                {
                    _keyEventWriter.AppendKeyEvent(utcNow, "FLATTEN_CONFIRMED", instrumentKey, null,
                        nameof(FlattenCompletionAuthority),
                        new Dictionary<string, object?> { ["correlation_id"] = correlationId, ["episode_id"] = pendingEpisodeId });
                }
                _flattenCompletionDecisionLog.Append(new FlattenCompletionDecisionRecord
                {
                    Utc = utcNow,
                    Instrument = instrumentKey,
                    CanonicalBrokerKey = exposure.CanonicalKey,
                    ReconciliationAbsRemaining = 0,
                    Proof = FlattenCompletionDecisionRecord.ProofCanonicalAbsZero,
                    CorrelationId = correlationId,
                    EpisodeId = pendingEpisodeId,
                    Source = "ADAPTER_VERIFY"
                });
                try
                {
                    var uDone = _executionJournal.CompleteOpenUntrackedFillRecoveryForInstrument(
                        instrumentKey,
                        string.IsNullOrEmpty(canonicalForKey) ? null : canonicalForKey,
                        utcNow,
                        CompletionReasons.UNTRACKED_FILL_RECOVERY_FLAT);
                    if (uDone > 0)
                    {
                        _log.Write(RobotEvents.EngineBase(utcNow, "", "UNTRACKED_FILL_RECOVERY_JOURNAL_COMPLETED_VERIFY", "ENGINE",
                            new { instrument = instrumentKey, count = uDone, correlation_id = correlationId }));
                    }
                }
                catch (Exception ex)
                {
                    _log.Write(RobotEvents.EngineBase(utcNow, "", "UNTRACKED_FILL_RECOVERY_JOURNAL_COMPLETE_ERROR", "ENGINE",
                        new { instrument = instrumentKey, error = ex.Message, exception_type = ex.GetType().Name }));
                }
                toRemove.Add(instrumentKey);
                continue;
            }

            var pv = FlattenCoordinationTracker.Shared.ProcessVerifyWindow(coordAcct, instrumentKey, coordInst, remainingAbs, utcNow, out _, out var epAfter, out var worsened);
            FlattenCoordinationTracker.Shared.TryPeekEpisodeForOwner(coordAcct, instrumentKey, coordInst, out _, out var verifyAttemptCount, out var consecNonzero);

            _log.Write(RobotEvents.ExecutionBase(utcNow, "", instrumentKey, "FLATTEN_VERIFY_FAIL",
                new
                {
                    correlation_id = correlationId,
                    instrument = instrumentKey,
                    position_qty = remainingAbs,
                    verify_attempt_count = verifyAttemptCount,
                    consecutive_nonzero_verifies = consecNonzero
                }));

            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "FLATTEN_BROKER_POSITION_REMAINS", state: "ENGINE",
                new
                {
                    account = coordAcct,
                    correlation_id = correlationId,
                    logical_instrument = instrumentKey,
                    canonical_broker_key = exposure.CanonicalKey,
                    reconciliation_abs_remaining = remainingAbs,
                    broker_exposure_aggregated = exposure.IsAggregatedMultipleRows,
                    broker_position_rows = BrokerPositionResolver.ToDiagnosticRows(exposure),
                    host_chart_instrument = hostChart,
                    owner_instance_id = coordInst,
                    current_instance_id = coordInst,
                    episode_id = string.IsNullOrEmpty(epAfter) ? pendingEpisodeId : epAfter,
                    verify_attempt_count = verifyAttemptCount,
                    consecutive_nonzero_verifies = consecNonzero,
                    note = "Post-flatten verification: canonical exposure still non-zero; recovery/mismatch not cleared by submit alone"
                }));

            switch (pv)
            {
                case FlattenVerifyProcessOutcome.FailedPersistent:
                    FlattenCoordinationTracker.Shared.MarkFailedPersistent(coordAcct, instrumentKey, coordInst, utcNow);
                    _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "FLATTEN_FAILED_PERSISTENT", state: "CRITICAL",
                        new
                        {
                            account = coordAcct,
                            correlation_id = correlationId,
                            instrument = instrumentKey,
                            canonical_broker_key = exposure.CanonicalKey,
                            reconciliation_abs_remaining = remainingAbs,
                            verify_attempt_count = verifyAttemptCount,
                            episode_id = pendingEpisodeId,
                            owner_instance_id = coordInst,
                            metrics_flatten_failed_persistent_total = FlattenCoordinationTracker.Shared.Metrics.FlattenFailedPersistentTotal,
                            note = "Flatten verify failed after max coordinator retries - fail-closed"
                        }));
                    _standDownStreamCallback?.Invoke("", utcNow, $"FLATTEN_FAILED_PERSISTENT:{instrumentKey}");
                    _blockInstrumentCallback?.Invoke(instrumentKey, utcNow, $"FLATTEN_FAILED_PERSISTENT:{instrumentKey}");
                    toRemove.Add(instrumentKey);
                    break;

                case FlattenVerifyProcessOutcome.DebounceExtendWindow:
                    _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "FLATTEN_VERIFY_STILL_OPEN_DEBOUNCED", state: "ENGINE",
                        new
                        {
                            account = coordAcct,
                            canonical_broker_key = exposure.CanonicalKey,
                            reconciliation_abs_remaining = remainingAbs,
                            consecutive_nonzero_verifies = consecNonzero,
                            verify_attempt_count = verifyAttemptCount,
                            owner_instance_id = coordInst,
                            episode_id = pendingEpisodeId,
                            metrics_flatten_verify_debounced_total = FlattenCoordinationTracker.Shared.Metrics.FlattenVerifyDebouncedTotal
                        }));
                    _pendingFlattenVerifications[instrumentKey] = (requestedUtc, utcNow.AddSeconds(FLATTEN_VERIFY_WINDOW_SEC), correlationId, pendingEpisodeId);
                    break;

                case FlattenVerifyProcessOutcome.EnqueueRetryFlatten:
                    if (worsened)
                    {
                        _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "FLATTEN_VERIFY_EXPOSURE_WORSENED", state: "ENGINE",
                            new
                            {
                                account = coordAcct,
                                canonical_broker_key = exposure.CanonicalKey,
                                reconciliation_abs_remaining = remainingAbs,
                                owner_instance_id = coordInst,
                                current_instance_id = coordInst,
                                episode_id = pendingEpisodeId,
                                metrics_flatten_verify_worsened_total = FlattenCoordinationTracker.Shared.Metrics.FlattenVerifyWorsenedTotal
                            }));
                    }

                    var newCid = $"{correlationId}:V{verifyAttemptCount}";
                    var retryCmd = new NtFlattenInstrumentCommand(
                        newCid,
                        null,
                        instrumentKey,
                        $"VERIFY_FAIL_RETRY_{verifyAttemptCount}",
                        utcNow,
                        DestructiveActionSource.MANUAL,
                        DestructiveTriggerReason.MANUAL,
                        allowAccountWideCancelFallback: false,
                        hasRecoveryPolicySeal: false,
                        recoveryPolicySealAllowInstrument: false,
                        recoveryPolicySealCode: null,
                        recoveryPolicySealAttributionScope: null,
                        explicitCancelBrokerOrderIds: null,
                        isVerifyRetryFlatten: true);
                    EnqueueNtActionInternal(retryCmd);
                    FlattenCoordinationTracker.Shared.TryPeekEpisodeForOwner(coordAcct, instrumentKey, coordInst, out var ep2, out _, out _);
                    _pendingFlattenVerifications[instrumentKey] = (requestedUtc, utcNow.AddSeconds(FLATTEN_VERIFY_WINDOW_SEC), newCid, string.IsNullOrEmpty(ep2) ? pendingEpisodeId : ep2);
                    break;
            }
        }

        foreach (var k in toRemove)
            _pendingFlattenVerifications.TryRemove(k, out _);
    }

    /// <summary>
    /// Check if executionInstrument matches the strategy's NinjaTrader Instrument.
    /// Handles both root-only names (e.g., "MGC") and full contract names (e.g., "MGC 04-26").
    /// If executionInstrument is root-only, compares to strategy instrument root.
    /// If executionInstrument includes contract month, requires exact match.
    /// </summary>
    private bool IsStrategyExecutionInstrument(string executionInstrument)
    {
        if (_ntInstrument == null)
            return false;

        var strategyInstrument = _ntInstrument as Instrument;
        if (strategyInstrument == null)
            return false;

        var trimmedInstrument = executionInstrument?.Trim();
        if (string.IsNullOrWhiteSpace(trimmedInstrument))
            return false;

        var strategyFullName = strategyInstrument.FullName ?? "";
        var strategyRoot = strategyFullName.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";

        // CRITICAL FIX: Check if executionInstrument is root-only (no space = no contract month)
        // If it's root-only, compare to strategy instrument root
        var executionParts = trimmedInstrument.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        var isRootOnly = executionParts.Length == 1;

        if (isRootOnly)
        {
            // Root-only comparison: "MGC" matches "MGC 04-26"
            return string.Equals(strategyRoot, trimmedInstrument, StringComparison.OrdinalIgnoreCase);
        }

        // Full contract name provided: require exact match
        try
        {
            var resolvedInstrument = Instrument.GetInstrument(trimmedInstrument);
            if (resolvedInstrument == null)
            {
                // Resolution failed - compare strings directly
                return string.Equals(strategyFullName, trimmedInstrument, StringComparison.OrdinalIgnoreCase);
            }

            // Compare Instrument instances (reference equality or FullName match)
            return ReferenceEquals(resolvedInstrument, strategyInstrument) ||
                   string.Equals(resolvedInstrument.FullName, strategyFullName, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // If resolution throws, fall back to string comparison
            return string.Equals(strategyFullName, trimmedInstrument, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Helper method to get Intent info for journal logging.
    /// Returns tradingDate, stream, and intent prices from IntentMap if available.
    /// </summary>
    private (string tradingDate, string stream, decimal? entryPrice, decimal? stopPrice, decimal? targetPrice, string? direction, string? ocoGroup) GetIntentInfo(string intentId)
    {
        if (IntentMap.TryGetValue(intentId, out var intent))
        {
            return (intent.TradingDate, intent.Stream, intent.EntryPrice, intent.StopPrice, intent.TargetPrice, intent.Direction, null);
        }
        return ("", "", null, null, null, null, null);
    }

}

#endif
