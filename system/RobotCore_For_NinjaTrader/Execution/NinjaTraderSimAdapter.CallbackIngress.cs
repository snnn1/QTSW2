// Per-instrument ingress buffering for non-IEA NT callbacks. NinjaTrader fans out many OnExecutionUpdate /
// OnOrderUpdate deliveries per logical event; we must not run heavy fill/order logic on the callback thread.
// Drain runs on the strategy thread from RobotEngine.Tick after RuntimeAudit periodic work.

#if NINJATRADER
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using NinjaTrader.Cbi;

namespace QTSW2.Robot.Core.Execution;

public sealed partial class NinjaTraderSimAdapter
{
    private const int IngressMaxExecutionPerInstrumentPerDrain = 10;
    private const int IngressMaxOrderPerInstrumentPerDrain = 10;
    private const int IngressMaxTotalEventsPerDrain = 50;
    private const int IngressQueueWarnDepth = 100;
    private const int IngressQueueCriticalDepth = 500;

    private readonly ConcurrentDictionary<string, InstrumentCallbackIngress> _callbackIngressByInstrument =
        new(StringComparer.OrdinalIgnoreCase);

    private sealed class InstrumentCallbackIngress
    {
        public readonly object LockObj = new();
        public readonly Queue<ExecutionIngress> WorkExec = new();
        public readonly List<OrderIngress> workOrders = new();

        public long ExecEnqueued;
        public long OrderEnqueued;
        public long OrderCoalescedInQueue;
        public long ExecProcessedDrain;
        public long OrderProcessedDrain;
        public int MaxDepthSeen;
        public DateTimeOffset? LastDrainUtc;
        public bool HighWarnEmitted;
        public bool CriticalWarnEmitted;
    }

    private sealed class ExecutionIngress
    {
        public object ExecutionObj = null!;
        public object OrderObj = null!;
        public UnresolvedExecutionRecord? RetryRecord;
        public OrderInfo? RetryOrderInfo;
    }

    private sealed class OrderIngress
    {
        public object OrderObj = null!;
        public object OrderUpdateObj = null!;
        public string OrderId = "";
        public string EffectiveStateKey = "";
    }

    private static string NormalizeIngressInstrumentKey(string? name)
    {
        var t = (name ?? "").Trim();
        return string.IsNullOrEmpty(t) ? "__UNKNOWN__" : t;
    }

    /// <summary>Post-flat CPU audit: current ingress depths for a logical/chart instrument key.</summary>
    public bool TryGetCallbackIngressQueueLengths(string logicalInstrument, out int executionQueueLength, out int orderQueueLength)
    {
        executionQueueLength = 0;
        orderQueueLength = 0;
        var key = NormalizeIngressInstrumentKey(logicalInstrument);
        if (!_callbackIngressByInstrument.TryGetValue(key, out var st))
            return false;
        lock (st.LockObj)
        {
            executionQueueLength = st.WorkExec.Count;
            orderQueueLength = st.workOrders.Count;
        }
        return true;
    }

    /// <summary>Aggregate ingress depth across all instruments (non-IEA audit).</summary>
    public void GetTotalCallbackIngressQueueLengths(out int executionTotal, out int orderTotal)
    {
        executionTotal = 0;
        orderTotal = 0;
        foreach (var kvp in _callbackIngressByInstrument)
        {
            var st = kvp.Value;
            lock (st.LockObj)
            {
                executionTotal += st.WorkExec.Count;
                orderTotal += st.workOrders.Count;
            }
        }
    }

    /// <summary>NT order snapshot for coalescing duplicate order-state churn before drain.</summary>
    internal static string BuildOrderIngressEffectiveStateKey(Order order)
    {
        var st = order.OrderState.ToString();
        var qty = order.Quantity;
        double sp = 0, lp = 0;
        try { sp = order.StopPrice; } catch { /* NT dynamic */ }
        try { lp = order.LimitPrice; } catch { }
        return $"{st}|{qty}|{sp:R}|{lp:R}";
    }

    private InstrumentCallbackIngress GetOrCreateIngress(string instrumentKey)
    {
        return _callbackIngressByInstrument.GetOrAdd(instrumentKey, _ => new InstrumentCallbackIngress());
    }

    /// <summary>Non-IEA: deferred execution retry continues on strategy thread via same ingress queue.</summary>
    internal void EnqueueExecutionIngressRetry(UnresolvedExecutionRecord record, OrderInfo orderInfo)
    {
        var key = NormalizeIngressInstrumentKey(record.Instrument);
        var st = GetOrCreateIngress(key);
        lock (st.LockObj)
        {
            st.WorkExec.Enqueue(new ExecutionIngress
            {
                ExecutionObj = record.Execution,
                OrderObj = record.Order,
                RetryRecord = record,
                RetryOrderInfo = orderInfo
            });
            st.ExecEnqueued++;
            TouchIngressDepthMetrics(st, key);
        }
    }

    internal void EnqueueExecutionIngressNormal(object executionObj, object orderObj, string instrumentKeyFromOrder)
    {
        var key = NormalizeIngressInstrumentKey(instrumentKeyFromOrder);
        var st = GetOrCreateIngress(key);
        lock (st.LockObj)
        {
            st.WorkExec.Enqueue(new ExecutionIngress
            {
                ExecutionObj = executionObj,
                OrderObj = orderObj,
                RetryRecord = null,
                RetryOrderInfo = null
            });
            st.ExecEnqueued++;
            TouchIngressDepthMetrics(st, key);
        }
    }

