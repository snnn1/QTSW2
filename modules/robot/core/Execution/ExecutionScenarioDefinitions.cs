// Execution scenario definitions for integrated system-level harness testing.
// Validates execution ordering, queue poison, protective coverage, mismatch escalation, and replay parity.

using System;
using System.Collections.Generic;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Expected final state for scenario validation.
/// </summary>
public sealed class ExpectedFinalState
{
    public string? LifecycleState { get; set; }
    public decimal BrokerExposure { get; set; }
    public bool ProtectiveBlock { get; set; }
    public string MismatchState { get; set; } = "NONE";
    public bool Terminal { get; set; }
    public decimal OpenQuantity { get; set; }
    public bool InstrumentBlocked { get; set; }
    public bool PositionFlattened { get; set; }
    public string? IntentId { get; set; }
}

/// <summary>
/// Single event step in a scenario. Maps to CanonicalExecutionEvent.
/// </summary>
public sealed class EventStep
{
    public string Type { get; set; } = "";
    public string? Instrument { get; set; }
    public decimal? Quantity { get; set; }
    public decimal? Price { get; set; }
    public string? IntentId { get; set; }
    public string? LifecycleStateBefore { get; set; }
    public string? LifecycleStateAfter { get; set; }
    public object? Payload { get; set; }
    public string? CommandId { get; set; }
    public string? Side { get; set; }
}

/// <summary>
/// Scenario definition: name, event sequence, expected final state.
/// </summary>
public sealed class ScenarioDefinition
{
    public string Name { get; set; } = "";
    public List<EventStep> EventSteps { get; set; } = new();
    public ExpectedFinalState ExpectedState { get; set; } = new();
}

/// <summary>
/// All execution scenario definitions in test order.
/// </summary>
public static class ExecutionScenarioDefinitions
{
    public const string SCENARIO_NORMAL_ENTRY_TARGET = "SCENARIO_NORMAL_ENTRY_TARGET";
    public const string SCENARIO_EXECUTION_BEFORE_ORDER_UPDATE = "SCENARIO_EXECUTION_BEFORE_ORDER_UPDATE";
    public const string SCENARIO_DUPLICATE_EXECUTION = "SCENARIO_DUPLICATE_EXECUTION";
    public const string SCENARIO_STOP_CANCEL_RECOVERY = "SCENARIO_STOP_CANCEL_RECOVERY";
    public const string SCENARIO_STOP_CANCEL_FLATTEN = "SCENARIO_STOP_CANCEL_FLATTEN";
    public const string SCENARIO_PERSISTENT_MISMATCH = "SCENARIO_PERSISTENT_MISMATCH";
    public const string SCENARIO_RESTART_RECONSTRUCTION = "SCENARIO_RESTART_RECONSTRUCTION";
    public const string SCENARIO_QUEUE_POISON_FLATTEN = "SCENARIO_QUEUE_POISON_FLATTEN";
    public const string SCENARIO_SESSION_FORCED_FLATTEN = "SCENARIO_SESSION_FORCED_FLATTEN";

    public static IReadOnlyList<ScenarioDefinition> GetAll()
    {
        return new List<ScenarioDefinition>
        {
            Scenario1_NormalEntryTarget(),
            Scenario2_ExecutionBeforeOrderUpdate(),
            Scenario3_DuplicateExecution(),
            Scenario4_StopCancelRecovery(),
            Scenario5_StopCancelFlatten(),
            Scenario6_PersistentMismatch(),
            Scenario7_RestartReconstruction(),
            Scenario8_QueuePoisonFlatten(),
            Scenario9_SessionForcedFlatten()
        };
    }

    /// <summary>
    /// Chaos scenarios: real-world failure simulations (Tests B, C, D, E, F).
    /// </summary>
    public static IReadOnlyList<ScenarioDefinition> GetChaosScenarios()
    {
        var all = GetAll();
        return new[]
        {
            all[3], // Scenario4: Stop cancel recovery (Test B)
            all[4], // Scenario5: Stop cancel flatten (Test C)
            all[5], // Scenario6: Persistent mismatch (Test D)
            all[7], // Scenario8: Queue poison (Test E)
            all[8]  // Scenario9: Session forced flatten (Test F)
        };
    }

