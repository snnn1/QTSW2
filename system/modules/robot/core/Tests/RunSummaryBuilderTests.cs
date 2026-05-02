using System;
using System.IO;
using QTSW2.Robot.Core.Execution;

namespace QTSW2.Robot.Core.Tests;

public static class RunSummaryBuilderTests
{
    public static (bool Pass, string? Error) RunAll()
    {
        var root = Path.Combine(Path.GetTempPath(), "run_summary_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var ts = "2026-04-26T18:46:32.4016098+00:00";
            var keyPath = Path.Combine(root, RunRootArtifacts.KeyEventsFileName);
            File.WriteAllLines(keyPath, new[]
            {
                "{\"ts_utc\":\"2026-04-13T20:55:00.0000000+00:00\",\"event\":\"FLATTEN_REQUESTED\",\"instrument\":\"MNG\",\"data\":{\"correlation_id\":\"SESSION_CLOSE_FLATTEN:intent-a:20260413205500000\"}}",
                "{\"ts_utc\":\"2026-04-13T20:55:00.0000000+00:00\",\"event\":\"FLATTEN_SUBMITTED\",\"instrument\":\"MNG\",\"data\":{\"correlation_id\":\"SESSION_CLOSE_FLATTEN:intent-a:20260413205500000\",\"success\":true}}",
                "{\"ts_utc\":\"" + ts + "\",\"event\":\"FLATTEN_CONFIRMED\",\"instrument\":\"MNG\",\"reason\":\"LATE_SESSION_CLOSE_CONFIRM\",\"data\":{\"correlation_id\":\"SESSION_CLOSE_FLATTEN:intent-a:20260413205500000\",\"original_intent_id\":\"intent-a\"}}",
                "{\"ts_utc\":\"" + ts + "\",\"event\":\"FLATTEN_CONFIRMED\",\"instrument\":\"MNG\",\"reason\":\"LATE_SESSION_CLOSE_CONFIRM\",\"data\":{\"correlation_id\":\"FLATTEN:MNG:20260413205500000:L0\",\"original_intent_id\":\"intent-a\"}}"
            });

            var doc = RunSummaryBuilder.Build(
                root,
                "summary-test",
                DateTimeOffset.Parse("2026-04-26T18:00:00+00:00"),
                ExecutionMode.SIM,
                new[] { "MNG" },
                new ExecutionSummarySnapshot());

            if (doc.key_counts.flatten_confirmed != 1)
                return (false, $"expected duplicate late flatten confirms to count once, got {doc.key_counts.flatten_confirmed}");
            if (doc.status != "WARN" || doc.status_reason != "FLATTEN_OCCURRED")
                return (false, $"expected late confirmed flatten to be WARN/FLATTEN_OCCURRED, got {doc.status}/{doc.status_reason}");

            var brokerFlatProofRoot = Path.Combine(root, "broker_flat_proof_summary");
            Directory.CreateDirectory(brokerFlatProofRoot);
            File.WriteAllText(Path.Combine(brokerFlatProofRoot, RunRootArtifacts.KeyEventsFileName), "");
            var brokerFlatProofDecisionDir = RobotRunArtifactPaths.DecisionsDir(brokerFlatProofRoot);
            Directory.CreateDirectory(brokerFlatProofDecisionDir);
            File.WriteAllText(Path.Combine(brokerFlatProofDecisionDir, "flatten_broker_flat_completions.jsonl"),
                "{\"Utc\":\"\\/Date(1777555578977)\\/\",\"Instrument\":\"MNG\",\"CanonicalBrokerKey\":\"MNG\",\"ReconciliationAbsRemaining\":0,\"Proof\":\"BROKER_CANONICAL_RECONCILIATION_ABS_ZERO\",\"CorrelationId\":\"SESSION_CLOSE_FLATTEN:intent-a:20260414205500000\",\"EpisodeId\":\"episode-a\",\"Source\":\"ADAPTER_VERIFY\"}" + Environment.NewLine);

            var brokerFlatProofDoc = RunSummaryBuilder.Build(
                brokerFlatProofRoot,
                "broker-flat-proof-summary-test",
                DateTimeOffset.Parse("2026-04-26T18:00:00+00:00"),
                ExecutionMode.SIM,
                new[] { "MNG" },
                new ExecutionSummarySnapshot());

            if (brokerFlatProofDoc.status != "OK" ||
                brokerFlatProofDoc.status_reason != "NORMAL_RUN" ||
                brokerFlatProofDoc.verdict_class != "GREEN")
            {
                return (false, $"expected broker-flat verification proof to stay green, got {brokerFlatProofDoc.status}/{brokerFlatProofDoc.status_reason}/{brokerFlatProofDoc.verdict_class}");
            }
            if (brokerFlatProofDoc.flags.had_flatten ||
                brokerFlatProofDoc.key_counts.flatten_confirmed != 0)
            {
                return (false, "expected broker-flat verification proof not to count as flatten activity");
            }

            var flattenJournalRoot = Path.Combine(root, "flatten_journal_summary");
            Directory.CreateDirectory(flattenJournalRoot);
            File.WriteAllText(Path.Combine(flattenJournalRoot, RunRootArtifacts.KeyEventsFileName), "");
            var flattenJournalDir = RobotRunArtifactPaths.StateExecutionJournals(flattenJournalRoot);
            Directory.CreateDirectory(flattenJournalDir);
            File.WriteAllText(Path.Combine(flattenJournalDir, "2026-04-13_NG2_session-flatten-intent.json"), JsonUtil.Serialize(new ExecutionJournalEntry
            {
                IntentId = "session-flatten-intent",
                TradingDate = "2026-04-13",
                Stream = "NG2",
                Instrument = "MNG",
                EntrySubmitted = true,
                EntryFilled = true,
                EntryFilledQuantityTotal = 2,
                ExitFilledQuantityTotal = 2,
                TradeCompleted = true,
                Direction = "Short",
                ExitOrderType = "FLATTEN",
                CompletionReason = "FLATTEN",
                ExitFilledAtUtc = "2026-04-26T21:00:00.0000000Z",
                CompletedAtUtc = "2026-04-26T21:00:00.0000000Z"
            }));

            var flattenJournalDoc = RunSummaryBuilder.Build(
                flattenJournalRoot,
                "flatten-journal-summary-test",
                DateTimeOffset.Parse("2026-04-26T18:00:00+00:00"),
                ExecutionMode.SIM,
                new[] { "MNG" },
                new ExecutionSummarySnapshot());

            if (flattenJournalDoc.status != "WARN" ||
                flattenJournalDoc.status_reason != "FLATTEN_OCCURRED" ||
                flattenJournalDoc.verdict_class != "OPERATOR_REVIEW")
            {
                return (false, $"expected completed flatten journal to be WARN/FLATTEN_OCCURRED/OPERATOR_REVIEW, got {flattenJournalDoc.status}/{flattenJournalDoc.status_reason}/{flattenJournalDoc.verdict_class}");
            }
            if (!flattenJournalDoc.flags.had_flatten ||
                flattenJournalDoc.key_counts.flatten_confirmed != 1 ||
                flattenJournalDoc.key_counts.completed_filled_trade_journals != 1 ||
                flattenJournalDoc.key_counts.unique_entry_filled_intents != 1)
            {
                return (false, "expected completed flatten journal to count as one flatten-confirmed completed trade");
            }

            var journalDir = RobotRunArtifactPaths.StateExecutionJournals(root);
            Directory.CreateDirectory(journalDir);
            File.WriteAllText(Path.Combine(journalDir, "2026-04-13_NG2_open-intent.json"), JsonUtil.Serialize(new ExecutionJournalEntry
            {
                IntentId = "open-intent",
                TradingDate = "2026-04-13",
                Stream = "NG2",
                Instrument = "MNG",
                EntrySubmitted = true,
                EntryFilled = true,
                EntryFilledQuantityTotal = 2,
                ExitFilledQuantityTotal = 0,
                TradeCompleted = false,
                Direction = "Short"
            }));

            File.WriteAllText(Path.Combine(root, RunRootArtifacts.RunShutdownSignalFileName),
                "{\"ts_utc\":\"2026-04-26T18:46:40.0000000+00:00\",\"reason\":\"engine_stop\",\"source\":\"test\"}");

            var snapshotDir = RobotRunArtifactPaths.EventsOwnershipSnapshotsTradingDate(root, "2026-04-13");
            Directory.CreateDirectory(snapshotDir);
            File.WriteAllText(Path.Combine(snapshotDir, "ownership_snapshots.jsonl"),
                "{\"EmittedUtc\":\"2026-04-26T18:46:35.0000000+00:00\",\"Instruments\":[{\"Instrument\":\"MNG\",\"BrokerPositionQty\":-2,\"BrokerWorkingOrderCount\":2,\"JournalOpenQty\":2,\"ActiveSlotCount\":1,\"OrphanSlotCount\":0,\"SnapshotSequence\":3,\"SnapshotUtc\":\"2026-04-26T18:46:35.0000000+00:00\"}]}" + Environment.NewLine);

            var openDoc = RunSummaryBuilder.Build(
                root,
                "summary-test",
                DateTimeOffset.Parse("2026-04-26T18:00:00+00:00"),
                ExecutionMode.SIM,
                new[] { "MNG" },
                new ExecutionSummarySnapshot());

            if (openDoc.status != "FAIL" || openDoc.status_reason != "OPEN_EXPOSURE_AT_SHUTDOWN")
                return (false, $"expected open shutdown exposure to be FAIL/OPEN_EXPOSURE_AT_SHUTDOWN, got {openDoc.status}/{openDoc.status_reason}");
            if (openDoc.recommended_action != "STOP" || openDoc.confidence != "HIGH")
                return (false, $"expected STOP/HIGH for open shutdown exposure, got {openDoc.recommended_action}/{openDoc.confidence}");
            if (!openDoc.flags.had_open_exposure_at_shutdown)
                return (false, "expected had_open_exposure_at_shutdown flag");
            if (openDoc.verdict_class != "UNSAFE_EXPOSURE")
                return (false, $"expected unsafe exposure verdict class, got {openDoc.verdict_class}");
            if (openDoc.errors != 1)
                return (false, $"expected one summary error for open shutdown exposure, got {openDoc.errors}");
            if (openDoc.key_counts.open_position_at_shutdown != 1 ||
                openDoc.key_counts.open_position_qty_at_shutdown != 2 ||
                openDoc.key_counts.broker_position_qty_at_shutdown != 2 ||
                openDoc.key_counts.broker_working_orders_at_shutdown != 2 ||
                openDoc.key_counts.ownership_active_slots_at_shutdown != 1 ||
                openDoc.key_counts.ownership_journal_open_qty_at_shutdown != 2)
            {
                return (false, "expected open shutdown exposure counts from journal and ownership snapshot");
            }

            var staleRoot = Path.Combine(root, "stale_ownership_summary");
            Directory.CreateDirectory(staleRoot);
            File.WriteAllText(Path.Combine(staleRoot, RunRootArtifacts.KeyEventsFileName), "");
            var staleSnapshotDir = RobotRunArtifactPaths.EventsOwnershipSnapshotsTradingDate(staleRoot, "2026-04-14");
            Directory.CreateDirectory(staleSnapshotDir);
            File.WriteAllText(Path.Combine(staleSnapshotDir, "ownership_snapshots.jsonl"),
                "{\"EmittedUtc\":\"2026-04-28T20:01:08.0000000+00:00\",\"Instruments\":[{\"Instrument\":\"M2K\",\"BrokerPositionQty\":0,\"BrokerWorkingOrderCount\":2,\"JournalOpenQty\":0,\"ActiveSlotCount\":0,\"OrphanSlotCount\":0,\"SnapshotSequence\":23,\"SnapshotUtc\":\"2026-04-28T20:01:08.0000000+00:00\"}]}" + Environment.NewLine);
            var staleLogDir = Path.Combine(staleRoot, "logs", "robot");
            Directory.CreateDirectory(staleLogDir);
            File.WriteAllText(Path.Combine(staleLogDir, "robot_M2K.jsonl"),
                "{\"ts_utc\":\"2026-04-28T20:01:37.0409618+00:00\",\"level\":\"INFO\",\"instrument\":\"M2K\",\"event\":\"ORDER_REGISTRY_METRICS\",\"data\":{\"owned_orders_active\":\"0\",\"adopted_orders_active\":\"0\",\"terminal_orders_recent\":\"2\"}}" + Environment.NewLine);

            var staleDoc = RunSummaryBuilder.Build(
                staleRoot,
                "stale-ownership-summary-test",
                DateTimeOffset.Parse("2026-04-12T22:01:00+00:00"),
                ExecutionMode.SIM,
                new[] { "M2K" },
                new ExecutionSummarySnapshot());

            if (staleDoc.status != "OK" ||
                staleDoc.status_reason != "NORMAL_RUN" ||
                staleDoc.verdict_class != "GREEN")
            {
                return (false, $"expected stale ownership suppression to keep summary green, got {staleDoc.status}/{staleDoc.status_reason}/{staleDoc.verdict_class}");
            }
            if (staleDoc.key_counts.broker_working_orders_at_shutdown != 0 ||
                staleDoc.key_counts.stale_ownership_working_orders_suppressed != 2 ||
                staleDoc.key_counts.diagnostic_contradictions != 0)
            {
                return (false, "expected stale ownership working orders to be suppressed without diagnostic contradiction");
            }
            if (staleDoc.flags.had_open_exposure_at_shutdown || staleDoc.flags.had_diagnostic_contradiction)
                return (false, "expected stale ownership suppression to avoid unsafe exposure and diagnostic contradiction");
            if (staleDoc.errors != 0)
                return (false, $"expected stale ownership suppression to keep hard error count zero, got {staleDoc.errors}");

            var staleTerminalRoot = Path.Combine(root, "stale_ownership_terminal_orders_summary");
            Directory.CreateDirectory(staleTerminalRoot);
            File.WriteAllText(Path.Combine(staleTerminalRoot, RunRootArtifacts.KeyEventsFileName), "");
            var staleTerminalSnapshotDir = RobotRunArtifactPaths.EventsOwnershipSnapshotsTradingDate(staleTerminalRoot, "2026-04-14");
            Directory.CreateDirectory(staleTerminalSnapshotDir);
            File.WriteAllText(Path.Combine(staleTerminalSnapshotDir, "ownership_snapshots.jsonl"),
                "{\"EmittedUtc\":\"2026-04-30T00:20:36.3247835+00:00\",\"Instruments\":[{\"Instrument\":\"M2K\",\"BrokerPositionQty\":0,\"BrokerWorkingOrderCount\":2,\"JournalOpenQty\":0,\"ActiveSlotCount\":0,\"OrphanSlotCount\":0,\"SnapshotSequence\":38,\"SnapshotUtc\":\"2026-04-30T00:20:36.3247835+00:00\"}]}" + Environment.NewLine);
            var staleTerminalLogDir = Path.Combine(staleTerminalRoot, "logs", "robot");
            Directory.CreateDirectory(staleTerminalLogDir);
            File.WriteAllLines(Path.Combine(staleTerminalLogDir, "robot_M2K.jsonl"), new[]
            {
                "{\"ts_utc\":\"2026-04-30T00:21:04.2337276+00:00\",\"level\":\"INFO\",\"instrument\":\"M2K\",\"event\":\"ORDER_REGISTRY_LIFECYCLE\",\"data\":{\"broker_order_id\":\"2a86feb7c7664646b86272b88f6254be\",\"intent_id\":\"5c046e4f194fbd2c\",\"previous_state\":\"WORKING\",\"new_state\":\"CANCELED\",\"ownership_status\":\"TERMINAL\"}}",
                "{\"ts_utc\":\"2026-04-30T00:21:04.2728667+00:00\",\"level\":\"INFO\",\"instrument\":\"M2K\",\"event\":\"ORDER_REGISTRY_LIFECYCLE\",\"data\":{\"broker_order_id\":\"a341669e74174e489dc87977b56415b6\",\"intent_id\":\"312eb00c98d0599b\",\"previous_state\":\"WORKING\",\"new_state\":\"CANCELED\",\"ownership_status\":\"TERMINAL\"}}"
            });

            var staleTerminalDoc = RunSummaryBuilder.Build(
                staleTerminalRoot,
                "stale-ownership-terminal-orders-summary-test",
                DateTimeOffset.Parse("2026-04-12T22:01:00+00:00"),
                ExecutionMode.SIM,
                new[] { "M2K" },
                new ExecutionSummarySnapshot());

            if (staleTerminalDoc.status != "OK" ||
                staleTerminalDoc.status_reason != "NORMAL_RUN" ||
                staleTerminalDoc.verdict_class != "GREEN" ||
                staleTerminalDoc.key_counts.broker_working_orders_at_shutdown != 0 ||
                staleTerminalDoc.key_counts.stale_ownership_working_orders_suppressed != 2 ||
                staleTerminalDoc.key_counts.diagnostic_contradictions != 0 ||
                staleTerminalDoc.flags.had_open_exposure_at_shutdown ||
                staleTerminalDoc.flags.had_diagnostic_contradiction)
            {
                return (false, "expected terminal order evidence to suppress stale ownership working orders without warning or unsafe exposure");
            }

            var staleCohortRoot = Path.Combine(root, "stale_ownership_final_cohort_summary");
            Directory.CreateDirectory(staleCohortRoot);
            File.WriteAllText(Path.Combine(staleCohortRoot, RunRootArtifacts.KeyEventsFileName), "");
            var staleCohortSnapshotDir = RobotRunArtifactPaths.EventsOwnershipSnapshotsTradingDate(staleCohortRoot, "2026-04-14");
            Directory.CreateDirectory(staleCohortSnapshotDir);
            File.WriteAllLines(Path.Combine(staleCohortSnapshotDir, "ownership_snapshots.jsonl"), new[]
            {
                "{\"EmittedUtc\":\"2026-04-30T02:27:37.0843102+00:00\",\"Instruments\":[{\"Instrument\":\"M2K\",\"BrokerPositionQty\":0,\"BrokerWorkingOrderCount\":2,\"JournalOpenQty\":0,\"ActiveSlotCount\":0,\"OrphanSlotCount\":0,\"SnapshotSequence\":38,\"SnapshotUtc\":\"2026-04-30T02:27:37.0843102+00:00\"}]}",
                "{\"EmittedUtc\":\"2026-04-30T03:11:38.3839950+00:00\",\"Instruments\":[{\"Instrument\":\"MYM\",\"BrokerPositionQty\":0,\"BrokerWorkingOrderCount\":0,\"JournalOpenQty\":0,\"ActiveSlotCount\":0,\"OrphanSlotCount\":0,\"SnapshotSequence\":133,\"SnapshotUtc\":\"2026-04-30T03:11:38.3839950+00:00\"}]}"
            });

            var staleCohortDoc = RunSummaryBuilder.Build(
                staleCohortRoot,
                "stale-ownership-final-cohort-summary-test",
                DateTimeOffset.Parse("2026-04-12T22:01:00+00:00"),
                ExecutionMode.SIM,
                new[] { "M2K", "MYM" },
                new ExecutionSummarySnapshot());

            if (staleCohortDoc.status != "OK" ||
                staleCohortDoc.status_reason != "NORMAL_RUN" ||
                staleCohortDoc.verdict_class != "GREEN" ||
                staleCohortDoc.key_counts.broker_working_orders_at_shutdown != 0 ||
                staleCohortDoc.key_counts.stale_ownership_working_orders_suppressed != 2 ||
                staleCohortDoc.key_counts.diagnostic_contradictions != 0 ||
                staleCohortDoc.flags.had_open_exposure_at_shutdown ||
                staleCohortDoc.flags.had_diagnostic_contradiction)
            {
                return (false, "expected stale ownership outside final snapshot cohort to be suppressed without warning or unsafe exposure");
            }

            var finalFlatRoot = Path.Combine(root, "final_flat_ownership_snapshot_summary");
            Directory.CreateDirectory(finalFlatRoot);
            File.WriteAllText(Path.Combine(finalFlatRoot, RunRootArtifacts.KeyEventsFileName), "");
            File.WriteAllText(Path.Combine(finalFlatRoot, RunRootArtifacts.RunShutdownSignalFileName),
                "{\"ts_utc\":\"2026-05-02T02:49:58.0000000+00:00\",\"reason\":\"engine_stop\",\"source\":\"test\"}");
            var finalFlatSnapshotDir = RobotRunArtifactPaths.EventsOwnershipSnapshotsTradingDate(finalFlatRoot, "2026-04-14");
            Directory.CreateDirectory(finalFlatSnapshotDir);
            File.WriteAllLines(Path.Combine(finalFlatSnapshotDir, "ownership_snapshots.jsonl"), new[]
            {
                "{\"EmittedUtc\":\"2026-05-02T02:10:19.5988193+00:00\",\"Instruments\":[{\"Instrument\":\"M2K\",\"BrokerPositionQty\":0,\"BrokerWorkingOrderCount\":2,\"JournalOpenQty\":0,\"ActiveSlotCount\":0,\"OrphanSlotCount\":0,\"SnapshotSequence\":37,\"SnapshotUtc\":\"2026-05-02T02:10:19.5988193+00:00\"}]}",
                "{\"EmittedUtc\":\"2026-05-02T02:49:53.0374628+00:00\",\"Trigger\":13,\"Instruments\":[{\"Instrument\":\"M2K\",\"BrokerPositionQty\":0,\"BrokerWorkingOrderCount\":0,\"JournalOpenQty\":0,\"ActiveSlotCount\":0,\"OrphanSlotCount\":0,\"SnapshotSequence\":38,\"SnapshotUtc\":\"2026-05-02T02:49:53.0374628+00:00\",\"SnapshotTrigger\":13}]}"
            });

            var finalFlatDoc = RunSummaryBuilder.Build(
                finalFlatRoot,
                "final-flat-ownership-snapshot-summary-test",
                DateTimeOffset.Parse("2026-04-12T22:01:00+00:00"),
                ExecutionMode.SIM,
                new[] { "M2K" },
                new ExecutionSummarySnapshot());

            if (finalFlatDoc.status != "OK" ||
                finalFlatDoc.status_reason != "NORMAL_RUN" ||
                finalFlatDoc.verdict_class != "GREEN" ||
                finalFlatDoc.key_counts.broker_working_orders_at_shutdown != 0 ||
                finalFlatDoc.key_counts.stale_ownership_working_orders_suppressed != 0 ||
                finalFlatDoc.flags.had_open_exposure_at_shutdown)
            {
                return (false, "expected final flat ownership snapshot to supersede older working-order snapshot without stale suppression");
            }

            var incompleteRoot = Path.Combine(root, "incomplete_stream_summary");
            Directory.CreateDirectory(incompleteRoot);
            File.WriteAllText(Path.Combine(incompleteRoot, RunRootArtifacts.KeyEventsFileName), "");
            var streamJournalDir = RobotRunArtifactPaths.StateStreamJournals(incompleteRoot);
            Directory.CreateDirectory(streamJournalDir);
            File.WriteAllText(Path.Combine(streamJournalDir, "2026-04-13_NG1.json"),
                "{\"TradingDate\":\"2026-04-13\",\"Stream\":\"NG1\",\"Committed\":false,\"LastState\":\"RANGE_BUILDING\",\"SlotInstanceKey\":\"NG1_07:30_2026-04-13\"}");
            File.WriteAllText(Path.Combine(streamJournalDir, "2026-04-13_NG2.json"),
                "{\"TradingDate\":\"2026-04-13\",\"Stream\":\"NG2\",\"Committed\":false,\"LastState\":\"PRE_HYDRATION\",\"SlotInstanceKey\":\"NG2_11:00_2026-04-13\"}");

            var incompleteDoc = RunSummaryBuilder.Build(
                incompleteRoot,
                "incomplete-summary-test",
                DateTimeOffset.Parse("2026-04-26T18:00:00+00:00"),
                ExecutionMode.SIM,
                new[] { "MNG" },
                new ExecutionSummarySnapshot());

            if (incompleteDoc.status != "FAIL" || incompleteDoc.status_reason != "STREAM_INCOMPLETE_AT_SHUTDOWN")
                return (false, $"expected incomplete stream journals to fail summary, got {incompleteDoc.status}/{incompleteDoc.status_reason}");
            if (incompleteDoc.key_counts.incomplete_streams_at_shutdown != 2 || !incompleteDoc.flags.had_incomplete_streams_at_shutdown)
                return (false, "expected incomplete stream shutdown count and flag");
            if (incompleteDoc.errors != 2)
                return (false, $"expected two summary errors for incomplete streams, got {incompleteDoc.errors}");

            var protectiveRoot = Path.Combine(root, "protective_failure_summary");
            Directory.CreateDirectory(protectiveRoot);
            File.WriteAllText(Path.Combine(protectiveRoot, RunRootArtifacts.KeyEventsFileName), "");
            var protectiveJournalDir = RobotRunArtifactPaths.StateExecutionJournals(protectiveRoot);
            Directory.CreateDirectory(protectiveJournalDir);
            File.WriteAllText(Path.Combine(protectiveJournalDir, "2026-04-13_GC2_protective-fail.json"), JsonUtil.Serialize(new ExecutionJournalEntry
            {
                IntentId = "protective-fail",
                TradingDate = "2026-04-13",
                Stream = "GC2",
                Instrument = "MGC",
                EntrySubmitted = true,
                EntryFilled = true,
                EntryFilledQuantityTotal = 2,
                ExitFilledQuantityTotal = 1,
                TradeCompleted = false,
                Rejected = false,
                ProtectiveRejectedAt = "2026-04-26T21:00:04.0000000+00:00",
                ProtectiveRejectionReason = "STOP_SUBMIT_FAILED: Object reference not set to an instance of an object.",
                ProtectiveRejectionOrderType = "STOP",
                Direction = "Long"
            }));

            var protectiveDoc = RunSummaryBuilder.Build(
                protectiveRoot,
                "protective-summary-test",
                DateTimeOffset.Parse("2026-04-26T18:00:00+00:00"),
                ExecutionMode.SIM,
                new[] { "MGC" },
                new ExecutionSummarySnapshot());

            if (protectiveDoc.status != "FAIL" || protectiveDoc.status_reason != "OPEN_EXPOSURE_AT_SHUTDOWN")
                return (false, $"expected active protective journal failure with open exposure to fail summary as open exposure, got {protectiveDoc.status}/{protectiveDoc.status_reason}");
            if (protectiveDoc.key_counts.protective_failed != 1 || !protectiveDoc.flags.had_protective_failure)
                return (false, "expected protective failure count and flag from active journal");
            if (protectiveDoc.errors != 2)
                return (false, $"expected two summary errors for protective failure plus open exposure, got {protectiveDoc.errors}");

            var recoveredProtectiveRoot = Path.Combine(root, "recovered_protective_failure_summary");
            Directory.CreateDirectory(recoveredProtectiveRoot);
            File.WriteAllText(Path.Combine(recoveredProtectiveRoot, RunRootArtifacts.KeyEventsFileName), "");
            var recoveredProtectiveJournalDir = RobotRunArtifactPaths.StateExecutionJournals(recoveredProtectiveRoot);
            Directory.CreateDirectory(recoveredProtectiveJournalDir);
            File.WriteAllText(Path.Combine(recoveredProtectiveJournalDir, "2026-04-13_CL2-protective-recovered.json"), JsonUtil.Serialize(new ExecutionJournalEntry
            {
                IntentId = "protective-recovered",
                TradingDate = "2026-04-13",
                Stream = "CL2",
                Instrument = "MCL",
                EntrySubmitted = true,
                EntryFilled = true,
                EntryFilledQuantityTotal = 2,
                ExitFilledQuantityTotal = 2,
                TradeCompleted = true,
                CompletionReason = "TARGET",
                Rejected = false,
                ProtectiveRejectedAt = "2026-04-26T21:00:04.0000000+00:00",
                ProtectiveRejectionReason = "STOP_SUBMIT_FAILED: Object reference not set to an instance of an object.",
                ProtectiveRejectionOrderType = "STOP",
                Direction = "Long"
            }));

            var recoveredProtectiveDoc = RunSummaryBuilder.Build(
                recoveredProtectiveRoot,
                "recovered-protective-summary-test",
                DateTimeOffset.Parse("2026-04-26T18:00:00+00:00"),
                ExecutionMode.SIM,
                new[] { "MCL" },
                new ExecutionSummarySnapshot());

            if (recoveredProtectiveDoc.status != "FAIL" || recoveredProtectiveDoc.status_reason != "PROTECTIVE_FAILED")
                return (false, $"expected recovered protective rejection to remain visible as PROTECTIVE_FAILED, got {recoveredProtectiveDoc.status}/{recoveredProtectiveDoc.status_reason}");
            if (recoveredProtectiveDoc.key_counts.protective_failed != 1 ||
                recoveredProtectiveDoc.flags.had_open_exposure_at_shutdown ||
                !recoveredProtectiveDoc.flags.had_protective_failure)
            {
                return (false, "expected recovered protective failure to count without open shutdown exposure");
            }

            var badRoot = Path.Combine(root, "bad_summary");
            Directory.CreateDirectory(badRoot);
            File.WriteAllLines(Path.Combine(badRoot, RunRootArtifacts.KeyEventsFileName), new[]
            {
                "{\"ts_utc\":\"2026-04-26T21:00:00.0000000+00:00\",\"event\":\"ENTRY_FILLED\",\"instrument\":\"M2K\",\"stream\":\"RTY2\",\"data\":{\"intent_id\":\"dup-intent\",\"partial\":true}}",
                "{\"ts_utc\":\"2026-04-26T21:00:01.0000000+00:00\",\"event\":\"ENTRY_FILLED\",\"instrument\":\"M2K\",\"stream\":\"RTY2\",\"data\":{\"intent_id\":\"dup-intent\",\"partial\":false}}"
            });

            var badJournalDir = RobotRunArtifactPaths.StateExecutionJournals(badRoot);
            Directory.CreateDirectory(badJournalDir);
            File.WriteAllText(Path.Combine(badJournalDir, "2026-04-13_RTY2_dup-intent.json"), JsonUtil.Serialize(new ExecutionJournalEntry
            {
                IntentId = "dup-intent",
                TradingDate = "2026-04-13",
                Stream = "RTY2",
                Instrument = "M2K",
                EntrySubmitted = true,
                EntryFilled = true,
                EntryFilledQuantityTotal = 2,
                ExitFilledQuantityTotal = 2,
                TradeCompleted = true,
                CompletionReason = "TARGET",
                Rejected = true,
                RejectionReason = "STOP_SUBMIT_FAILED: transient retry recovered",
                Direction = "Long"
            }));

            var logDir = Path.Combine(badRoot, "logs", "robot");
            Directory.CreateDirectory(logDir);
            File.WriteAllText(Path.Combine(logDir, "robot_ENGINE.jsonl"),
                "{\"ts_utc\":\"2026-04-26T21:00:02.0000000+00:00\",\"level\":\"ERROR\",\"event\":\"OWNERSHIP_FLATTEN_EXIT_CLOSE_FAILED\",\"message\":\"OWNERSHIP_FLATTEN_EXIT_CLOSE_FAILED\",\"data\":{}}" + Environment.NewLine);
            File.WriteAllText(Path.Combine(logDir, "robot_ENGINE_20260427_040903.jsonl"),
                "{\"ts_utc\":\"2026-04-26T21:00:03.0000000+00:00\",\"level\":\"CRITICAL\",\"event\":\"NT_THREAD_VIOLATION\",\"message\":\"NT_THREAD_VIOLATION\",\"data\":{\"method\":\"CancelProtectiveOrdersForRecreate\"}}" + Environment.NewLine);

            var badDoc = RunSummaryBuilder.Build(
                badRoot,
                "bad-summary-test",
                DateTimeOffset.Parse("2026-04-26T18:00:00+00:00"),
                ExecutionMode.SIM,
                new[] { "M2K" },
                new ExecutionSummarySnapshot());

            if (badDoc.status != "FAIL" || badDoc.status_reason != "ROBOT_LOG_ERROR")
                return (false, $"expected internal robot log error to fail summary, got {badDoc.status}/{badDoc.status_reason}");
            if (badDoc.trades != 1)
                return (false, $"expected completed trade journals to determine trade count, got {badDoc.trades}");
            if (badDoc.key_counts.unique_entry_filled_intents != 1 ||
                badDoc.key_counts.completed_filled_trade_journals != 1 ||
                badDoc.key_counts.completed_rejected_journals != 1 ||
                badDoc.key_counts.robot_log_errors != 1 ||
                badDoc.key_counts.robot_log_critical != 1)
            {
                return (false, "expected duplicate entry fill and stale completed rejection summary counts; got " +
                    $"unique_entry_filled_intents={badDoc.key_counts.unique_entry_filled_intents}, " +
                    $"completed_filled_trade_journals={badDoc.key_counts.completed_filled_trade_journals}, " +
                    $"completed_rejected_journals={badDoc.key_counts.completed_rejected_journals}, " +
                    $"robot_log_errors={badDoc.key_counts.robot_log_errors}, " +
                    $"robot_log_critical={badDoc.key_counts.robot_log_critical}");
            }
            if (badDoc.errors != 3)
                return (false, $"expected summary errors to include log error + log critical + completed rejected journal, got {badDoc.errors}");
            if (!badDoc.flags.had_completed_rejected_journal || !badDoc.flags.had_robot_log_error)
                return (false, "expected completed rejected journal and robot log error flags");

            var dailyOnlyRoot = Path.Combine(root, "daily_only_critical_summary");
            Directory.CreateDirectory(dailyOnlyRoot);
            File.WriteAllText(Path.Combine(dailyOnlyRoot, RunRootArtifacts.KeyEventsFileName), "");
            var dailyOnlyLogDir = Path.Combine(dailyOnlyRoot, "logs", "robot");
            Directory.CreateDirectory(dailyOnlyLogDir);
            File.WriteAllText(Path.Combine(dailyOnlyLogDir, "daily_20260501.md"),
                "# Robot Daily Log Summary (20260501)" + Environment.NewLine +
                "- errors: 1" + Environment.NewLine +
                "- latest_error: 2026-05-01T22:59:44.6307820+00:00 | MNG | PROTECTIVE_MISSING_STOP | PROTECTIVE_MISSING_STOP" + Environment.NewLine);

            var dailyOnlyDoc = RunSummaryBuilder.Build(
                dailyOnlyRoot,
                "daily-only-critical-summary-test",
                DateTimeOffset.Parse("2026-05-01T22:00:00+00:00"),
                ExecutionMode.SIM,
                new[] { "MNG" },
                new ExecutionSummarySnapshot());

            if (dailyOnlyDoc.status != "FAIL" ||
                dailyOnlyDoc.status_reason != "ROBOT_LOG_CRITICAL" ||
                dailyOnlyDoc.key_counts.robot_log_critical != 1 ||
                dailyOnlyDoc.key_counts.robot_log_hard_critical != 1)
            {
                return (false, "expected daily summary critical fallback to fail summary; got " +
                    $"{dailyOnlyDoc.status}/{dailyOnlyDoc.status_reason}, " +
                    $"robot_log_critical={dailyOnlyDoc.key_counts.robot_log_critical}, " +
                    $"robot_log_hard_critical={dailyOnlyDoc.key_counts.robot_log_hard_critical}");
            }

            var normalTerminatedRoot = Path.Combine(root, "normal_strategy_terminated_summary");
            Directory.CreateDirectory(normalTerminatedRoot);
            File.WriteAllText(Path.Combine(normalTerminatedRoot, RunRootArtifacts.KeyEventsFileName), "");
            var normalTerminatedLogDir = Path.Combine(normalTerminatedRoot, "logs", "robot");
            Directory.CreateDirectory(normalTerminatedLogDir);
            File.WriteAllText(Path.Combine(normalTerminatedLogDir, "robot_ENGINE.jsonl"),
                "{\"ts_utc\":\"2026-04-29T11:48:00.0000000+00:00\",\"level\":\"INFO\",\"event\":\"STRATEGY_TERMINATED_BY_NINJATRADER\",\"message\":\"STRATEGY_TERMINATED_BY_NINJATRADER\",\"data\":{\"termination_classification\":\"STATE_TERMINATED\",\"platform_disable_signal\":false}}" + Environment.NewLine);

            var normalTerminatedDoc = RunSummaryBuilder.Build(
                normalTerminatedRoot,
                "normal-strategy-terminated-summary-test",
                DateTimeOffset.Parse("2026-04-29T11:40:00+00:00"),
                ExecutionMode.SIM,
                new[] { "MYM" },
                new ExecutionSummarySnapshot());

            if (normalTerminatedDoc.status != "OK" ||
                normalTerminatedDoc.status_reason != "NORMAL_RUN" ||
                normalTerminatedDoc.verdict_class != "GREEN")
            {
                return (false, $"expected normal State.Terminated lifecycle to stay green, got {normalTerminatedDoc.status}/{normalTerminatedDoc.status_reason}/{normalTerminatedDoc.verdict_class}");
            }
            if (normalTerminatedDoc.key_counts.strategy_disabled_events != 0 ||
                normalTerminatedDoc.flags.had_platform_disable_signal ||
                normalTerminatedDoc.flags.had_crash_or_freeze_signal)
            {
                return (false, "expected normal State.Terminated lifecycle not to count as platform disable/crash signal");
            }

            var platformRoot = Path.Combine(root, "platform_disable_summary");
            Directory.CreateDirectory(platformRoot);
            File.WriteAllText(Path.Combine(platformRoot, RunRootArtifacts.KeyEventsFileName), "");
            var platformLogDir = Path.Combine(platformRoot, "logs", "robot");
            Directory.CreateDirectory(platformLogDir);
            File.WriteAllLines(Path.Combine(platformLogDir, "robot_ENGINE.jsonl"), new[]
            {
                "{\"ts_utc\":\"2026-04-29T11:47:00.0000000+00:00\",\"level\":\"WARN\",\"event\":\"ENGINE_PLAYBACK_STALL_QUIESCENCE_DEFERRED_IEA_WORK\",\"message\":\"ENGINE_PLAYBACK_STALL_QUIESCENCE_DEFERRED_IEA_WORK\",\"data\":{\"iea_pending_work\":7,\"iea_pending_instruments\":\"MYM:7\"}}",
                "{\"ts_utc\":\"2026-04-29T11:47:31.0000000+00:00\",\"level\":\"WARN\",\"event\":\"ENGINE_PLAYBACK_STALL_QUIESCENCE_STALE_IEA_WORK_RELEASED\",\"message\":\"ENGINE_PLAYBACK_STALL_QUIESCENCE_STALE_IEA_WORK_RELEASED\",\"data\":{\"iea_pending_work\":7,\"iea_pending_instruments\":\"MYM:7\"}}",
                "{\"ts_utc\":\"2026-04-29T11:48:00.0000000+00:00\",\"level\":\"WARN\",\"event\":\"STRATEGY_DISABLED_BY_NINJATRADER\",\"message\":\"STRATEGY_DISABLED_BY_NINJATRADER\",\"data\":{\"reason\":\"Lost price connection\"}}"
            });

            var platformDoc = RunSummaryBuilder.Build(
                platformRoot,
                "platform-disable-summary-test",
                DateTimeOffset.Parse("2026-04-29T11:40:00+00:00"),
                ExecutionMode.SIM,
                new[] { "MYM" },
                new ExecutionSummarySnapshot());

            if (platformDoc.status != "FAIL" ||
                platformDoc.status_reason != "CRASH_OR_FREEZE_SIGNAL" ||
                platformDoc.verdict_class != "CRASH_OR_FREEZE")
            {
                return (false, $"expected platform disable/stall to be FAIL/CRASH_OR_FREEZE_SIGNAL/CRASH_OR_FREEZE, got {platformDoc.status}/{platformDoc.status_reason}/{platformDoc.verdict_class}");
            }
            if (platformDoc.key_counts.playback_stall_warnings != 2 ||
                platformDoc.key_counts.strategy_disabled_events != 1 ||
                !platformDoc.flags.had_crash_or_freeze_signal ||
                !platformDoc.flags.had_platform_disable_signal)
            {
                return (false, "expected platform disable/stall counters and flags");
            }
            if (platformDoc.recommended_action != "STOP" || platformDoc.confidence != "HIGH")
                return (false, $"expected STOP/HIGH for platform disable/stall, got {platformDoc.recommended_action}/{platformDoc.confidence}");

            var shutdownSignalRoot = Path.Combine(root, "playback_shutdown_signal_summary");
            Directory.CreateDirectory(shutdownSignalRoot);
            File.WriteAllText(Path.Combine(shutdownSignalRoot, RunRootArtifacts.KeyEventsFileName), "");
            File.WriteAllText(Path.Combine(shutdownSignalRoot, RunRootArtifacts.RunShutdownSignalFileName),
                "{\"ts_utc\":\"2026-04-29T19:15:31.3617783+00:00\",\"reason\":\"playback_stall_live_exposure_timeout\",\"source\":\"playback_stall_quiesce\"}");

            var shutdownSignalDoc = RunSummaryBuilder.Build(
                shutdownSignalRoot,
                "playback-shutdown-signal-summary-test",
                DateTimeOffset.Parse("2026-04-29T19:10:00+00:00"),
                ExecutionMode.SIM,
                new[] { "MNG" },
                new ExecutionSummarySnapshot());

            if (shutdownSignalDoc.status != "FAIL" ||
                shutdownSignalDoc.status_reason != "CRASH_OR_FREEZE_SIGNAL" ||
                shutdownSignalDoc.verdict_class != "CRASH_OR_FREEZE")
            {
                return (false, $"expected durable playback shutdown signal to be FAIL/CRASH_OR_FREEZE_SIGNAL/CRASH_OR_FREEZE, got {shutdownSignalDoc.status}/{shutdownSignalDoc.status_reason}/{shutdownSignalDoc.verdict_class}");
            }
            if (shutdownSignalDoc.key_counts.playback_stall_warnings != 1 ||
                !shutdownSignalDoc.flags.had_crash_or_freeze_signal)
            {
                return (false, "expected durable playback shutdown signal to set playback stall count and crash/freeze flag");
            }

            var platformExposureRoot = Path.Combine(root, "platform_disable_open_exposure_summary");
            Directory.CreateDirectory(platformExposureRoot);
            File.WriteAllText(Path.Combine(platformExposureRoot, RunRootArtifacts.KeyEventsFileName), "");
            var platformExposureLogDir = Path.Combine(platformExposureRoot, "logs", "robot");
            Directory.CreateDirectory(platformExposureLogDir);
            File.WriteAllText(Path.Combine(platformExposureLogDir, "robot_ENGINE.jsonl"),
                "{\"ts_utc\":\"2026-04-29T11:48:00.0000000+00:00\",\"level\":\"WARN\",\"event\":\"STRATEGY_DISABLED_BY_NINJATRADER\",\"message\":\"STRATEGY_DISABLED_BY_NINJATRADER\",\"data\":{\"reason\":\"Lost price connection\"}}" + Environment.NewLine);
            var platformExposureJournalDir = RobotRunArtifactPaths.StateExecutionJournals(platformExposureRoot);
            Directory.CreateDirectory(platformExposureJournalDir);
            File.WriteAllText(Path.Combine(platformExposureJournalDir, "2026-04-13_YM2_open-after-disable.json"), JsonUtil.Serialize(new ExecutionJournalEntry
            {
                IntentId = "open-after-disable",
                TradingDate = "2026-04-13",
                Stream = "YM2",
                Instrument = "MYM",
                EntrySubmitted = true,
                EntryFilled = true,
                EntryFilledQuantityTotal = 2,
                ExitFilledQuantityTotal = 0,
                TradeCompleted = false,
                Direction = "Long"
            }));

            var platformExposureDoc = RunSummaryBuilder.Build(
                platformExposureRoot,
                "platform-disable-open-exposure-summary-test",
                DateTimeOffset.Parse("2026-04-29T11:40:00+00:00"),
                ExecutionMode.SIM,
                new[] { "MYM" },
                new ExecutionSummarySnapshot());

            if (platformExposureDoc.status != "FAIL" ||
                platformExposureDoc.status_reason != "OPEN_EXPOSURE_AT_SHUTDOWN" ||
                platformExposureDoc.verdict_class != "UNSAFE_EXPOSURE")
            {
                return (false, $"expected open exposure to outrank platform disable, got {platformExposureDoc.status}/{platformExposureDoc.status_reason}/{platformExposureDoc.verdict_class}");
            }
            if (!platformExposureDoc.flags.had_open_exposure_at_shutdown ||
                !platformExposureDoc.flags.had_crash_or_freeze_signal ||
                !platformExposureDoc.flags.had_platform_disable_signal)
            {
                return (false, "expected open exposure and platform disable flags together");
            }

            return (true, null);
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, true);
            }
            catch
            {
                // best effort
            }
        }
    }
}
