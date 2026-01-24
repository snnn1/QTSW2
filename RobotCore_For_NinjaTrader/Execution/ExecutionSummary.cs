using System.Collections.Generic;
using System.Linq;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Execution summary for a run: tracks intents, orders, rejects, fills, blocks.
/// Persisted as JSON artifact for audit and analysis.
/// </summary>
public sealed class ExecutionSummary
{
    private readonly Dictionary<string, IntentSummary> _intents = new();
    private int _intentsSeen;
    private int _intentsExecuted;
    private int _ordersSubmitted;
    private int _ordersRejected;
    private int _ordersFilled;
    private int _ordersBlocked;
    private readonly Dictionary<string, int> _blockedByReason = new();
    private int _duplicatesSkipped;
    
    // Cost tracking
    private decimal _totalSlippageDollars = 0;
    private decimal _totalCommission = 0;
    private decimal _totalFees = 0;
    private decimal _totalExecutionCost = 0;

    public void RecordIntentSeen(string intentId, string tradingDate, string stream, string instrument)
    {
        _intentsSeen++;
        if (!_intents.ContainsKey(intentId))
        {
            _intents[intentId] = new IntentSummary
            {
                IntentId = intentId,
                TradingDate = tradingDate,
                Stream = stream,
                Instrument = instrument
            };
        }
    }

    public void RecordIntentExecuted(string intentId)
    {
        _intentsExecuted++;
        if (_intents.TryGetValue(intentId, out var intent))
        {
            intent.Executed = true;
        }
    }

    public void RecordOrderSubmitted(string intentId, string orderType)
    {
        _ordersSubmitted++;
        if (_intents.TryGetValue(intentId, out var intent))
        {
            intent.OrdersSubmitted++;
            intent.OrderTypes.Add(orderType);
        }
    }

    public void RecordOrderRejected(string intentId, string reason)
    {
        _ordersRejected++;
        if (_intents.TryGetValue(intentId, out var intent))
        {
            intent.OrdersRejected++;
            intent.RejectionReasons.Add(reason);
        }
    }

    public void RecordOrderFilled(string intentId)
    {
        _ordersFilled++;
        if (_intents.TryGetValue(intentId, out var intent))
        {
            intent.OrdersFilled++;
        }
    }

    public void RecordBlocked(string intentId, string reason)
    {
        _ordersBlocked++;
        if (!_blockedByReason.TryGetValue(reason, out var count)) count = 0;
        _blockedByReason[reason] = count + 1;
        if (_intents.TryGetValue(intentId, out var intent))
        {
            intent.Blocked = true;
            intent.BlockReason = reason;
        }
    }

    public void RecordDuplicateSkipped(string intentId)
    {
        _duplicatesSkipped++;
        if (_intents.TryGetValue(intentId, out var intent))
        {
            intent.DuplicateSkipped = true;
        }
    }
    
    /// <summary>
    /// Record execution cost (slippage, commission, fees) for an intent.
    /// </summary>
    public void RecordExecutionCost(string intentId, decimal slippageDollars, decimal? commission, decimal? fees)
    {
        _totalSlippageDollars += slippageDollars;
        if (commission.HasValue) _totalCommission += commission.Value;
        if (fees.HasValue) _totalFees += fees.Value;
        _totalExecutionCost = _totalSlippageDollars + _totalCommission + _totalFees;
        
        if (_intents.TryGetValue(intentId, out var intent))
        {
            intent.SlippageDollars = slippageDollars;
            intent.Commission = commission;
            intent.Fees = fees;
            intent.TotalCost = slippageDollars + (commission ?? 0) + (fees ?? 0);
        }
    }

    public ExecutionSummarySnapshot GetSnapshot() => new ExecutionSummarySnapshot
    {
        IntentsSeen = _intentsSeen,
        IntentsExecuted = _intentsExecuted,
        OrdersSubmitted = _ordersSubmitted,
        OrdersRejected = _ordersRejected,
        OrdersFilled = _ordersFilled,
        OrdersBlocked = _ordersBlocked,
        BlockedByReason = new Dictionary<string, int>(_blockedByReason),
        DuplicatesSkipped = _duplicatesSkipped,
        IntentDetails = new List<IntentSummary>(_intents.Values),
        TotalSlippageDollars = _totalSlippageDollars,
        TotalCommission = _totalCommission,
        TotalFees = _totalFees,
        TotalExecutionCost = _totalExecutionCost
    };
}

public class IntentSummary
{
    public string IntentId { get; set; } = "";

    public string TradingDate { get; set; } = "";

    public string Stream { get; set; } = "";

    public string Instrument { get; set; } = "";

    public bool Executed { get; set; }

    public int OrdersSubmitted { get; set; }

    public int OrdersRejected { get; set; }

    public int OrdersFilled { get; set; }

    public List<string> OrderTypes { get; set; } = new();

    public List<string> RejectionReasons { get; set; } = new();

    public bool Blocked { get; set; }

    public string? BlockReason { get; set; }

    public bool DuplicateSkipped { get; set; }
    
    // Cost tracking
    public decimal? SlippageDollars { get; set; }
    public decimal? Commission { get; set; }
    public decimal? Fees { get; set; }
    public decimal? TotalCost { get; set; }
}

public class ExecutionSummarySnapshot
{
    public int IntentsSeen { get; set; }

    public int IntentsExecuted { get; set; }

    public int OrdersSubmitted { get; set; }

    public int OrdersRejected { get; set; }

    public int OrdersFilled { get; set; }

    public int OrdersBlocked { get; set; }

    public Dictionary<string, int> BlockedByReason { get; set; } = new();

    public int DuplicatesSkipped { get; set; }

    public List<IntentSummary> IntentDetails { get; set; } = new();
    
    // Cost tracking
    public decimal TotalSlippageDollars { get; set; }
    public decimal TotalCommission { get; set; }
    public decimal TotalFees { get; set; }
    public decimal TotalExecutionCost { get; set; }
}
