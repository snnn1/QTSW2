// P2.6 — Unified destructive-action policy (flatten / pre-flatten cancel scope). Robot.Contracts / netstandard2.0.

using System;

namespace QTSW2.Robot.Core.Execution;

/// <summary>Where a flatten or cancel-then-flatten was initiated (observability + policy routing).</summary>
public enum DestructiveActionSource
{
    RECOVERY,
    BOOTSTRAP,
    FAIL_CLOSED,
    EMERGENCY,
    MANUAL,
    RECONCILIATION,
    COMMAND
}

/// <summary>Normalized trigger for destructive actions. Prefer explicit enum on NT commands over free-text reasons.</summary>
public enum DestructiveTriggerReason
{
    ORPHAN_FILL,
    PROTECTIVE_TIMEOUT,
    FAIL_CLOSED,
    IEA_ENQUEUE_FAILURE,
    MANUAL,
    RECOVERY_RECONSTRUCTION,
    BOOTSTRAP,
    /// <summary>P2.6.7: IIEAOrderExecutor.EmergencyFlatten only — must pass policy emergency classifier before direct submit.</summary>
    DIRECT_EMERGENCY_FLATTEN,
    UNKNOWN
}

/// <summary>Outcome of <see cref="DestructiveActionPolicy.EvaluateDestructiveActionPolicy"/>.</summary>
public sealed class DestructiveActionDecision
{
    public bool AllowInstrumentScope { get; set; }
    public bool AllowStreamScope { get; set; } = true;
    public bool IsEmergency { get; set; }
    public string ReasonCode { get; set; } = "";
    public string PolicyPath { get; set; } = "";
    /// <summary>For cancel logging: instrument | explicit_set | fallback_account</summary>
    public string CancelScopeMode { get; set; } = "instrument";
}

/// <summary>Inputs for centralized destructive policy (P2.6).</summary>
public sealed class DestructiveActionPolicyInput
{
    public DestructiveActionSource Source { get; set; }
    public string? RecoveryReasonString { get; set; }
    /// <summary>When set, takes precedence over parsing <see cref="RecoveryReasonString"/>.</summary>
    public DestructiveTriggerReason? ExplicitTrigger { get; set; }
    public bool GateEngagedForSymbol { get; set; }
    public string ExecutionInstrumentKey { get; set; } = "";
    public int BrokerPositionQty { get; set; }
    public int JournalOpenQtySum { get; set; }
    public RecoveryActionKind? ReconstructionActionKind { get; set; }
    public StateOwnershipAttributionResult? Attribution { get; set; }
    public RecoveryAttributionSnapshot? AttributionSnapshot { get; set; }
    /// <summary>Bootstrap flatten chosen by BootstrapPhase4 (administrative).</summary>
    public bool BootstrapAdministrativeFlatten { get; set; }
    /// <summary>Strategy / delegated / verify-retry flatten (intentional instrument flatten).</summary>
    public bool ManualInstrumentFlatten { get; set; }
    /// <summary>Reconciliation path requesting instrument-wide recovery (freeze all + RequestRecovery).</summary>
    public bool ReconciliationInstrumentRecoveryRequested { get; set; }
    public StateOwnershipAttributionResult? ReconciliationAttribution { get; set; }
    /// <summary>When true, policy was already satisfied at enqueue (recovery seal).</summary>
    public bool RecoveryEnqueuePolicySealValid { get; set; }
    public bool RecoveryEnqueueAllowInstrument { get; set; }
    public string? RecoveryEnqueueReasonCode { get; set; }
}

/// <summary>
/// P2.6.6: Template for IEA RequestFlatten policy merge (final pre-submit gate).
/// Broker qty and execution key are applied at flatten time from live IEA/account state.
/// </summary>
public sealed class FlattenPolicyExecutionContext
{
    public DestructiveActionSource Source { get; set; } = DestructiveActionSource.MANUAL;
    public string? RecoveryReasonString { get; set; }
    public DestructiveTriggerReason? ExplicitTrigger { get; set; }
    public bool GateEngagedForSymbol { get; set; }
    public int JournalOpenQtySum { get; set; }
    public RecoveryActionKind? ReconstructionActionKind { get; set; } = RecoveryActionKind.Flatten;
    public StateOwnershipAttributionResult? Attribution { get; set; }
    public RecoveryAttributionSnapshot? AttributionSnapshot { get; set; }
    public bool BootstrapAdministrativeFlatten { get; set; }
    public bool ManualInstrumentFlatten { get; set; }
    public bool ReconciliationInstrumentRecoveryRequested { get; set; }
    public StateOwnershipAttributionResult? ReconciliationAttribution { get; set; }
    public bool RecoveryEnqueuePolicySealValid { get; set; }
    public bool RecoveryEnqueueAllowInstrument { get; set; }
    public string? RecoveryEnqueueReasonCode { get; set; }

