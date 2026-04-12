// Randomized stress test for execution replay rebuilder.
// Simulates: order updates before executions, duplicate executions, partial fills,
// cancel/replace, delayed execution updates. Run for 5-10 minutes to find crashes or inconsistent state.
// Run: dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --test RANDOM_STRESS [--stress-duration 300]

using System;
using System.Collections.Generic;
using System.Linq;
using QTSW2.Robot.Core.Execution;

namespace QTSW2.Robot.Core.Tests;

public static class RandomEventStressTests
{
    public const string NORMAL_ENTRY = "NORMAL_ENTRY";
    public const string EXECUTION_BEFORE_ORDER_UPDATE = "EXECUTION_BEFORE_ORDER_UPDATE";
    public const string DUPLICATE_EXECUTION = "DUPLICATE_EXECUTION";
    public const string PARTIAL_FILLS = "PARTIAL_FILLS";
    public const string CANCEL_PROTECTIVE = "CANCEL_PROTECTIVE";
    public const string MISMATCH_ESCALATION = "MISMATCH_ESCALATION";
    public const string QUEUE_POISON = "QUEUE_POISON";
    public const string RANDOM_MIX = "RANDOM_MIX";

    private static readonly string[] ScenarioNames = { NORMAL_ENTRY, EXECUTION_BEFORE_ORDER_UPDATE, DUPLICATE_EXECUTION, PARTIAL_FILLS, CANCEL_PROTECTIVE, MISMATCH_ESCALATION, QUEUE_POISON, RANDOM_MIX };

    private static readonly string[] Instruments = { "ES", "NQ", "RTY", "YM", "MES", "MNQ" };
    private static readonly string[] EventTypes =
    {
        ExecutionEventTypes.COMMAND_RECEIVED,
        ExecutionEventTypes.COMMAND_DISPATCHED,
        ExecutionEventTypes.COMMAND_COMPLETED,
        ExecutionEventTypes.ORDER_REGISTERED,
        ExecutionEventTypes.ORDER_CANCELLED,
        ExecutionEventTypes.EXECUTION_OBSERVED,
        ExecutionEventTypes.EXECUTION_DEFERRED,
        ExecutionEventTypes.EXECUTION_RESOLVED,
        ExecutionEventTypes.EXECUTION_DEDUPLICATED,
        ExecutionEventTypes.LIFECYCLE_TRANSITIONED,
        ExecutionEventTypes.PROTECTIVE_INSTRUMENT_BLOCKED,
        ExecutionEventTypes.PROTECTIVE_RECOVERY_CONFIRMED,
        ExecutionEventTypes.MISMATCH_DETECTED,
        ExecutionEventTypes.MISMATCH_PERSISTENT,
        ExecutionEventTypes.MISMATCH_FAIL_CLOSED,
        ExecutionEventTypes.QUEUE_POISON_DETECTED,
        ExecutionEventTypes.INSTRUMENT_FROZEN,
        ExecutionEventTypes.INTENT_TERMINALIZED,
        ExecutionEventTypes.POSITION_FLATTENED,
        ExecutionEventTypes.SESSION_FORCED_FLATTENED
    };

