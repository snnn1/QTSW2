using System;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Forensic snapshot of flatten decision for incident analysis.
/// Answers: What did the IEA believe the live position was when it decided to flatten?
/// </summary>
public sealed class FlattenDecisionSnapshot
{
    public string Instrument { get; set; } = "";
    public int AccountQuantityAtDecision { get; set; }
    public string AccountDirectionAtDecision { get; set; } = "";
    public string RequestedReason { get; set; } = "";
    public string? CallerContext { get; set; }
    public string ChosenSide { get; set; } = "";
    public int ChosenQuantity { get; set; }
    public string LatchRequestId { get; set; } = "";
    public string? LatchState { get; set; }
    public DateTimeOffset DecisionUtc { get; set; }

    /// <summary>When flattening multiple broker rows, 0-based leg index.</summary>
    public int? FlattenLegIndex { get; set; }

    /// <summary>Total abs qty across canonical bucket at decision (reconciliation-aligned).</summary>
    public int? CanonicalExposureAbsTotalAtDecision { get; set; }

    /// <summary>Contract label for this leg (e.g. NT FullName).</summary>
    public string? LegContractLabel { get; set; }

    /// <summary>Convert to anonymous object for structured logging (JSONL).</summary>
    public object ToLogPayload()
    {
        return new
        {
            instrument = Instrument,
            account_quantity_at_decision = AccountQuantityAtDecision,
            account_direction_at_decision = AccountDirectionAtDecision,
            requested_reason = RequestedReason,
            caller_context = CallerContext,
            chosen_side = ChosenSide,
            chosen_quantity = ChosenQuantity,
            latch_request_id = LatchRequestId,
            latch_state = LatchState,
            decision_utc = DecisionUtc.ToString("o"),
            flatten_leg_index = FlattenLegIndex,
            canonical_exposure_abs_total_at_decision = CanonicalExposureAbsTotalAtDecision,
            leg_contract_label = LegContractLabel
        };
    }
}