    internal void EnqueueOrCoalesceOrderIngress(object orderObj, object orderUpdateObj, Order order, string orderId, string effectiveKey)
    {
        var key = NormalizeIngressInstrumentKey(order.Instrument?.MasterInstrument?.Name);
        var st = GetOrCreateIngress(key);
        lock (st.LockObj)
        {
            for (var i = 0; i < st.workOrders.Count; i++)
            {
                var o = st.workOrders[i];
                if (string.Equals(o.OrderId, orderId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(o.EffectiveStateKey, effectiveKey, StringComparison.Ordinal))
                {
                    st.workOrders[i] = new OrderIngress
                    {
                        OrderObj = orderObj,
                        OrderUpdateObj = orderUpdateObj,
                        OrderId = orderId,
                        EffectiveStateKey = effectiveKey
                    };
                    st.OrderCoalescedInQueue++;
                    TouchIngressDepthMetrics(st, key);
                    return;
                }
            }

            st.workOrders.Add(new OrderIngress
            {
                OrderObj = orderObj,
                OrderUpdateObj = orderUpdateObj,
                OrderId = orderId,
                EffectiveStateKey = effectiveKey
            });
            st.OrderEnqueued++;
            TouchIngressDepthMetrics(st, key);
        }
    }

    private void TouchIngressDepthMetrics(InstrumentCallbackIngress st, string instrumentKey)
    {
        var d = st.WorkExec.Count + st.workOrders.Count;
        if (d > st.MaxDepthSeen) st.MaxDepthSeen = d;

        if (d >= IngressQueueWarnDepth && !st.HighWarnEmitted)
            st.HighWarnEmitted = true;

        if (d >= IngressQueueCriticalDepth)
        {
            if (!st.CriticalWarnEmitted)
                st.CriticalWarnEmitted = true;

            CollapseStaleDuplicateOrderStates(st);
        }
    }

    /// <summary>Under critical load, drop redundant order rows that share order_id+state (keep last).</summary>
    private static void CollapseStaleDuplicateOrderStates(InstrumentCallbackIngress st)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = st.workOrders.Count - 1; i >= 0; i--)
        {
            var o = st.workOrders[i];
            var k = o.OrderId + "\0" + o.EffectiveStateKey;
            if (!seen.Add(k))
                st.workOrders.RemoveAt(i);
        }
    }

    /// <summary>Strategy-thread drain: bounded work per engine tick. Non-IEA only.</summary>
    public void DrainCallbackIngress(DateTimeOffset utcNow)
    {
        if (_useInstrumentExecutionAuthority && _iea != null)
            return;

        var totalBudget = IngressMaxTotalEventsPerDrain;
        foreach (var kvp in _callbackIngressByInstrument.ToArray())
        {
            if (totalBudget <= 0) break;
            var instrumentKey = kvp.Key;
            var st = kvp.Value;
            int execDone = 0, ordDone = 0;

            lock (st.LockObj)
            {
                var execCap = Math.Min(IngressMaxExecutionPerInstrumentPerDrain, totalBudget);
                while (execCap-- > 0 && totalBudget > 0 && st.WorkExec.Count > 0)
                {
                    var w = st.WorkExec.Dequeue();
                    totalBudget--;
                    execDone++;
                    st.ExecProcessedDrain++;

                    try
                    {
                        if (w.RetryRecord != null && w.RetryOrderInfo != null)
                            ProcessExecutionUpdateContinuation(w.RetryRecord, w.RetryOrderInfo);
                        else
                            HandleExecutionUpdateReal(w.ExecutionObj, w.OrderObj, null, null, beginAtFillPath: true);
                    }
                    catch (Exception ex)
                    {
                        _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "CALLBACK_DRAIN_EXEC_ERROR", state: "ENGINE",
                            new { instrument = instrumentKey, error = ex.Message, exception_type = ex.GetType().Name }));
                    }
                }

                var ordCap = Math.Min(IngressMaxOrderPerInstrumentPerDrain, totalBudget);
                while (ordCap-- > 0 && totalBudget > 0 && st.workOrders.Count > 0)
                {
                    var o = st.workOrders[0];
                    st.workOrders.RemoveAt(0);
                    totalBudget--;
                    ordDone++;
                    st.OrderProcessedDrain++;

                    try
                    {
                        HandleOrderUpdateReal(o.OrderObj, o.OrderUpdateObj, beginAfterIngress: true);
                    }
                    catch (Exception ex)
                    {
                        _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "CALLBACK_DRAIN_ORDER_ERROR", state: "ENGINE",
                            new { instrument = instrumentKey, error = ex.Message, exception_type = ex.GetType().Name }));
                    }
                }

                var depthAfter = st.WorkExec.Count + st.workOrders.Count;
                st.LastDrainUtc = utcNow;

                if (depthAfter < IngressQueueWarnDepth)
                    st.HighWarnEmitted = false;
                if (depthAfter < IngressQueueCriticalDepth)
                    st.CriticalWarnEmitted = false;
            }
        }
    }
}
#endif