    // --- P2.6.7 pre-cancel vs pre-submit drift observability (set by adapter before RequestFlatten) ---

    /// <summary>Broker |qty| observed in ExecuteFlattenInstrument after first policy allow, before cancel+flatten.</summary>
    public int? PrecheckBrokerPositionQtyAbs { get; set; }
    /// <summary>Signed broker qty at precheck (detect long/short flip before submit).</summary>
    public int? PrecheckBrokerQtySigned { get; set; }
    /// <summary>Journal open qty sum at precheck (compare if refreshed at final gate).</summary>
    public int? PrecheckJournalOpenQtySum { get; set; }
    public string? PrecheckCorrelationId { get; set; }
    public string? PrecheckPolicyReasonCode { get; set; }
    public bool? PrecheckAllowInstrument { get; set; }

    /// <summary>Build policy input for the final pre-submit gate (live broker qty + IEA execution key).</summary>
    public DestructiveActionPolicyInput ToPolicyInput(string executionInstrumentKey, int brokerPositionQtyAbs, string? reasonFallback)
    {
        return new DestructiveActionPolicyInput
        {
            Source = Source,
            RecoveryReasonString = RecoveryReasonString ?? reasonFallback,
            ExplicitTrigger = ExplicitTrigger,
            GateEngagedForSymbol = GateEngagedForSymbol,
            ExecutionInstrumentKey = executionInstrumentKey,
            BrokerPositionQty = brokerPositionQtyAbs,
            JournalOpenQtySum = JournalOpenQtySum,
            ReconstructionActionKind = ReconstructionActionKind,
            Attribution = Attribution,
            AttributionSnapshot = AttributionSnapshot,
            BootstrapAdministrativeFlatten = BootstrapAdministrativeFlatten,
            ManualInstrumentFlatten = ManualInstrumentFlatten,
            ReconciliationInstrumentRecoveryRequested = ReconciliationInstrumentRecoveryRequested,
            ReconciliationAttribution = ReconciliationAttribution,
            RecoveryEnqueuePolicySealValid = RecoveryEnqueuePolicySealValid,
            RecoveryEnqueueAllowInstrument = RecoveryEnqueueAllowInstrument,
            RecoveryEnqueueReasonCode = RecoveryEnqueueReasonCode
        };
    }

    /// <summary>When no caller template (legacy path): manual flatten + strict-prefix triggers from reason.</summary>
    public static FlattenPolicyExecutionContext CreateDefault(string? reason)
    {
        var ctx = new FlattenPolicyExecutionContext
        {
            Source = DestructiveActionSource.MANUAL,
            ManualInstrumentFlatten = true,
            RecoveryReasonString = reason,
            ReconstructionActionKind = RecoveryActionKind.Flatten
        };
        var p = DestructiveTriggerParser.TryParseStrictPrefix(reason);
        if (p.HasValue)
            ctx.ExplicitTrigger = p.Value;
        return ctx;
    }

    /// <summary>Copy fields from a pre-enqueue destructive input (P2.6.6 single funnel).</summary>
    public static FlattenPolicyExecutionContext FromDestructivePolicyInput(DestructiveActionPolicyInput input)
    {
        return new FlattenPolicyExecutionContext
        {
            Source = input.Source,
            RecoveryReasonString = input.RecoveryReasonString,
            ExplicitTrigger = input.ExplicitTrigger,
            GateEngagedForSymbol = input.GateEngagedForSymbol,
            JournalOpenQtySum = input.JournalOpenQtySum,
            ReconstructionActionKind = input.ReconstructionActionKind,
            Attribution = input.Attribution,
            AttributionSnapshot = input.AttributionSnapshot,
            BootstrapAdministrativeFlatten = input.BootstrapAdministrativeFlatten,
            ManualInstrumentFlatten = input.ManualInstrumentFlatten,
            ReconciliationInstrumentRecoveryRequested = input.ReconciliationInstrumentRecoveryRequested,
            ReconciliationAttribution = input.ReconciliationAttribution,
            RecoveryEnqueuePolicySealValid = input.RecoveryEnqueuePolicySealValid,
            RecoveryEnqueueAllowInstrument = input.RecoveryEnqueueAllowInstrument,
            RecoveryEnqueueReasonCode = input.RecoveryEnqueueReasonCode
        };
    }
}

