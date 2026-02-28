using System;
using System.Collections.Generic;
using QTSW2.Robot.Contracts;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Direct-call replay driver. Feeds events to IEA via *Core methods (no queue).
/// Single-threaded, deterministic. Use with ReplayEventClock for event-time ordering.
/// </summary>
public sealed class ReplayDriver
{
    private readonly InstrumentExecutionAuthority _iea;
    private readonly ReplayEventClock _clock;

    public ReplayDriver(string accountName, string executionInstrumentKey)
    {
        _clock = new ReplayEventClock();
        var executor = new ReplayExecutor();
        _iea = new InstrumentExecutionAuthority(
            accountName,
            executionInstrumentKey,
            executor,
            log: null,
            aggregationPolicy: null,
            eventClock: _clock,
            wallClock: _clock);
        executor.SetIEA(_iea);
    }

    /// <summary>Process a single event. Call in order.</summary>
    public void ProcessEvent(ReplayEventEnvelope envelope)
    {
        switch (envelope.Type)
        {
            case ReplayEventType.IntentRegistered:
                var ir = (ReplayIntentRegistered)envelope.Payload;
                var intent = ToIntent(ir.Intent);
                _iea.RegisterIntent(intent);
                break;

            case ReplayEventType.IntentPolicyRegistered:
                var ipr = (ReplayIntentPolicyRegistered)envelope.Payload;
                _iea.RegisterIntentPolicy(
                    ipr.IntentId,
                    ipr.ExpectedQty,
                    ipr.MaxQty,
                    ipr.Canonical,
                    ipr.Execution,
                    ipr.PolicySource);
                break;

            case ReplayEventType.ExecutionUpdate:
                var eu = (ReplayExecutionUpdate)envelope.Payload;
                _clock.SetNow(eu.ExecutionTime);
                var execId = eu.ExecutionId;
                var ticks = eu.ExecutionTime.UtcTicks;
                if (TryMarkAndCheckDuplicateCore(execId, eu.OrderId, ticks, eu.FillQuantity, eu.MarketPosition))
                    return;
                _iea.ProcessExecutionUpdateCore(eu);
                break;

            case ReplayEventType.OrderUpdate:
                var ou = (ReplayOrderUpdate)envelope.Payload;
                if (ou.UpdateTime.HasValue)
                    _clock.SetNow(ou.UpdateTime.Value);
                _iea.ProcessOrderUpdateCore(ou);
                break;

            case ReplayEventType.Tick:
                var tick = (ReplayTick)envelope.Payload;
                var tickTime = tick.TickTimeFromEvent ?? _clock.NowEvent();
                _clock.SetNow(tickTime);
                _iea.EvaluateBreakEvenDirect(tick.TickPrice, tickTime, hasEventTime: true, tick.ExecutionInstrument);
                break;
        }
    }

    /// <summary>Process all events in order.</summary>
    public void ProcessAll(IReadOnlyList<ReplayEventEnvelope> events)
    {
        foreach (var e in events)
            ProcessEvent(e);
    }

    /// <summary>Get IEA for snapshot or inspection.</summary>
    public InstrumentExecutionAuthority IEA => _iea;

    /// <summary>Get deterministic snapshot for hash comparison.</summary>
    public IEASnapshot GetSnapshot() => _iea.GetSnapshot();

    private static Intent ToIntent(ReplayIntent r)
    {
        return new Intent(
            r.TradingDate ?? "",
            r.Stream ?? "",
            r.Instrument ?? "",
            r.ExecutionInstrument ?? "",
            r.Session ?? "",
            r.SlotTimeChicago ?? "",
            r.Direction,
            r.EntryPrice,
            r.StopPrice,
            r.TargetPrice,
            r.BeTrigger,
            r.EntryTimeUtc,
            r.TriggerReason ?? "");
    }

    private bool TryMarkAndCheckDuplicateCore(string? executionId, string orderId, long ticks, int quantity, string marketPosition)
    {
        return _iea.TryMarkAndCheckDuplicateCore(executionId, orderId, ticks, quantity, marketPosition);
    }
}