    private static ScenarioDefinition Scenario1_NormalEntryTarget()
    {
        const string intentId = "intent-normal-1";
        return new ScenarioDefinition
        {
            Name = SCENARIO_NORMAL_ENTRY_TARGET,
            EventSteps = new List<EventStep>
            {
                new() { Type = ExecutionEventTypes.COMMAND_RECEIVED, Instrument = "ES", CommandId = "cmd-1" },
                new() { Type = ExecutionEventTypes.COMMAND_DISPATCHED, Instrument = "ES", CommandId = "cmd-1" },
                new() { Type = ExecutionEventTypes.EXECUTION_OBSERVED, Instrument = "ES", IntentId = intentId, Quantity = 1, Side = "Long", Payload = new { filled_qty = 1m, side = "Long" } },
                new() { Type = ExecutionEventTypes.LIFECYCLE_TRANSITIONED, Instrument = "ES", IntentId = intentId, LifecycleStateAfter = "WORKING" },
                new() { Type = ExecutionEventTypes.ORDER_REGISTERED, Instrument = "ES", Payload = new { order_type = "Stop", order_type2 = "Target" } },
                new() { Type = ExecutionEventTypes.EXECUTION_OBSERVED, Instrument = "ES", IntentId = intentId, Quantity = 1, Side = "Short", Payload = new { filled_qty = 1m, side = "Short" } },
                new() { Type = ExecutionEventTypes.LIFECYCLE_TRANSITIONED, Instrument = "ES", IntentId = intentId, LifecycleStateAfter = "TERMINAL" },
                new() { Type = ExecutionEventTypes.INTENT_TERMINALIZED, Instrument = "ES", IntentId = intentId }
            },
            ExpectedState = new ExpectedFinalState
            {
                LifecycleState = "TERMINAL",
                BrokerExposure = 0,
                ProtectiveBlock = false,
                MismatchState = "NONE",
                Terminal = true,
                OpenQuantity = 0,
                InstrumentBlocked = false,
                PositionFlattened = false,
                IntentId = intentId
            }
        };
    }

    private static ScenarioDefinition Scenario2_ExecutionBeforeOrderUpdate()
    {
        const string intentId = "intent-order-1";
        return new ScenarioDefinition
        {
            Name = SCENARIO_EXECUTION_BEFORE_ORDER_UPDATE,
            EventSteps = new List<EventStep>
            {
                new() { Type = ExecutionEventTypes.EXECUTION_DEFERRED, Instrument = "ES", IntentId = intentId, Payload = new { execution_id = "ex-1", reason = "order_not_registered" } },
                new() { Type = ExecutionEventTypes.ORDER_REGISTERED, Instrument = "ES", IntentId = intentId, Payload = new { order_id = "ord-1" } },
                new() { Type = ExecutionEventTypes.EXECUTION_RESOLVED, Instrument = "ES", IntentId = intentId, Quantity = 1, Side = "Long", Payload = new { filled_qty = 1m, side = "Long" } },
                new() { Type = ExecutionEventTypes.LIFECYCLE_TRANSITIONED, Instrument = "ES", IntentId = intentId, LifecycleStateAfter = "WORKING" },
                new() { Type = ExecutionEventTypes.EXECUTION_OBSERVED, Instrument = "ES", IntentId = intentId, Quantity = 1, Side = "Short", Payload = new { filled_qty = 1m, side = "Short" } },
                new() { Type = ExecutionEventTypes.LIFECYCLE_TRANSITIONED, Instrument = "ES", IntentId = intentId, LifecycleStateAfter = "TERMINAL" },
                new() { Type = ExecutionEventTypes.INTENT_TERMINALIZED, Instrument = "ES", IntentId = intentId }
            },
            ExpectedState = new ExpectedFinalState
            {
                LifecycleState = "TERMINAL",
                BrokerExposure = 0,
                ProtectiveBlock = false,
                MismatchState = "NONE",
                Terminal = true,
                OpenQuantity = 0,
                InstrumentBlocked = false,
                PositionFlattened = false,
                IntentId = intentId
            }
        };
    }