/// <summary>P2.6 — Explicit emergency classification (no substring Contains on arbitrary text).</summary>
public static class DestructiveTriggerClassifier
{
    /// <summary>
    /// Instrument-wide emergency bypass list (narrow). ORPHAN_FILL must be confirmed via
    /// <see cref="DestructiveTriggerParser.TryParseStrictPrefix"/> or explicit <see cref="DestructiveTriggerReason.ORPHAN_FILL"/>.
    /// </summary>
    public static bool IsEmergencyInstrumentTrigger(DestructiveTriggerReason reason)
    {
        return reason == DestructiveTriggerReason.FAIL_CLOSED
               || reason == DestructiveTriggerReason.IEA_ENQUEUE_FAILURE
               || reason == DestructiveTriggerReason.ORPHAN_FILL
               || reason == DestructiveTriggerReason.DIRECT_EMERGENCY_FLATTEN;
    }
}

/// <summary>Strict prefix parsing for legacy recovery reason strings (RequestRecovery only).</summary>
public static class DestructiveTriggerParser
{
    public static DestructiveTriggerReason? TryParseStrictPrefix(string? reason)
    {
        if (string.IsNullOrEmpty(reason)) return null;
        if (reason.StartsWith("ORPHAN_FILL", StringComparison.OrdinalIgnoreCase))
            return DestructiveTriggerReason.ORPHAN_FILL;
        if (reason.StartsWith("IEA_ENQUEUE_FAILURE", StringComparison.OrdinalIgnoreCase))
            return DestructiveTriggerReason.IEA_ENQUEUE_FAILURE;
        return null;
    }

    /// <summary>Resolve trigger: explicit enum first, then strict legacy prefixes only.</summary>
    public static DestructiveTriggerReason Resolve(DestructiveTriggerReason? explicitTrigger, string? recoveryReasonString)
    {
        if (explicitTrigger.HasValue)
            return explicitTrigger.Value;
        var p = TryParseStrictPrefix(recoveryReasonString);
        if (p.HasValue)
            return p.Value;
        return DestructiveTriggerReason.UNKNOWN;
    }
}

