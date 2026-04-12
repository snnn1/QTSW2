using System;
using System.Collections.Generic;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Order tracking info for callback correlation.
/// Shared between NinjaTraderSimAdapter and InstrumentExecutionAuthority.
/// </summary>
internal sealed class OrderInfo
{
    public string IntentId { get; set; } = "";
    public string Instrument { get; set; } = "";
    public string OrderId { get; set; } = "";
    public string OrderType { get; set; } = "";
    public string Direction { get; set; } = "";
    public int Quantity { get; set; }
    public decimal? Price { get; set; }
    public string State { get; set; } = "";
    public bool IsEntryOrder { get; set; }
    public int FilledQuantity { get; set; }
    public DateTimeOffset? EntryFillTime { get; set; }
    public DateTimeOffset? BrokerLastEventUtc { get; set; }
    public bool ProtectiveStopAcknowledged { get; set; }
    public bool ProtectiveTargetAcknowledged { get; set; }
    public int ExpectedQuantity { get; set; }
    public int MaxQuantity { get; set; }
    public string PolicySource { get; set; } = "";
    public string CanonicalInstrument { get; set; } = "";
    public string ExecutionInstrument { get; set; } = "";
    public IReadOnlyList<string>? AggregatedIntentIds { get; set; }
    public Dictionary<string, int>? AggregatedFilledByIntent { get; set; }
    public string? OcoGroup { get; set; }
#if NINJATRADER
    public object? NTOrder { get; set; } // NinjaTrader.Cbi.Order
#endif
}

/// <summary>
/// Intent policy expectation for quantity invariant tracking.
/// </summary>
internal sealed class IntentPolicyExpectation
{
    public int ExpectedQuantity { get; set; }
    public int MaxQuantity { get; set; }
    public string PolicySource { get; set; } = "EXECUTION_POLICY_FILE";
    public string CanonicalInstrument { get; set; } = "";
    public string ExecutionInstrument { get; set; } = "";
}