    private static ScenarioDefinition Scenario3_DuplicateExecution()
    {
        const string intentId = "intent-dup-1";
        return new ScenarioDefinition
        {
            Name = SCENARIO_DUPLICATE_EXECUTION,
            EventSteps = new List<EventStep>
            {
                new() { Type = ExecutionEventTypes.EXECUTION_OBSERVED, Instrument = "ES", IntentId = intentId, Quantity = 1, Side = "Long", Payload = new { filled_qty = 1m, side = "Long", execution_id = "ex-1" } },
                new() { Type = ExecutionEventTypes.EXECUTION_DEDUPLICATED, Instrument = "ES", IntentId = intentId, Payload = new { execution_id = "ex-1", reason = "duplicate" } },
                new() { Type = ExecutionEventTypes.EXECUTION_DEDUPLICATED, Instrument = "ES", IntentId = intentId, Payload = new { execution_id = "ex-1", reason = "duplicate" } },
                new() { Type = ExecutionEventTypes.LIFECYCLE_TRANSITIONED, Instrument = "ES", IntentId = intentId, LifecycleStateAfter = "WORKING" },
                new() { Type = ExecutionEventTypes.EXECUTION_OBSERVED, Instrument = "ES", IntentId = intentId, Quantity = 1, Side = "Short", Payload = new { filled_qty = 1m, side = "Short" } },
                new() { Type = ExecutionEventTypes.LIFECYCLE_TRANSITIONED, Instrument = "ES", IntentId = intentId, LifecycleStateAfter = "TERMINAL" },
                new() { Type = ExecutionEventTypes.INTENT_TERMINALIZED, Instrument = "ES", IntentId = intentId }
            },
            ExpectedState = new ExpectedFinalState
            {
                LifecycleState = "TERMINAL",
                BrokerExposure = 0,
                ProtectiveBlock = false,
                MismatchState = "NONE",
                Terminal = true,
                OpenQuantity = 0,
                InstrumentBlocked = false,
                PositionFlattened = false,
                IntentId = intentId
            }
        };
    }

    private static ScenarioDefinition Scenario4_StopCancelRecovery()
    {
        const string intentId = "intent-stop-1";
        return new ScenarioDefinition
        {
            Name = SCENARIO_STOP_CANCEL_RECOVERY,
            EventSteps = new List<EventStep>
            {
                new() { Type = ExecutionEventTypes.EXECUTION_OBSERVED, Instrument = "ES", IntentId = intentId, Quantity = 1, Side = "Long", Payload = new { filled_qty = 1m, side = "Long" } },
                new() { Type = ExecutionEventTypes.LIFECYCLE_TRANSITIONED, Instrument = "ES", IntentId = intentId, LifecycleStateAfter = "WORKING" },
                new() { Type = ExecutionEventTypes.ORDER_CANCELLED, Instrument = "ES", Payload = new { order_type = "Stop" } },
                new() { Type = ExecutionEventTypes.PROTECTIVE_INSTRUMENT_BLOCKED, Instrument = "ES", Payload = new { reason = "PROTECTIVE_MISSING_STOP" } },
                new() { Type = ExecutionEventTypes.PROTECTIVE_RECOVERY_SUBMITTED, Instrument = "ES", Payload = new { } },
                new() { Type = ExecutionEventTypes.PROTECTIVE_RECOVERY_CONFIRMED, Instrument = "ES", Payload = new { } },
                new() { Type = ExecutionEventTypes.EXECUTION_OBSERVED, Instrument = "ES", IntentId = intentId, Quantity = 1, Side = "Short", Payload = new { filled_qty = 1m, side = "Short" } },
                new() { Type = ExecutionEventTypes.LIFECYCLE_TRANSITIONED, Instrument = "ES", IntentId = intentId, LifecycleStateAfter = "TERMINAL" },
                new() { Type = ExecutionEventTypes.INTENT_TERMINALIZED, Instrument = "ES", IntentId = intentId }
            },
            ExpectedState = new ExpectedFinalState
            {
                LifecycleState = "TERMINAL",
                BrokerExposure = 0,
                ProtectiveBlock = false,
                MismatchState = "NONE",
                Terminal = true,
                OpenQuantity = 0,
                InstrumentBlocked = false,
                PositionFlattened = false,
                IntentId = intentId
            }
        };
    }