/// <summary>P2.6 single policy surface for instrument-scoped destructive actions.</summary>
public static class DestructiveActionPolicy
{
    public static DestructiveActionDecision EvaluateDestructiveActionPolicy(DestructiveActionPolicyInput input)
    {
        var trigger = DestructiveTriggerParser.Resolve(input.ExplicitTrigger, input.RecoveryReasonString);

        if (DestructiveTriggerClassifier.IsEmergencyInstrumentTrigger(trigger))
        {
            return new DestructiveActionDecision
            {
                AllowInstrumentScope = true,
                AllowStreamScope = true,
                IsEmergency = true,
                ReasonCode = trigger.ToString(),
                PolicyPath = "emergency_bypass",
                CancelScopeMode = "instrument"
            };
        }

        if (input.RecoveryEnqueuePolicySealValid)
        {
            return new DestructiveActionDecision
            {
                AllowInstrumentScope = input.RecoveryEnqueueAllowInstrument,
                AllowStreamScope = true,
                IsEmergency = false,
                ReasonCode = input.RecoveryEnqueueReasonCode ?? "recovery_enqueue_seal",
                PolicyPath = "recovery_enqueue_seal",
                CancelScopeMode = "instrument"
            };
        }

        switch (input.Source)
        {
            case DestructiveActionSource.BOOTSTRAP:
                if (input.BootstrapAdministrativeFlatten)
                {
                    return new DestructiveActionDecision
                    {
                        AllowInstrumentScope = true,
                        AllowStreamScope = true,
                        IsEmergency = false,
                        ReasonCode = "BOOTSTRAP_ADMINISTRATIVE_FLATTEN",
                        PolicyPath = "bootstrap",
                        CancelScopeMode = "instrument"
                    };
                }
                return Deny("bootstrap_flatten_not_authorized");

            case DestructiveActionSource.MANUAL:
            case DestructiveActionSource.COMMAND:
                if (input.ManualInstrumentFlatten)
                {
                    return new DestructiveActionDecision
                    {
                        AllowInstrumentScope = true,
                        AllowStreamScope = true,
                        IsEmergency = false,
                        ReasonCode = input.Source == DestructiveActionSource.COMMAND ? "COMMAND_FLATTEN" : "MANUAL_FLATTEN",
                        PolicyPath = input.Source == DestructiveActionSource.COMMAND ? "iea_command" : "manual",
                        CancelScopeMode = "instrument"
                    };
                }
                return Deny("manual_flatten_not_authorized");

            case DestructiveActionSource.FAIL_CLOSED:
                // Enqueue path must set ExplicitTrigger FAIL_CLOSED; if missing, deny (no silent Contains).
                return input.ExplicitTrigger == DestructiveTriggerReason.FAIL_CLOSED
                    ? new DestructiveActionDecision
                    {
                        AllowInstrumentScope = true,
                        AllowStreamScope = true,
                        IsEmergency = true,
                        ReasonCode = "FAIL_CLOSED",
                        PolicyPath = "fail_closed_explicit",
                        CancelScopeMode = "instrument"
                    }
                    : Deny("fail_closed_missing_explicit_trigger");

            case DestructiveActionSource.EMERGENCY:
                return input.ExplicitTrigger == DestructiveTriggerReason.IEA_ENQUEUE_FAILURE
                    ? new DestructiveActionDecision
                    {
                        AllowInstrumentScope = true,
                        AllowStreamScope = true,
                        IsEmergency = true,
                        ReasonCode = "IEA_ENQUEUE_FAILURE",
                        PolicyPath = "emergency_iea_block",
                        CancelScopeMode = "instrument"
                    }
                    : Deny("emergency_missing_explicit_trigger");

            case DestructiveActionSource.RECONCILIATION:
                if (input.ReconciliationInstrumentRecoveryRequested && input.ReconciliationAttribution != null)
                {
                    var freeze = RecoveryOwnershipAttributionEvaluator.CanEscalateReconciliationToInstrumentFreeze(
                        input.ReconciliationAttribution,
                        input.BrokerPositionQty,
                        input.JournalOpenQtySum);
                    if (freeze)
                    {
                        return new DestructiveActionDecision
                        {
                            AllowInstrumentScope = true,
                            AllowStreamScope = true,
                            IsEmergency = false,
                            ReasonCode = "RECONCILIATION_INSTRUMENT_RECOVERY",
                            PolicyPath = "reconciliation",
                            CancelScopeMode = "instrument"
                        };
                    }
                    return new DestructiveActionDecision
                    {
                        AllowInstrumentScope = false,
                        AllowStreamScope = true,
                        IsEmergency = false,
                        ReasonCode = "RECONCILIATION_STREAM_SCOPE",
                        PolicyPath = "reconciliation_stream_only",
                        CancelScopeMode = "instrument"
                    };
                }
                return Deny("reconciliation_missing_attribution");

            case DestructiveActionSource.RECOVERY:
            default:
                if (input.ReconstructionActionKind != RecoveryActionKind.Flatten)
                {
                    return new DestructiveActionDecision
                    {
                        AllowInstrumentScope = false,
                        AllowStreamScope = true,
                        IsEmergency = false,
                        ReasonCode = "not_flatten_reconstruction_action",
                        PolicyPath = "recovery_non_flatten",
                        CancelScopeMode = "instrument"
                    };
                }

                if (input.Attribution == null || input.AttributionSnapshot == null)
                    return Deny("recovery_missing_attribution");

                var escalate = RecoveryOwnershipAttributionEvaluator.CanEscalateToInstrumentScopedRecovery(
                    input.Attribution,
                    input.AttributionSnapshot,
                    input.RecoveryReasonString,
                    emergencyInstrumentRequested: false,
                    gateEngagedForSymbol: input.GateEngagedForSymbol);

                if (!escalate)
                {
                    return new DestructiveActionDecision
                    {
                        AllowInstrumentScope = false,
                        AllowStreamScope = true,
                        IsEmergency = false,
                        ReasonCode = "recovery_attribution_blocks_instrument",
                        PolicyPath = "recovery_stream_containment",
                        CancelScopeMode = "instrument"
                    };
                }

                return new DestructiveActionDecision
                {
                    AllowInstrumentScope = true,
                    AllowStreamScope = true,
                    IsEmergency = false,
                    ReasonCode = "recovery_attribution_allows_instrument",
                    PolicyPath = "recovery_reconstruction",
                    CancelScopeMode = "instrument"
                };
        }
    }

    private static DestructiveActionDecision Deny(string code)
    {
        return new DestructiveActionDecision
        {
            AllowInstrumentScope = false,
            AllowStreamScope = true,
            IsEmergency = false,
            ReasonCode = code,
            PolicyPath = "denied",
            CancelScopeMode = "instrument"
        };
    }
}
