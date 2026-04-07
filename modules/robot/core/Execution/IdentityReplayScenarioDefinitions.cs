// Identity-focused replay scenarios: deterministic canonical intent IDs, no live trading.
// Used by IdentityReplayScenarioRunner and documentation (docs/robot/IDENTITY_REPLAY_SCENARIOS.md).

using System;
using System.Collections.Generic;

namespace QTSW2.Robot.Core.Execution;

/// <summary>Names for identity replay / validation scenarios.</summary>
public static class IdentityReplayScenarioNames
{
    public const string S1_NORMAL_LIFECYCLE = "IDENTITY_S1_NORMAL_LIFECYCLE_REPLAY";
    public const string S2_JOURNAL_PERSISTENCE = "IDENTITY_S2_JOURNAL_PERSISTENCE";
    public const string S3_REENTRY_CANONICAL = "IDENTITY_S3_REENTRY_CANONICAL";
    public const string S4_FLATTEN_SAME_ID = "IDENTITY_S4_FLATTEN_SAME_INTENT_ID";
    public const string S5_SUBMIT_NOT_IN_MAP = "IDENTITY_S5_SUBMIT_NOT_IN_MAP";
    public const string S6_KEY_MISMATCH = "IDENTITY_S6_INTENT_KEY_MISMATCH";
    public const string S7_AGG_TAG = "IDENTITY_S7_AGG_TAG_ATTRIBUTION";
    /// <summary>Full journal → <see cref="NinjaTraderSimAdapter"/> hydration (restart simulation).</summary>
    public const string S8_FULL_HYDRATION_RECONSTRUCTION = "IDENTITY_S8_FULL_HYDRATION_RECONSTRUCTION";
}

/// <summary>Builds <see cref="ScenarioDefinition"/> instances and deterministic <see cref="Intent"/> for identity tests.</summary>
public static class IdentityReplayScenarioDefinitions
{
    /// <summary>Fixed clock for reproducible <see cref="ExecutionJournal.ComputeIntentId"/>.</summary>
    public static DateTimeOffset DeterministicEntryUtc { get; } =
        DateTimeOffset.Parse("2026-04-06T15:30:00Z", System.Globalization.CultureInfo.InvariantCulture);

    public const string DeterministicTradingDate = "2026-04-06";

    /// <summary>Canonical instrument stream and fields aligned with journal filename conventions.</summary>
    public static Intent BuildDeterministicIntent(string triggerReason)
    {
        return new Intent(
            DeterministicTradingDate,
            "MES_S1",
            "MES",
            "MES",
            "S1",
            "09:00",
            "Long",
            4500.00m,
            4400.00m,
            4600.00m,
            4450.00m,
            DeterministicEntryUtc,
            triggerReason);
    }

    /// <summary>16-hex canonical id for <see cref="BuildDeterministicIntent"/>.</summary>
    public static string DeterministicCanonicalIntentId => BuildDeterministicIntent("IDENTITY_REPLAY").ComputeIntentId();

    /// <summary>Stream/instrument pair aligned with <c>DeriveCanonicalFromStream</c> (e.g. <c>ES_S1</c> → <c>ES</c>), not <c>MES_S1</c> → <c>ME</c>.</summary>
    public const string HydrationTradeStream = "ES_S1";

    public const string HydrationExecutionInstrument = "ES";

    /// <summary>BE trigger matching <see cref="NinjaTraderSimAdapter"/> hydration reconstruction.</summary>
    public static decimal HydrationComputeBeTrigger(decimal entryPrice, decimal targetPrice, string direction)
    {
        var dist = Math.Abs(targetPrice - entryPrice);
        var bePts = dist * 0.65m;
        return direction == "Long" ? entryPrice + bePts : entryPrice - bePts;
    }

    /// <summary>
    /// Intent fields must match what <c>CreateIntentFromJournalEntry</c> builds from the same journal row
    /// (same prices, session/slot as reconstructed, <see cref="HydrationComputeBeTrigger"/>).
    /// </summary>
    public static Intent BuildHydrationTradeIntent(string triggerReason)
    {
        const decimal ep = 4500.00m;
        const decimal sp = 4400.00m;
        const decimal tp = 4600.00m;
        var be = HydrationComputeBeTrigger(ep, tp, "Long");
        return new Intent(
            DeterministicTradingDate,
            HydrationTradeStream,
            HydrationExecutionInstrument,
            HydrationExecutionInstrument,
            "S1",
            "09:00",
            "Long",
            ep,
            sp,
            tp,
            be,
            DeterministicEntryUtc,
            triggerReason);
    }

    /// <summary>
    /// Replay scenario: same canonical intent id on every step (normal entry → protectives → exit).
    /// Mirrors <see cref="ExecutionScenarioDefinitions.Scenario1_NormalEntryTarget"/> shape with real hash id.
    /// </summary>
    public static ScenarioDefinition GetScenario1_NormalLifecycleIdentity()
    {
        var intentId = DeterministicCanonicalIntentId;
        return new ScenarioDefinition
        {
            Name = IdentityReplayScenarioNames.S1_NORMAL_LIFECYCLE,
            EventSteps = new List<EventStep>
            {
                new() { Type = ExecutionEventTypes.COMMAND_RECEIVED, Instrument = "MES", CommandId = "cmd-identity-1" },
                new() { Type = ExecutionEventTypes.COMMAND_DISPATCHED, Instrument = "MES", CommandId = "cmd-identity-1" },
                new()
                {
                    Type = ExecutionEventTypes.EXECUTION_OBSERVED, Instrument = "MES", IntentId = intentId, Quantity = 1, Side = "Long",
                    Payload = new { filled_qty = 1m, side = "Long" }
                },
                new()
                {
                    Type = ExecutionEventTypes.LIFECYCLE_TRANSITIONED, Instrument = "MES", IntentId = intentId,
                    LifecycleStateAfter = "WORKING"
                },
                new() { Type = ExecutionEventTypes.ORDER_REGISTERED, Instrument = "MES", Payload = new { order_type = "Stop", order_type2 = "Target" } },
                new()
                {
                    Type = ExecutionEventTypes.EXECUTION_OBSERVED, Instrument = "MES", IntentId = intentId, Quantity = 1, Side = "Short",
                    Payload = new { filled_qty = 1m, side = "Short" }
                },
                new()
                {
                    Type = ExecutionEventTypes.LIFECYCLE_TRANSITIONED, Instrument = "MES", IntentId = intentId,
                    LifecycleStateAfter = "TERMINAL"
                },
                new() { Type = ExecutionEventTypes.INTENT_TERMINALIZED, Instrument = "MES", IntentId = intentId }
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
}
