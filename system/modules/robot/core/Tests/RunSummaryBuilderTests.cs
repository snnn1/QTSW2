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

            if (staleDoc.status != "WARN" ||
                staleDoc.status_reason != "DIAGNOSTIC_CONTRADICTION" ||
                staleDoc.verdict_class != "DIAGNOSTIC_CONTRADICTION")
            {
                return (false, $"expected stale ownership to be WARN/DIAGNOSTIC_CONTRADICTION, got {staleDoc.status}/{staleDoc.status_reason}/{staleDoc.verdict_class}");
            }
            if (staleDoc.key_counts.broker_working_orders_at_shutdown != 0 ||
                staleDoc.key_counts.stale_ownership_working_orders_suppressed != 2 ||
                staleDoc.key_counts.diagnostic_contradictions == 0)
            {
                return (false, "expected stale ownership working orders to be suppressed and counted as diagnostic contradiction");
            }
            if (staleDoc.flags.had_open_exposure_at_shutdown || !staleDoc.flags.had_diagnostic_contradiction)
                return (false, "expected stale ownership to avoid unsafe exposure while flagging diagnostic contradiction");
            if (staleDoc.errors != 0)
                return (false, $"expected stale diagnostic contradiction to keep hard error count zero, got {staleDoc.errors}");

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

            if (protectiveDoc.status != "FAIL" || protectiveDoc.status_reason != "PROTECTIVE_FAILED")
                return (false, $"expected active protective journal failure to fail summary, got {protectiveDoc.status}/{protectiveDoc.status_reason}");
            if (protectiveDoc.key_counts.protective_failed != 1 || !protectiveDoc.flags.had_protective_failure)
                return (false, "expected protective failure count and flag from active journal");
            if (protectiveDoc.errors != 2)
                return (false, $"expected two summary errors for protective failure plus open exposure, got {protectiveDoc.errors}");

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