    private static ScenarioDefinition Scenario5_StopCancelFlatten()
    {
        const string intentId = "intent-flat-1";
        return new ScenarioDefinition
        {
            Name = SCENARIO_STOP_CANCEL_FLATTEN,
            EventSteps = new List<EventStep>
            {
                new() { Type = ExecutionEventTypes.EXECUTION_OBSERVED, Instrument = "ES", IntentId = intentId, Quantity = 1, Side = "Long", Payload = new { filled_qty = 1m, side = "Long" } },
                new() { Type = ExecutionEventTypes.LIFECYCLE_TRANSITIONED, Instrument = "ES", IntentId = intentId, LifecycleStateAfter = "WORKING" },
                new() { Type = ExecutionEventTypes.ORDER_CANCELLED, Instrument = "ES", Payload = new { order_type = "Stop" } },
                new() { Type = ExecutionEventTypes.PROTECTIVE_INSTRUMENT_BLOCKED, Instrument = "ES", Payload = new { reason = "PROTECTIVE_MISSING_STOP" } },
                new() { Type = ExecutionEventTypes.PROTECTIVE_RECOVERY_FAILED, Instrument = "ES", Payload = new { reason = "NO_SAFE_STOP_PRICE" } },
                new() { Type = ExecutionEventTypes.PROTECTIVE_EMERGENCY_FLATTEN_TRIGGERED, Instrument = "ES", Payload = new { } },
                new() { Type = ExecutionEventTypes.EXECUTION_OBSERVED, Instrument = "ES", IntentId = intentId, Quantity = 1, Side = "Short", Payload = new { filled_qty = 1m, side = "Short" } },
                new() { Type = ExecutionEventTypes.POSITION_FLATTENED, Instrument = "ES", Payload = new { } },
                new() { Type = ExecutionEventTypes.PROTECTIVE_FLATTEN_COMPLETED, Instrument = "ES", Payload = new { } },
                new() { Type = ExecutionEventTypes.PROTECTIVE_INSTRUMENT_BLOCKED, Instrument = "ES", Payload = new { reason = "LOCKED_FAIL_CLOSED" } },
                new() { Type = ExecutionEventTypes.LIFECYCLE_TRANSITIONED, Instrument = "ES", IntentId = intentId, LifecycleStateAfter = "TERMINAL" },
                new() { Type = ExecutionEventTypes.INTENT_TERMINALIZED, Instrument = "ES", IntentId = intentId }
            },
            ExpectedState = new ExpectedFinalState
            {
                LifecycleState = "TERMINAL",
                BrokerExposure = 0,
                ProtectiveBlock = true,
                MismatchState = "NONE",
                Terminal = true,
                OpenQuantity = 0,
                InstrumentBlocked = true,
                PositionFlattened = true,
                IntentId = intentId
            }
        };
    }

    private static ScenarioDefinition Scenario6_PersistentMismatch()
    {
        return new ScenarioDefinition
        {
            Name = SCENARIO_PERSISTENT_MISMATCH,
            EventSteps = new List<EventStep>
            {
                new() { Type = ExecutionEventTypes.MISMATCH_DETECTED, Instrument = "ES", Payload = new { broker_qty = 2, local_qty = 0, mismatch_type = "POSITION_QTY_MISMATCH" } },
                new() { Type = ExecutionEventTypes.MISMATCH_PERSISTENT, Instrument = "ES", Payload = new { } },
                new() { Type = ExecutionEventTypes.MISMATCH_FAIL_CLOSED, Instrument = "ES", Payload = new { } },
                new() { Type = ExecutionEventTypes.POSITION_FLATTENED, Instrument = "ES", Payload = new { } }
            },
            ExpectedState = new ExpectedFinalState
            {
                LifecycleState = null,
                BrokerExposure = 0,
                ProtectiveBlock = false,
                MismatchState = "FAIL_CLOSED",
                Terminal = false,
                OpenQuantity = 0,
                InstrumentBlocked = true,
                PositionFlattened = true,
                IntentId = null
            }
        };
    }

    private static ScenarioDefinition Scenario7_RestartReconstruction()
    {
        const string intentId = "intent-restart-1";
        return new ScenarioDefinition
        {
            Name = SCENARIO_RESTART_RECONSTRUCTION,
            EventSteps = new List<EventStep>
            {
                // Simulates: entry fill, robot restart, rebuild from canonical events, protectives present
                new() { Type = ExecutionEventTypes.EXECUTION_OBSERVED, Instrument = "ES", IntentId = intentId, Quantity = 1, Side = "Long", Payload = new { filled_qty = 1m, side = "Long" } },
                new() { Type = ExecutionEventTypes.LIFECYCLE_TRANSITIONED, Instrument = "ES", IntentId = intentId, LifecycleStateAfter = "WORKING" },
                new() { Type = ExecutionEventTypes.ORDER_REGISTERED, Instrument = "ES", Payload = new { order_type = "Stop", order_type2 = "Target" } },
                new() { Type = ExecutionEventTypes.EXECUTION_OBSERVED, Instrument = "ES", IntentId = intentId, Quantity = 1, Side = "Short", Payload = new { filled_qty = 1m, side = "Short" } },
                new() { Type = ExecutionEventTypes.LIFECYCLE_TRANSITIONED, Instrument = "ES", IntentId = intentId, LifecycleStateAfter = "TERMINAL" },
                new() { Type = ExecutionEventTypes.INTENT_TERMINALIZED, Instrument = "ES", IntentId = intentId }
            },
            ExpectedState = new ExpectedFinalState
            {
                LifecycleState = "TERMINAL",
                BrokerExposure = 0,
                ProtectiveBlock = false,
                MismatchState = "NONE",
                Terminal = true,
                OpenQuantity = 0,
                InstrumentBlocked = false,
                PositionFlattened = false,
                IntentId = intentId
            }
        };
    }

