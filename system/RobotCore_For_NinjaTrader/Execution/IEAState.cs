using System;
using System.Collections.Generic;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// IEA canonical ledger: position state. Source of truth for net position.
/// </summary>
public sealed class PositionState
{
    public string ExecutionInstrumentKey { get; set; } = "";
    public int NetFilledQty { get; set; }
    public decimal? AverageFillPrice { get; set; }
    public string Direction { get; set; } = ""; // "Long", "Short", or ""
}

/// <summary>
/// Bracket state machine. NONE / SUBMITTING / WORKING / RESIZING / CANCEL_REPLACE_PENDING / FAILED_CLOSED.
/// </summary>
public enum BracketStateKind
{
    NONE,
    SUBMITTING,
    WORKING,
    RESIZING,
    CANCEL_REPLACE_PENDING,
    FAILED_CLOSED
}

/// <summary>
/// IEA canonical ledger: bracket state. Working stop(s), target(s), state machine.
/// </summary>
public sealed class BracketState
{
    public BracketStateKind Kind { get; set; }
    public IReadOnlyList<string>? WorkingStopOrderIds { get; set; }
    public IReadOnlyList<decimal>? WorkingStopPrices { get; set; }
    public IReadOnlyList<string>? WorkingTargetOrderIds { get; set; }
    public IReadOnlyList<decimal>? WorkingTargetPrices { get; set; }
    public bool IsTransitional => Kind == BracketStateKind.RESIZING || Kind == BracketStateKind.CANCEL_REPLACE_PENDING;
}

/// <summary>
/// BE state: NOT_ARMED / ARMED / MOVED / BLOCKED_TRANSITIONAL.
/// </summary>
public enum BEStateKind
{
    NOT_ARMED,
    ARMED,
    MOVED,
    BLOCKED_TRANSITIONAL
}

/// <summary>
/// IEA canonical ledger: break-even state.
/// </summary>
public sealed class BEState
{
    public BEStateKind Kind { get; set; }
    public decimal? BeTriggerPrice { get; set; }
    public decimal? BeStopPrice { get; set; }
}
