using QTSW2.Robot.Core.Execution;

namespace QTSW2.Robot.Core.Tests;

public static class EntrySubmitIdempotencyTests
{
    public static (bool Pass, string? Error) RunEntrySubmitIdempotencyTests()
    {
        foreach (var state in new[] { "SUBMITTED", "ACCEPTED", "WORKING", "PART_FILLED", "CancelPending", "ChangePending", "Initialized", "FILLED" })
        {
            var order = new OrderInfo
            {
                IntentId = "intent",
                IsEntryOrder = true,
                State = state,
                Quantity = 2,
                FilledQuantity = state == "FILLED" ? 2 : 0
            };

            if (!NinjaTraderSimAdapter.ShouldBlockEntryResubmitForExistingOrder(order))
                return (false, $"Expected entry resubmit blocked for state {state}");
        }

        foreach (var state in new[] { "CANCELLED", "CANCELED", "REJECTED", "", "UNKNOWN" })
        {
            var order = new OrderInfo
            {
                IntentId = "intent",
                IsEntryOrder = true,
                State = state,
                Quantity = 2
            };

            if (NinjaTraderSimAdapter.ShouldBlockEntryResubmitForExistingOrder(order))
                return (false, $"Expected entry resubmit allowed for terminal/non-live state {state}");
        }

        var protective = new OrderInfo
        {
            IntentId = "intent",
            IsEntryOrder = false,
            OrderType = "STOP",
            State = "WORKING",
            Quantity = 2
        };

        if (NinjaTraderSimAdapter.ShouldBlockEntryResubmitForExistingOrder(protective))
            return (false, "Protective working order must not be classified as an entry-resubmit blocker");

        var duplicateResult = NinjaTraderSimAdapter.DuplicateEntryResubmitResult("broker-123", DateTimeOffset.Parse("2026-05-11T15:43:00Z"));
        if (!duplicateResult.Success || duplicateResult.BrokerOrderId != "broker-123" || duplicateResult.AcknowledgedAt == null)
            return (false, "Duplicate entry resubmit must return idempotent success for the existing broker order");

        return (true, null);
    }
}
