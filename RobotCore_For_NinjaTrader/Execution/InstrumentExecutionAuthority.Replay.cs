using System;
using System.Collections.Generic;
using System.Linq;
using QTSW2.Robot.Contracts;

namespace QTSW2.Robot.Core.Execution;


/// <summary>
/// Replay Core methods — shared logic for deterministic state updates.
/// Called directly by ReplayDriver (no queue). NT adapter parses to DTO and calls these from worker.
/// </summary>
public sealed partial class InstrumentExecutionAuthority
{
    /// <summary>
    /// Process execution update from replay DTO. Updates OrderMap (FilledQuantity, EntryFillTime).
    /// Caller must have called TryMarkAndCheckDuplicateCore first and skipped if duplicate.
    /// </summary>
    internal void ProcessExecutionUpdateCore(ReplayExecutionUpdate evt)
    {
        var intentId = evt.IntentId ?? RobotOrderIds.DecodeIntentId(evt.Tag ?? "");
        if (string.IsNullOrEmpty(intentId)) return;

        var fillQuantity = evt.FillQuantity;
        var fillPrice = evt.FillPrice;
        var utcNow = evt.ExecutionTime;

        bool isProtectiveOrder = false;
        string? orderTypeFromTag = null;
        if (!string.IsNullOrEmpty(evt.Tag))
        {
            if (evt.Tag.EndsWith(":STOP", StringComparison.OrdinalIgnoreCase))
            {
                orderTypeFromTag = "STOP";
                isProtectiveOrder = true;
            }
            else if (evt.Tag.EndsWith(":TARGET", StringComparison.OrdinalIgnoreCase))
            {
                orderTypeFromTag = "TARGET";
                isProtectiveOrder = true;
            }
        }

        var mapKey = intentId;
        if (isProtectiveOrder && !string.IsNullOrEmpty(orderTypeFromTag))
            mapKey = $"{intentId}:{orderTypeFromTag}";

        if (!OrderMap.TryGetValue(mapKey, out var orderInfo))
        {
            if (!IntentMap.TryGetValue(intentId, out var intent))
                return;
            orderInfo = new OrderInfo
            {
                IntentId = intentId,
                Instrument = intent.Instrument ?? "",
                OrderId = evt.OrderId,
                OrderType = isProtectiveOrder ? (orderTypeFromTag ?? "") : "ENTRY",
                Direction = intent.Direction ?? "",
                Quantity = 0,
                Price = fillPrice,
                State = "SUBMITTED",
                IsEntryOrder = !isProtectiveOrder,
                FilledQuantity = 0
            };
            OrderMap[mapKey] = orderInfo;
        }

        orderInfo.FilledQuantity += fillQuantity;
        orderInfo.Price = fillPrice;

        var isEntryFill = !isProtectiveOrder && orderInfo.IsEntryOrder;
        if (isEntryFill)
        {
            orderInfo.EntryFillTime = utcNow;
            var intentIdsToUpdate = orderInfo.AggregatedIntentIds ?? new List<string> { intentId };
            if (intentIdsToUpdate.Count > 1)
                AllocateFillToIntents(intentIdsToUpdate, fillQuantity, orderInfo);
        }
    }

    /// <summary>
    /// Process order update from replay DTO. Updates OrderMap (State, ProtectiveStopAcknowledged, ProtectiveTargetAcknowledged).
    /// OCO sibling cancellation: Rejected + Comment contains "CancelPending" → state CANCELLED (not REJECTED).
    /// </summary>
    internal void ProcessOrderUpdateCore(ReplayOrderUpdate evt)
    {
        var intentId = evt.IntentId ?? RobotOrderIds.DecodeIntentId(evt.Tag ?? "");
        if (string.IsNullOrEmpty(intentId)) return;

        var parsed = RobotOrderIds.ParseTag(evt.Tag ?? "");
        var orderState = evt.OrderState ?? "";
        var comment = evt.Comment ?? "";

        // OCO sibling cancellation: NinjaTrader reports cancelled OCO sibling as Rejected with "CancelPending"
        var hasCancelPending = comment.IndexOf("CancelPending", StringComparison.OrdinalIgnoreCase) >= 0;
        var isOcoSiblingCancel = orderState.Equals("Rejected", StringComparison.OrdinalIgnoreCase) && hasCancelPending;

        var mapKey = intentId;
        if (parsed.Leg == "STOP")
            mapKey = $"{intentId}:STOP";
        else if (parsed.Leg == "TARGET")
            mapKey = $"{intentId}:TARGET";

        if (!OrderMap.TryGetValue(mapKey, out var orderInfo))
        {
            // Entry order may not exist yet (replay has no submit event). Create if intent registered.
            if (parsed.Leg == null || parsed.Leg == "ENTRY")
            {
                if (IntentMap.TryGetValue(intentId, out var intent))
                {
                    orderInfo = new OrderInfo
                    {
                        IntentId = intentId,
                        Instrument = intent.Instrument ?? "",
                        OrderId = evt.OrderId,
                        OrderType = "ENTRY",
                        Direction = intent.Direction ?? "",
                        Quantity = evt.Quantity ?? 0,
                        Price = 0,
                        State = "SUBMITTED",
                        IsEntryOrder = true,
                        FilledQuantity = 0
                    };
                    OrderMap[mapKey] = orderInfo;
                }
                else
                    return;
            }
            else
                return;
        }

        orderInfo.State = isOcoSiblingCancel ? "CANCELLED" : orderState;
        if (orderState.Equals("Accepted", StringComparison.OrdinalIgnoreCase) || orderState.Equals("ACCEPTED", StringComparison.OrdinalIgnoreCase))
        {
            if (parsed.Leg == "STOP")
                orderInfo.ProtectiveStopAcknowledged = true;
            else if (parsed.Leg == "TARGET")
                orderInfo.ProtectiveTargetAcknowledged = true;
        }
        if (evt.Filled.HasValue)
            orderInfo.FilledQuantity = evt.Filled.Value;
    }
}