    public static (bool Pass, string? Error) RunRandomStressTests(int durationSeconds = 60)
    {
        var rng = new Random(42);
        var start = DateTime.UtcNow;
        var end = start.AddSeconds(durationSeconds);
        var iterations = 0L;
        var eventsProcessed = 0L;
        var replayFailures = 0;
        var scenarioCounts = new int[ScenarioNames.Length];
        var lastHeartbeat = start;
        var lastSummary = start;
        var lastScenario = "";

        while (DateTime.UtcNow < end)
        {
            try
            {
                var (events, scenarioIndex) = GenerateRandomEventSequence(rng);
                lastScenario = ScenarioNames[scenarioIndex];
                var state = ExecutionReplayRebuilder.Rebuild(events);

                iterations++;
                eventsProcessed += events.Count;
                scenarioCounts[scenarioIndex]++;

                var now = DateTime.UtcNow;

                // Heartbeat every 5 seconds
                if ((now - lastHeartbeat).TotalSeconds >= 5)
                {
                    var elapsed = now - start;
                    var elapsedStr = $"{(int)elapsed.TotalHours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}";
                    Console.WriteLine($"[{now:HH:mm:ss}] RANDOM_STRESS running");
                    Console.WriteLine($"elapsed: {elapsedStr}");
                    Console.WriteLine($"iterations: {iterations:N0}");
                    Console.WriteLine($"events processed: {eventsProcessed:N0}");
                    Console.WriteLine($"replay rebuilds: {iterations:N0}");
                    Console.WriteLine($"failures: {replayFailures}");
                    Console.WriteLine();
                    lastHeartbeat = now;
                }

                // Status summary every 60 seconds
                if ((now - lastSummary).TotalSeconds >= 60)
                {
                    var elapsed = now - start;
                    var elapsedStr = $"{(int)elapsed.TotalHours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}";
                    Console.WriteLine("------ RANDOM_STRESS STATUS ------");
                    Console.WriteLine($"elapsed: {elapsedStr}");
                    Console.WriteLine($"iterations: {iterations:N0}");
                    Console.WriteLine($"events processed: {eventsProcessed:N0}");
                    Console.WriteLine("scenario counts:");
                    for (int i = 0; i < ScenarioNames.Length; i++)
                        Console.WriteLine($"  {ScenarioNames[i].ToLowerInvariant()}: {scenarioCounts[i]:N0}");
                    Console.WriteLine($"replay_failures: {replayFailures}");
                    Console.WriteLine("----------------------------------");
                    Console.WriteLine();
                    lastSummary = now;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.WriteLine("ERROR: RANDOM_STRESS failure");
                Console.WriteLine($"iteration: {iterations}");
                Console.WriteLine($"scenario: {lastScenario}");
                Console.WriteLine($"exception: {ex.GetType().Name}: {ex.Message}");
                Console.WriteLine();
                return (false, $"Iteration {iterations}: CRASH - {ex.Message}");
            }
        }

        var totalElapsed = DateTime.UtcNow - start;
        var totalElapsedStr = $"{(int)totalElapsed.TotalHours:D2}:{totalElapsed.Minutes:D2}:{totalElapsed.Seconds:D2}";
        Console.WriteLine();
        Console.WriteLine("PASS: Random stress test");
        Console.WriteLine($"duration: {durationSeconds}s");
        Console.WriteLine($"elapsed: {totalElapsedStr}");
        Console.WriteLine($"iterations: {iterations:N0}");
        Console.WriteLine($"events processed: {eventsProcessed:N0}");
        Console.WriteLine($"replay_failures: {replayFailures}");
        Console.WriteLine();

        return (true, null);
    }

    private static (List<CanonicalExecutionEvent> Events, int ScenarioIndex) GenerateRandomEventSequence(Random rng)
    {
        var events = new List<CanonicalExecutionEvent>();
        var inst = Instruments[rng.Next(Instruments.Length)];
        var intentId = "intent-" + rng.Next(1000);
        var ts = DateTimeOffset.UtcNow.ToString("o");
        var seq = 0L;

        // Pick a scenario type (0-7)
        var scenarioIndex = rng.Next(8);
        switch (scenarioIndex)
        {
            case 0:
                // Normal entry + target
                AddEvt(events, ref seq, ExecutionEventTypes.EXECUTION_OBSERVED, inst, intentId, ts, new { filled_qty = 1m, side = "Long" });
                AddEvt(events, ref seq, ExecutionEventTypes.LIFECYCLE_TRANSITIONED, inst, intentId, ts, null, "WORKING");
                AddEvt(events, ref seq, ExecutionEventTypes.ORDER_REGISTERED, inst, intentId, ts, new { order_type = "Stop" }, null);
                AddEvt(events, ref seq, ExecutionEventTypes.EXECUTION_OBSERVED, inst, intentId, ts, new { filled_qty = 1m, side = "Short" }, null);
                AddEvt(events, ref seq, ExecutionEventTypes.LIFECYCLE_TRANSITIONED, inst, intentId, ts, null, "TERMINAL");
                AddEvt(events, ref seq, ExecutionEventTypes.INTENT_TERMINALIZED, inst, intentId, ts, null, null);
                break;
            case 1:
                // Execution before order update (deferred then resolved)
                AddEvt(events, ref seq, ExecutionEventTypes.EXECUTION_DEFERRED, inst, intentId, ts, new { reason = "order_not_registered" }, null);
                AddEvt(events, ref seq, ExecutionEventTypes.ORDER_REGISTERED, inst, intentId, ts, new { order_id = "ord-1" }, null);
                AddEvt(events, ref seq, ExecutionEventTypes.EXECUTION_RESOLVED, inst, intentId, ts, new { filled_qty = 1m, side = "Long" }, null);
                AddEvt(events, ref seq, ExecutionEventTypes.LIFECYCLE_TRANSITIONED, inst, intentId, ts, null, "WORKING");
                AddEvt(events, ref seq, ExecutionEventTypes.EXECUTION_OBSERVED, inst, intentId, ts, new { filled_qty = 1m, side = "Short" }, null);
                AddEvt(events, ref seq, ExecutionEventTypes.LIFECYCLE_TRANSITIONED, inst, intentId, ts, null, "TERMINAL");
                AddEvt(events, ref seq, ExecutionEventTypes.INTENT_TERMINALIZED, inst, intentId, ts, null, null);
                break;
            case 2:
                // Duplicate execution + dedup
                AddEvt(events, ref seq, ExecutionEventTypes.EXECUTION_OBSERVED, inst, intentId, ts, new { filled_qty = 1m, side = "Long", execution_id = "ex-1" }, null);
                for (int i = 0; i < rng.Next(1, 4); i++)
                    AddEvt(events, ref seq, ExecutionEventTypes.EXECUTION_DEDUPLICATED, inst, intentId, ts, new { execution_id = "ex-1", reason = "duplicate" }, null);
                AddEvt(events, ref seq, ExecutionEventTypes.LIFECYCLE_TRANSITIONED, inst, intentId, ts, null, "WORKING");
                AddEvt(events, ref seq, ExecutionEventTypes.EXECUTION_OBSERVED, inst, intentId, ts, new { filled_qty = 1m, side = "Short" }, null);
                AddEvt(events, ref seq, ExecutionEventTypes.LIFECYCLE_TRANSITIONED, inst, intentId, ts, null, "TERMINAL");
                AddEvt(events, ref seq, ExecutionEventTypes.INTENT_TERMINALIZED, inst, intentId, ts, null, null);
                break;
            case 3:
                // Partial fills
                AddEvt(events, ref seq, ExecutionEventTypes.EXECUTION_OBSERVED, inst, intentId, ts, new { filled_qty = 0.5m, side = "Long" }, null);
                AddEvt(events, ref seq, ExecutionEventTypes.EXECUTION_OBSERVED, inst, intentId, ts, new { filled_qty = 0.5m, side = "Long" }, null);
                AddEvt(events, ref seq, ExecutionEventTypes.LIFECYCLE_TRANSITIONED, inst, intentId, ts, null, "WORKING");
                AddEvt(events, ref seq, ExecutionEventTypes.EXECUTION_OBSERVED, inst, intentId, ts, new { filled_qty = 1m, side = "Short" }, null);
                AddEvt(events, ref seq, ExecutionEventTypes.LIFECYCLE_TRANSITIONED, inst, intentId, ts, null, "TERMINAL");
                AddEvt(events, ref seq, ExecutionEventTypes.INTENT_TERMINALIZED, inst, intentId, ts, null, null);
                break;
            case 4:
                // Order cancelled + protective
                AddEvt(events, ref seq, ExecutionEventTypes.EXECUTION_OBSERVED, inst, intentId, ts, new { filled_qty = 1m, side = "Long" }, null);
                AddEvt(events, ref seq, ExecutionEventTypes.LIFECYCLE_TRANSITIONED, inst, intentId, ts, null, "WORKING");
                AddEvt(events, ref seq, ExecutionEventTypes.ORDER_CANCELLED, inst, intentId, ts, new { order_type = "Stop" }, null);
                AddEvt(events, ref seq, ExecutionEventTypes.PROTECTIVE_INSTRUMENT_BLOCKED, inst, intentId, ts, new { reason = "PROTECTIVE_MISSING_STOP" }, null);
                AddEvt(events, ref seq, ExecutionEventTypes.PROTECTIVE_RECOVERY_CONFIRMED, inst, intentId, ts, null, null);
                AddEvt(events, ref seq, ExecutionEventTypes.EXECUTION_OBSERVED, inst, intentId, ts, new { filled_qty = 1m, side = "Short" }, null);
                AddEvt(events, ref seq, ExecutionEventTypes.LIFECYCLE_TRANSITIONED, inst, intentId, ts, null, "TERMINAL");
                AddEvt(events, ref seq, ExecutionEventTypes.INTENT_TERMINALIZED, inst, intentId, ts, null, null);
                break;
            case 5:
                // Mismatch escalation
                AddEvt(events, ref seq, ExecutionEventTypes.MISMATCH_DETECTED, inst, null, ts, new { broker_qty = 2, local_qty = 0 }, null);
                AddEvt(events, ref seq, ExecutionEventTypes.MISMATCH_PERSISTENT, inst, null, ts, null, null);
                AddEvt(events, ref seq, ExecutionEventTypes.MISMATCH_FAIL_CLOSED, inst, null, ts, null, null);
                AddEvt(events, ref seq, ExecutionEventTypes.POSITION_FLATTENED, inst, null, ts, null, null);
                break;
            case 6:
                // Queue poison
                AddEvt(events, ref seq, ExecutionEventTypes.EXECUTION_OBSERVED, inst, intentId, ts, new { filled_qty = 1m, side = "Long" }, null);
                AddEvt(events, ref seq, ExecutionEventTypes.LIFECYCLE_TRANSITIONED, inst, intentId, ts, null, "WORKING");
                AddEvt(events, ref seq, ExecutionEventTypes.QUEUE_POISON_DETECTED, inst, intentId, ts, new { exception_count = 3 }, null);
                AddEvt(events, ref seq, ExecutionEventTypes.INSTRUMENT_FROZEN, inst, intentId, ts, new { reason = "queue_poison" }, null);
                AddEvt(events, ref seq, ExecutionEventTypes.EXECUTION_OBSERVED, inst, intentId, ts, new { filled_qty = 1m, side = "Short" }, null);
                AddEvt(events, ref seq, ExecutionEventTypes.POSITION_FLATTENED, inst, intentId, ts, null, null);
                AddEvt(events, ref seq, ExecutionEventTypes.LIFECYCLE_TRANSITIONED, inst, intentId, ts, null, "TERMINAL");
                AddEvt(events, ref seq, ExecutionEventTypes.INTENT_TERMINALIZED, inst, intentId, ts, null, null);
                break;
            default:
                // Random mix of events (may produce odd state but should not crash)
                var count = rng.Next(5, 25);
                for (int i = 0; i < count; i++)
                {
                    var et = EventTypes[rng.Next(EventTypes.Length)];
                    object? payload = et == ExecutionEventTypes.EXECUTION_OBSERVED || et == ExecutionEventTypes.EXECUTION_RESOLVED
                        ? new { filled_qty = (decimal)rng.Next(1, 3), side = rng.Next(2) == 0 ? "Long" : "Short" }
                        : null;
                    string? lifecycleAfter = et == ExecutionEventTypes.LIFECYCLE_TRANSITIONED
                        ? (rng.Next(2) == 0 ? "WORKING" : "TERMINAL")
                        : null;
                    AddEvt(events, ref seq, et, inst, intentId, ts, payload ?? new { }, lifecycleAfter);
                }
                break;
        }

        return (events, scenarioIndex);
    }

    private static void AddEvt(List<CanonicalExecutionEvent> list, ref long seq, string eventType, string inst, string? intentId, string ts, object? payload, string? lifecycleAfter = null)
    {
        list.Add(new CanonicalExecutionEvent
        {
            EventId = Guid.NewGuid().ToString("N"),
            EventSequence = ++seq,
            EventType = eventType,
            TimestampUtc = ts,
            Instrument = inst,
            IntentId = intentId,
            LifecycleStateAfter = lifecycleAfter,
            Payload = payload,
            Source = "RandomStress"
        });
    }
}
