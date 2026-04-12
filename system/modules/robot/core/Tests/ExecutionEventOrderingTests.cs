// Execution event ordering hardening tests.
// Run: dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --test EXECUTION_ORDERING
//
// Verifies: DeferredExecutionResolver, ExecutionDeduplicator, retry semantics, duplicate detection.

using System;
using QTSW2.Robot.Core.Execution;

namespace QTSW2.Robot.Core.Tests;

public static class ExecutionEventOrderingTests
{
    public static (bool Pass, string? Error) RunExecutionEventOrderingTests()
    {
        // 1. ExecutionDeduplicator: same execution twice -> second is duplicate
        var dedup = new ExecutionDeduplicator();
        var execId = "exec-123";
        var isDup1 = dedup.TryMarkAndCheckDuplicate(execId, "ord-1", 0, 1, "Long");
        var isDup2 = dedup.TryMarkAndCheckDuplicate(execId, "ord-1", 0, 1, "Long");
        if (isDup1)
            return (false, "First execution should not be duplicate");
        if (!isDup2)
            return (false, "Second execution (same execId) should be duplicate");

        // 2. ExecutionDeduplicator: different executions -> both new
        var isDup3 = dedup.TryMarkAndCheckDuplicate("exec-456", "ord-2", 0, 1, "Short");
        if (isDup3)
            return (false, "Different execution should not be duplicate");

        // 3. UnresolvedExecutionQueue: enqueue, dequeue, count
        var queue = new UnresolvedExecutionQueue();
        var deferred = new DeferredExecution
        {
            ExecutionId = "e1",
            OrderId = "o1",
            BrokerOrderId = "broker-o1",
            Instrument = "MNQ",
            ReceivedUtc = DateTimeOffset.UtcNow,
            RetryCount = 0
        };
        queue.Enqueue(deferred);
        queue.IncrementDeferredCount();
        if (queue.Count != 1)
            return (false, "Queue count should be 1 after enqueue");
        if (!queue.TryDequeue(out var outItem) || outItem == null || outItem.ExecutionId != "e1")
            return (false, "Dequeue should return enqueued item");
        if (queue.Count != 0)
            return (false, "Queue count should be 0 after dequeue");

        // 4. Retry constants
        if (UnresolvedExecutionQueue.MaxRetries != 5)
            return (false, "MaxRetries should be 5");
        if (UnresolvedExecutionQueue.RetryIntervalMs < 50 || UnresolvedExecutionQueue.RetryIntervalMs > 100)
            return (false, "RetryIntervalMs should be 50-100");

        // 5. Dedup without executionId uses composite key
        var dedup2 = new ExecutionDeduplicator();
        var comp1 = dedup2.TryMarkAndCheckDuplicate(null, "ord-x", 100, 2, "Flat");
        var comp2 = dedup2.TryMarkAndCheckDuplicate(null, "ord-x", 100, 2, "Flat");
        if (comp1 || !comp2)
            return (false, "Composite key dedup: first new, second duplicate");

        return (true, null);
    }
}