    private static ScenarioDefinition Scenario8_QueuePoisonFlatten()
    {
        const string intentId = "intent-poison-1";
        return new ScenarioDefinition
        {
            Name = SCENARIO_QUEUE_POISON_FLATTEN,
            EventSteps = new List<EventStep>
            {
                new() { Type = ExecutionEventTypes.EXECUTION_OBSERVED, Instrument = "ES", IntentId = intentId, Quantity = 1, Side = "Long", Payload = new { filled_qty = 1m, side = "Long" } },
                new() { Type = ExecutionEventTypes.LIFECYCLE_TRANSITIONED, Instrument = "ES", IntentId = intentId, LifecycleStateAfter = "WORKING" },
                new() { Type = ExecutionEventTypes.QUEUE_POISON_DETECTED, Instrument = "ES", Payload = new { exception_count = 3 } },
                new() { Type = ExecutionEventTypes.INSTRUMENT_FROZEN, Instrument = "ES", Payload = new { reason = "queue_poison" } },
                new() { Type = ExecutionEventTypes.EXECUTION_OBSERVED, Instrument = "ES", IntentId = intentId, Quantity = 1, Side = "Short", Payload = new { filled_qty = 1m, side = "Short" } },
                new() { Type = ExecutionEventTypes.POSITION_FLATTENED, Instrument = "ES", Payload = new { } },
                new() { Type = ExecutionEventTypes.LIFECYCLE_TRANSITIONED, Instrument = "ES", IntentId = intentId, LifecycleStateAfter = "TERMINAL" },
                new() { Type = ExecutionEventTypes.INTENT_TERMINALIZED, Instrument = "ES", IntentId = intentId }
            },
            ExpectedState = new ExpectedFinalState
            {
                LifecycleState = "TERMINAL",
                BrokerExposure = 0,
                ProtectiveBlock = true,
                MismatchState = "NONE",
                Terminal = true,
                OpenQuantity = 0,
                InstrumentBlocked = true,
                PositionFlattened = true,
                IntentId = intentId
            }
        };
    }

    /// <summary>
    /// Chaos Test F: Session forced flatten near close. Validates SESSION_FORCED_FLATTEN_TRIGGERED
    /// → SESSION_FORCED_FLATTEN_SUBMITTED (path=immediate) → SESSION_FORCED_FLATTENED.
    /// </summary>
    private static ScenarioDefinition Scenario9_SessionForcedFlatten()
    {
        const string intentId = "intent-ff-1";
        return new ScenarioDefinition
        {
            Name = SCENARIO_SESSION_FORCED_FLATTEN,
            EventSteps = new List<EventStep>
            {
                new() { Type = ExecutionEventTypes.EXECUTION_OBSERVED, Instrument = "ES", IntentId = intentId, Quantity = 1, Side = "Long", Payload = new { filled_qty = 1m, side = "Long" } },
                new() { Type = ExecutionEventTypes.LIFECYCLE_TRANSITIONED, Instrument = "ES", IntentId = intentId, LifecycleStateAfter = "WORKING" },
                new() { Type = ExecutionEventTypes.ORDER_REGISTERED, Instrument = "ES", Payload = new { order_type = "Stop", order_type2 = "Target" } },
                new() { Type = ExecutionEventTypes.SESSION_FORCED_FLATTEN_TRIGGERED, Instrument = "ES", Payload = new { path = "immediate", FlattenTriggerUtc = "2026-03-04T21:55:00Z" } },
                new() { Type = ExecutionEventTypes.SESSION_FORCED_FLATTEN_SUBMITTED, Instrument = "ES", Payload = new { path = "immediate" } },
                new() { Type = ExecutionEventTypes.EXECUTION_OBSERVED, Instrument = "ES", IntentId = intentId, Quantity = 1, Side = "Short", Payload = new { filled_qty = 1m, side = "Short" } },
                new() { Type = ExecutionEventTypes.SESSION_FORCED_FLATTENED, Instrument = "ES", Payload = new { } },
                new() { Type = ExecutionEventTypes.LIFECYCLE_TRANSITIONED, Instrument = "ES", IntentId = intentId, LifecycleStateAfter = "TERMINAL" },
                new() { Type = ExecutionEventTypes.INTENT_TERMINALIZED, Instrument = "ES", IntentId = intentId }
            },
            ExpectedState = new ExpectedFinalState
            {
                LifecycleState = "TERMINAL",
                BrokerExposure = 0,
                ProtectiveBlock = false,
                MismatchState = "NONE",
                Terminal = true,
                OpenQuantity = 0,
                InstrumentBlocked = false,
                PositionFlattened = true,
                IntentId = intentId
            }
        };
    }
}
