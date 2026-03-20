using System;
using System.Collections.Generic;
using System.Linq;
using QTSW2.Robot.Contracts;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// NinjaTrader Live adapter: places orders in real brokerage account.
/// Stub implementation - actual NT integration to be added in Phase C.
/// Requires explicit two-key enable (CLI flag + config).
/// GetCurrentMarketPrice: Uses Instrument.MarketData when SetNTContext called (breakout validity gate).
/// </summary>
public sealed partial class NinjaTraderLiveAdapter : IExecutionAdapter
{
    private readonly RobotLogger _log;
    private readonly TimeService _time;
    private object? _ntAccount;   // NinjaTrader.Cbi.Account when SetNTContext called
    private object? _ntInstrument; // NinjaTrader.Cbi.Instrument when SetNTContext called

    public NinjaTraderLiveAdapter(RobotLogger log, TimeService time)
    {
        _log = log;
        _time = time;
    }

    public OrderSubmissionResult SubmitEntryOrder(
        string intentId,
        string instrument,
        string direction,
        decimal? entryPrice,
        int quantity,
        string? entryOrderType,
        string? ocoGroup,
        DateTimeOffset utcNow)
    {
        // TODO: Phase C - Implement actual NT Live order placement with guardrails
        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_SUBMIT_ATTEMPT", new
        {
            order_type = "ENTRY",
            direction,
            entry_price = entryPrice,
            entry_order_type = entryOrderType,
            quantity,
            account = "LIVE",
            note = "Stub - actual NT integration pending"
        }));

        return OrderSubmissionResult.FailureResult("LIVE adapter not yet implemented", utcNow);
    }

    public OrderSubmissionResult SubmitStopEntryOrder(
        string intentId,
        string instrument,
        string direction,
        decimal stopPrice,
        int quantity,
        string? ocoGroup,
        DateTimeOffset utcNow)
    {
        // TODO: Phase C - Implement actual NT Live stop-entry placement with OCO linking
        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_SUBMIT_ATTEMPT", new
        {
            order_type = "ENTRY_STOP",
            direction,
            stop_price = stopPrice,
            quantity,
            oco_group = ocoGroup,
            account = "LIVE",
            note = "Stub - actual NT integration pending"
        }));

        return OrderSubmissionResult.FailureResult("LIVE adapter not yet implemented", utcNow);
    }

    public OrderSubmissionResult SubmitProtectiveStop(
        string intentId,
        string instrument,
        string direction,
        decimal stopPrice,
        int quantity,
        string? ocoGroup,
        DateTimeOffset utcNow)
    {
        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_SUBMIT_ATTEMPT", new
        {
            order_type = "PROTECTIVE_STOP",
            direction,
            stop_price = stopPrice,
            quantity,
            account = "LIVE",
            note = "Stub - actual NT integration pending"
        }));

        return OrderSubmissionResult.FailureResult("LIVE adapter not yet implemented", utcNow);
    }

    public OrderSubmissionResult SubmitTargetOrder(
        string intentId,
        string instrument,
        string direction,
        decimal targetPrice,
        int quantity,
        string? ocoGroup,
        DateTimeOffset utcNow)
    {
        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_SUBMIT_ATTEMPT", new
        {
            order_type = "TARGET",
            direction,
            target_price = targetPrice,
            quantity,
            account = "LIVE",
            note = "Stub - actual NT integration pending"
        }));

        return OrderSubmissionResult.FailureResult("LIVE adapter not yet implemented", utcNow);
    }

    public OrderModificationResult ModifyStopToBreakEven(
        string intentId,
        string instrument,
        decimal beStopPrice,
        DateTimeOffset utcNow)
    {
        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "STOP_MODIFY_ATTEMPT", new
        {
            be_stop_price = beStopPrice,
            account = "LIVE",
            note = "Stub - actual NT integration pending"
        }));

        return OrderModificationResult.FailureResult("LIVE adapter not yet implemented", utcNow);
    }

    public void ProcessPendingUnresolvedExecutions()
    {
        // LIVE: No-op (Phase 1 grace is SIM-only path)
    }

    public void EvaluateBreakEven(decimal tickPrice, DateTimeOffset? tickTimeFromEvent, string executionInstrument)
    {
        // LIVE: Not yet implemented
    }

    public FlattenResult Flatten(
        string intentId,
        string instrument,
        DateTimeOffset utcNow)
    {
        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "FLATTEN_ATTEMPT", new
        {
            account = "LIVE",
            note = "Stub - actual NT integration pending"
        }));

        return FlattenResult.FailureResult("LIVE adapter not yet implemented", utcNow);
    }

    public FlattenResult FlattenEmergency(string instrument, DateTimeOffset utcNow)
    {
        return FlattenEmergencyReal(instrument, utcNow);
    }
    
    public AccountSnapshot GetAccountSnapshot(DateTimeOffset utcNow)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // LIVE adapter: Implement snapshot using NT account (fail-closed on error)
        try
        {
            // TODO: Phase C - Implement actual NT Live account snapshot
            // For now, return empty snapshot with fail-closed log
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "ACCOUNT_SNAPSHOT_LIVE_STUB", state: "ENGINE",
                new
                {
                    account = "LIVE",
                    note = "LIVE adapter snapshot not yet implemented - returning empty snapshot"
                }));
            
            var snap = new AccountSnapshot
            {
                Positions = new List<PositionSnapshot>(),
                WorkingOrders = new List<WorkingOrderSnapshot>()
            };
            sw.Stop();
            SnapshotMetricsCollector.GetOrCreate(_log).RecordCall(DateTimeOffset.UtcNow, sw.ElapsedMilliseconds, 0, 0);
            return snap;
        }
        catch (Exception ex)
        {
            sw.Stop();
            SnapshotMetricsCollector.GetOrCreate(_log).RecordCall(DateTimeOffset.UtcNow, sw.ElapsedMilliseconds, 0, 0);
            // Fail-closed logging
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "ACCOUNT_SNAPSHOT_LIVE_ERROR", state: "ENGINE",
                new
                {
                    error = ex.Message,
                    exception_type = ex.GetType().Name,
                    account = "LIVE",
                    note = "Failed to snapshot LIVE account - returning empty snapshot"
                }));
            
            return new AccountSnapshot
            {
                Positions = new List<PositionSnapshot>(),
                WorkingOrders = new List<WorkingOrderSnapshot>()
            };
        }
    }
    
    public void CancelOrders(IEnumerable<string> orderIds, DateTimeOffset utcNow)
    {
        var ids = orderIds?.ToList() ?? new List<string>();
        if (ids.Count > 0)
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "CANCEL_ORDERS_LIVE_STUB", state: "ENGINE",
                new { order_ids = ids, account = "LIVE", note = "LIVE adapter CancelOrders not yet implemented" }));
    }

    public void CancelRobotOwnedWorkingOrders(AccountSnapshot snap, DateTimeOffset utcNow)
    {
        // LIVE adapter: Implement cancel using NT account (fail-closed on error)
        try
        {
            var robotOwnedOrders = snap.WorkingOrders?.Where(o => 
                (!string.IsNullOrEmpty(o.Tag) && o.Tag.StartsWith("QTSW2:", StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrEmpty(o.OcoGroup) && o.OcoGroup.StartsWith("QTSW2:", StringComparison.OrdinalIgnoreCase))
            ).ToList() ?? new List<WorkingOrderSnapshot>();
            
            // TODO: Phase C - Implement actual NT Live order cancellation
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "CANCEL_ROBOT_ORDERS_LIVE_STUB", state: "ENGINE",
                new
                {
                    robot_owned_count = robotOwnedOrders.Count,
                    robot_owned_order_ids = robotOwnedOrders.Select(o => o.OrderId).ToList(),
                    account = "LIVE",
                    note = "LIVE adapter cancel not yet implemented - would cancel robot-owned orders"
                }));
        }
        catch (Exception ex)
        {
            // Fail-closed logging
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "CANCEL_ROBOT_ORDERS_LIVE_ERROR", state: "ENGINE",
                new
                {
                    error = ex.Message,
                    exception_type = ex.GetType().Name,
                    account = "LIVE",
                    note = "Failed to cancel robot-owned orders in LIVE account"
                }));
        }
    }

    /// <summary>
    /// Set NinjaTrader context (Account, Instrument) from Strategy host.
    /// Required for GetCurrentMarketPrice to return live bid/ask (breakout validity gate).
    /// When not set, GetCurrentMarketPrice returns (null, null) → gate fail-open.
    /// </summary>
    public void SetNTContext(object account, object instrument, string? engineExecutionInstrument = null)
    {
        _ntAccount = account;
        _ntInstrument = instrument;
        _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", eventType: "LIVE_ADAPTER_NT_CONTEXT_SET", state: "ENGINE",
            new { note = "NinjaTrader Account and Instrument set for live execution" }));
    }

    public (decimal? Bid, decimal? Ask) GetCurrentMarketPrice(string instrument, DateTimeOffset utcNow)
    {
        if (_ntInstrument == null)
        {
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "GET_MARKET_PRICE_LIVE_NO_CONTEXT", state: "ENGINE",
                new { instrument, note = "LIVE adapter: SetNTContext not called - returning (null,null), gate fail-open" }));
            return (null, null);
        }
        try
        {
            dynamic dynInstrument = _ntInstrument;
            var marketData = dynInstrument.MarketData;
            if (marketData == null)
            {
                _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "GET_MARKET_PRICE_LIVE_NO_MARKET_DATA", state: "ENGINE",
                    new { instrument, note = "Instrument.MarketData is null - returning (null,null)" }));
                return (null, null);
            }
            double? bid = null;
            double? ask = null;
            try
            {
                var bidObj = marketData.GetBid(0);
                var askObj = marketData.GetAsk(0);
                bid = ToDoubleOrNull(bidObj);
                ask = ToDoubleOrNull(askObj);
            }
            catch
            {
                try
                {
                    var bidObj = marketData.Bid;
                    var askObj = marketData.Ask;
                    bid = ToDoubleOrNull(bidObj);
                    ask = ToDoubleOrNull(askObj);
                }
                catch
                {
                    _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "GET_MARKET_PRICE_LIVE_ACCESS_ERROR", state: "ENGINE",
                        new { instrument, note = "GetBid/GetAsk failed - returning (null,null)" }));
                    return (null, null);
                }
            }
            if (bid.HasValue && ask.HasValue && !double.IsNaN(bid.Value) && !double.IsNaN(ask.Value))
            {
                return ((decimal)bid.Value, (decimal)ask.Value);
            }
        }
        catch (Exception ex)
        {
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "GET_MARKET_PRICE_LIVE_EXCEPTION", state: "ENGINE",
                new { instrument, error = ex.Message, note = "Exception accessing market data - returning (null,null)" }));
        }
        return (null, null);
    }

    private static double? ToDoubleOrNull(object? value)
    {
        if (value == null) return null;
        try { return Convert.ToDouble(value); } catch { return null; }
    }

    public void RequestRecoveryForInstrument(string instrument, string reason, object context, DateTimeOffset utcNow)
    {
        // LIVE adapter: No-op (stub)
    }

    public void RequestSupervisoryActionForInstrument(string instrument, SupervisoryTriggerReason reason, SupervisorySeverity severity, object? context, DateTimeOffset utcNow)
    {
        // LIVE adapter: No-op (stub)
    }

    public void TryRetryDeferredAdoptionScan()
    {
        // LIVE adapter: No IEA, no-op
    }

    public IReadOnlyCollection<string> GetActiveIntentIdsForProtectiveAudit(string instrument)
    {
        return Array.Empty<string>();
    }

    public void EnqueueExecutionCommand(ExecutionCommandBase command)
    {
        // LIVE adapter: No-op (stub - IEA not yet bound)
    }

    public FlattenResult? RequestSessionCloseFlattenImmediate(string intentId, string instrument, DateTimeOffset utcNow)
    {
        // LIVE adapter: Not yet implemented - return null so caller uses EmergencyFlatten fallback
        return null;
    }
}
