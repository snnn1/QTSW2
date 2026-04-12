// Identity validation via execution replay + real adapter guard paths (no broker when SIM not verified).
// See docs/robot/IDENTITY_REPLAY_SCENARIOS.md.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using QTSW2.Robot.Contracts;

namespace QTSW2.Robot.Core.Execution;

/// <summary>Runs identity scenarios; returns aggregate pass/fail.</summary>
public static class IdentityReplayScenarioRunner
{
    private static readonly string[] ForbiddenForCleanReplay =
    {
        "INTENT_HYDRATION_ID_MISMATCH",
        "INTENT_ID_MISMATCH_REJECTED",
        "INTENT_LOOKUP_MISS_AT_SUBMIT_REJECTED",
        "SESSION_IDENTITY_MISMATCH_BLOCKED",
        "SESSION_IDENTITY_MISMATCH",
        "SUBMIT_MARKET_REENTRY_INTENT_ID_CANONICAL_OVERRIDE"
    };

    /// <summary>Run all identity scenarios (replay + journal + adapter guards + AGG tags).</summary>
    public static (bool AllPassed, string? Error) RunAllIdentityValidationScenarios(Action<string>? log = null)
    {
        var failures = new List<string>();

        if (!RunScenario1_ReplayNormalLifecycle(log, out var e1)) failures.Add(e1);
        if (!RunScenario2_JournalPersistence(log, out var e2)) failures.Add(e2);
        if (!RunScenario3_ReentryCanonical(log, out var e3)) failures.Add(e3);
        if (!RunScenario4_FlattenSameIntentId(log, out var e4)) failures.Add(e4);
        if (!RunScenario5_SubmitNotInMap(log, out var e5)) failures.Add(e5);
        if (!RunScenario6_MapKeyMismatch(log, out var e6)) failures.Add(e6);
        if (!RunScenario7_AggregatedTag(log, out var e7)) failures.Add(e7);
        if (!RunScenario8_FullHydrationReconstruction(log, out var e8)) failures.Add(e8);

        return (failures.Count == 0, failures.Count == 0 ? null : string.Join("; ", failures));
    }

    private static bool RunScenario1_ReplayNormalLifecycle(Action<string>? log, out string error)
    {
        error = "";
        var tempDir = Path.Combine(Path.GetTempPath(), "QTSW2_IdentityS1_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        try
        {
            Directory.CreateDirectory(tempDir);
            var tradingDate = IdentityReplayScenarioDefinitions.DeterministicTradingDate;
            var scenario = IdentityReplayScenarioDefinitions.GetScenario1_NormalLifecycleIdentity();
            var robotLog = new RobotLogger(tempDir, Path.Combine(tempDir, "logs"), "IDENTITY");
            var writer = new ExecutionEventWriter(tempDir, () => tradingDate, robotLog);

            var result = ExecutionScenarioRunner.RunScenario(scenario, writer, tempDir, tradingDate);
            if (!result.Pass)
            {
                error = $"{IdentityReplayScenarioNames.S1_NORMAL_LIFECYCLE}: replay state {result.Error}";
                return false;
            }

            var expectedId = IdentityReplayScenarioDefinitions.DeterministicCanonicalIntentId;
            if (!AllExecutionEventsUseIntentId(tempDir, tradingDate, expectedId, out var idErr))
            {
                error = $"{IdentityReplayScenarioNames.S1_NORMAL_LIFECYCLE}: {idErr}";
                return false;
            }

            if (RobotLogsContainForbidden(tempDir, ForbiddenForCleanReplay, out var bad))
            {
                error = $"{IdentityReplayScenarioNames.S1_NORMAL_LIFECYCLE}: unexpected log token {bad}";
                return false;
            }

            log?.Invoke($"PASS {IdentityReplayScenarioNames.S1_NORMAL_LIFECYCLE}");
            return true;
        }
        catch (Exception ex)
        {
            error = $"{IdentityReplayScenarioNames.S1_NORMAL_LIFECYCLE}: {ex.Message}";
            return false;
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best effort */ }
        }
    }

    private static bool AllExecutionEventsUseIntentId(string projectRoot, string tradingDate, string expectedId, out string err)
    {
        err = "";
        foreach (var evt in ExecutionReplayReader.ReadAllEventsForTradingDate(projectRoot, tradingDate))
        {
            if (string.IsNullOrEmpty(evt.IntentId)) continue;
            if (!string.Equals(evt.IntentId, expectedId, StringComparison.Ordinal))
            {
                err = $"event {evt.EventType} intent_id {evt.IntentId} != {expectedId}";
                return false;
            }
        }
        return true;
    }

    private static bool RobotLogsContainForbidden(string projectRoot, IReadOnlyList<string> forbidden, out string? found)
    {
        found = null;
        foreach (var path in Directory.GetFiles(projectRoot, "*.jsonl", SearchOption.AllDirectories))
        {
            string text;
            try { text = File.ReadAllText(path); }
            catch { continue; }
            foreach (var tok in forbidden)
            {
                if (text.IndexOf(tok, StringComparison.Ordinal) >= 0)
                {
                    found = tok;
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>Journal round-trip: same intent id on disk and via <see cref="ExecutionJournal.GetEntry"/>.</summary>
    private static bool RunScenario2_JournalPersistence(Action<string>? log, out string error)
    {
        error = "";
        var tempDir = Path.Combine(Path.GetTempPath(), "QTSW2_IdentityS2_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        try
        {
            Directory.CreateDirectory(tempDir);
            var robotLog = new RobotLogger(tempDir, Path.Combine(tempDir, "logs"), "IDENTITY");
            var journal = new ExecutionJournal(tempDir, robotLog);
            var intent = IdentityReplayScenarioDefinitions.BuildDeterministicIntent("IDENTITY_S2");
            var id = intent.ComputeIntentId();
            var td = IdentityReplayScenarioDefinitions.DeterministicTradingDate;
            const string stream = "MES_S1";

            journal.RecordSubmission(id, td, stream, "MES", "ENTRY", "broker-1", DateTimeOffset.UtcNow,
                expectedEntryPrice: 4500m, entryPrice: 4500m, stopPrice: 4400m, targetPrice: 4600m, direction: "Long");

            var entry = journal.GetEntry(id, td, stream);
            if (entry == null)
            {
                error = $"{IdentityReplayScenarioNames.S2_JOURNAL_PERSISTENCE}: GetEntry returned null";
                return false;
            }
            if (!string.Equals(entry.IntentId, id, StringComparison.Ordinal))
            {
                error = $"{IdentityReplayScenarioNames.S2_JOURNAL_PERSISTENCE}: entry.IntentId {entry.IntentId} != {id}";
                return false;
            }

            // Second journal instance (restart): same identity
            var journal2 = new ExecutionJournal(tempDir, robotLog);
            var entry2 = journal2.GetEntry(id, td, stream);
            if (entry2 == null || !string.Equals(entry2.IntentId, id, StringComparison.Ordinal))
            {
                error = $"{IdentityReplayScenarioNames.S2_JOURNAL_PERSISTENCE}: restart read mismatch";
                return false;
            }

            if (RobotLogsContainForbidden(tempDir, new[] { "INTENT_HYDRATION_ID_MISMATCH" }, out var bad))
            {
                error = $"{IdentityReplayScenarioNames.S2_JOURNAL_PERSISTENCE}: {bad}";
                return false;
            }

            log?.Invoke($"PASS {IdentityReplayScenarioNames.S2_JOURNAL_PERSISTENCE}");
            return true;
        }
        catch (Exception ex)
        {
            error = $"{IdentityReplayScenarioNames.S2_JOURNAL_PERSISTENCE}: {ex.Message}";
            return false;
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    private static bool RunScenario3_ReentryCanonical(Action<string>? log, out string error)
    {
        error = "";
        try
        {
            var utc = DateTimeOffset.UtcNow;
            var reentryIntent = new Intent(
                IdentityReplayScenarioDefinitions.DeterministicTradingDate,
                "MES_S1",
                "MES",
                "MES",
                "S1",
                "09:00",
                "Long",
                null,
                null,
                null,
                null,
                utc,
                "SUBMIT_MARKET_REENTRY");
            var canonicalId = reentryIntent.ComputeIntentId();
            var cmd = new SubmitMarketReentryCommand
            {
                CommandId = Guid.NewGuid().ToString(),
                TimestampUtc = utc,
                Stream = "MES_S1",
                Session = "S1",
                SlotTimeChicago = "09:00",
                TradingDate = IdentityReplayScenarioDefinitions.DeterministicTradingDate,
                ExecutionInstrument = "MES",
                ReentryIntentId = canonicalId,
                OriginalIntentId = IdentityReplayScenarioDefinitions.DeterministicCanonicalIntentId,
                Direction = "Long",
                Quantity = 1,
                Reason = "MARKET_REENTRY"
            };
            if (!string.Equals(cmd.ReentryIntentId, canonicalId, StringComparison.Ordinal))
            {
                error = $"{IdentityReplayScenarioNames.S3_REENTRY_CANONICAL}: command id drift";
                return false;
            }
            // Recompute as ExecuteSubmitMarketReentry would — must match
            var re2 = new Intent(
                cmd.TradingDate ?? "",
                cmd.Stream ?? "",
                cmd.ExecutionInstrument ?? "MES",
                (cmd.ExecutionInstrument ?? "MES").Trim().ToUpperInvariant(),
                cmd.Session ?? "",
                cmd.SlotTimeChicago ?? "",
                string.IsNullOrEmpty(cmd.Direction) ? "Long" : cmd.Direction!,
                null, null, null, null,
                utc,
                "SUBMIT_MARKET_REENTRY");
            if (!string.Equals(re2.ComputeIntentId(), canonicalId, StringComparison.Ordinal))
            {
                error = $"{IdentityReplayScenarioNames.S3_REENTRY_CANONICAL}: recompute mismatch";
                return false;
            }

            log?.Invoke($"PASS {IdentityReplayScenarioNames.S3_REENTRY_CANONICAL}");
            return true;
        }
        catch (Exception ex)
        {
            error = $"{IdentityReplayScenarioNames.S3_REENTRY_CANONICAL}: {ex.Message}";
            return false;
        }
    }

    private static bool RunScenario4_FlattenSameIntentId(Action<string>? log, out string error)
    {
        error = "";
        var tempDir = Path.Combine(Path.GetTempPath(), "QTSW2_IdentityS4_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        try
        {
            Directory.CreateDirectory(tempDir);
            var robotLog = new RobotLogger(tempDir, Path.Combine(tempDir, "logs"), "IDENTITY");
            var journal = new ExecutionJournal(tempDir, robotLog);
            var adapter = new NinjaTraderSimAdapter(tempDir, tempDir, robotLog, journal);
            adapter.SetEngineCallbacks(null, null, null, null, null, null, null, null, null,
                () => IdentityReplayScenarioDefinitions.DeterministicTradingDate);

            var intent = IdentityReplayScenarioDefinitions.BuildDeterministicIntent("IDENTITY_S4");
            var id = intent.ComputeIntentId();
            adapter.RegisterIntent(intent);
            var utc = DateTimeOffset.UtcNow;
            adapter.FlattenIntent(id, "MES", utc);

            if (!RobotJsonlContainsIntentIdForEvent(tempDir, "FLATTEN_INTENT_ATTEMPT", id, out var err2))
            {
                error = $"{IdentityReplayScenarioNames.S4_FLATTEN_SAME_ID}: {err2}";
                return false;
            }

            log?.Invoke($"PASS {IdentityReplayScenarioNames.S4_FLATTEN_SAME_ID}");
            return true;
        }
        catch (Exception ex)
        {
            error = $"{IdentityReplayScenarioNames.S4_FLATTEN_SAME_ID}: {ex.Message}";
            return false;
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    private static bool RobotJsonlContainsIntentIdForEvent(string projectRoot, string eventType, string intentId, out string err)
    {
        err = "";
        foreach (var path in Directory.GetFiles(projectRoot, "*.jsonl", SearchOption.AllDirectories))
        {
            string text;
            try { text = File.ReadAllText(path); }
            catch { continue; }
            if (text.IndexOf(eventType, StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (text.IndexOf(intentId, StringComparison.Ordinal) >= 0)
                return true;
        }
        err = $"missing {eventType} with intent_id {intentId}";
        return false;
    }

    private static bool RunScenario5_SubmitNotInMap(Action<string>? log, out string error)
    {
        error = "";
        var tempDir = Path.Combine(Path.GetTempPath(), "QTSW2_IdentityS5_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        try
        {
            Directory.CreateDirectory(tempDir);
            var robotLog = new RobotLogger(tempDir, Path.Combine(tempDir, "logs"), "IDENTITY");
            var journal = new ExecutionJournal(tempDir, robotLog);
            var adapter = new NinjaTraderSimAdapter(tempDir, tempDir, robotLog, journal);
            adapter.SetEngineCallbacks(null, null, null, null, null, null, null, null, null,
                () => IdentityReplayScenarioDefinitions.DeterministicTradingDate);

            var r = adapter.SubmitEntryOrder("definitely-not-registered-intent-id", "MES", "Long", null, 1, "MARKET", null,
                DateTimeOffset.UtcNow);
            if (r.Success || !string.Equals(r.ErrorMessage, "INTENT_NOT_IN_MAP", StringComparison.Ordinal))
            {
                error = $"{IdentityReplayScenarioNames.S5_SUBMIT_NOT_IN_MAP}: expected INTENT_NOT_IN_MAP, got {r.ErrorMessage}";
                return false;
            }
            if (!adapter.IsSessionIdentityLatched)
            {
                log?.Invoke($"PASS {IdentityReplayScenarioNames.S5_SUBMIT_NOT_IN_MAP}");
                return true;
            }
            error = $"{IdentityReplayScenarioNames.S5_SUBMIT_NOT_IN_MAP}: session latched unexpectedly";
            return false;
        }
        catch (Exception ex)
        {
            error = $"{IdentityReplayScenarioNames.S5_SUBMIT_NOT_IN_MAP}: {ex.Message}";
            return false;
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    private static bool RunScenario6_MapKeyMismatch(Action<string>? log, out string error)
    {
        error = "";
        var tempDir = Path.Combine(Path.GetTempPath(), "QTSW2_IdentityS6_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        try
        {
            Directory.CreateDirectory(tempDir);
            var robotLog = new RobotLogger(tempDir, Path.Combine(tempDir, "logs"), "IDENTITY");
            var journal = new ExecutionJournal(tempDir, robotLog);
            var adapter = new NinjaTraderSimAdapter(tempDir, tempDir, robotLog, journal);
            adapter.SetEngineCallbacks(null, null, null, null, null, null, null, null, null,
                () => IdentityReplayScenarioDefinitions.DeterministicTradingDate);

            var intent = IdentityReplayScenarioDefinitions.BuildDeterministicIntent("IDENTITY_S6");
            var canonicalId = intent.ComputeIntentId();
            adapter.RegisterIntent(intent);

            var mapField = typeof(NinjaTraderSimAdapter).GetField("_intentMap", BindingFlags.Instance | BindingFlags.NonPublic);
            if (mapField?.GetValue(adapter) is not ConcurrentDictionary<string, Intent> map)
            {
                error = $"{IdentityReplayScenarioNames.S6_KEY_MISMATCH}: reflection failed";
                return false;
            }
            map.TryRemove(canonicalId, out _);
            map["wrong-registration-key"] = intent;

            var r = adapter.SubmitEntryOrder("wrong-registration-key", "MES", "Long", null, 1, "MARKET", null,
                DateTimeOffset.UtcNow);
            if (r.Success || !string.Equals(r.ErrorMessage, "INTENT_ID_MISMATCH", StringComparison.Ordinal))
            {
                error = $"{IdentityReplayScenarioNames.S6_KEY_MISMATCH}: expected INTENT_ID_MISMATCH, got {r.ErrorMessage}";
                return false;
            }
            if (adapter.IsSessionIdentityLatched)
            {
                error = $"{IdentityReplayScenarioNames.S6_KEY_MISMATCH}: latch should not arm for mismatch guard";
                return false;
            }

            log?.Invoke($"PASS {IdentityReplayScenarioNames.S6_KEY_MISMATCH}");
            return true;
        }
        catch (Exception ex)
        {
            error = $"{IdentityReplayScenarioNames.S6_KEY_MISMATCH}: {ex.Message}";
            return false;
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    private static bool RunScenario7_AggregatedTag(Action<string>? log, out string error)
    {
        error = "";
        try
        {
            var a = IdentityReplayScenarioDefinitions.DeterministicCanonicalIntentId;
            var b = ExecutionJournal.ComputeIntentId(
                IdentityReplayScenarioDefinitions.DeterministicTradingDate,
                "MES_S2",
                "MES",
                "S1",
                "10:00",
                "Short",
                1m, 2m, 3m, 4m);
            var tag = RobotOrderIds.EncodeAggregatedTag(new[] { a, b });
            if (!RobotOrderIds.IsAggregatedTag(tag))
            {
                error = $"{IdentityReplayScenarioNames.S7_AGG_TAG}: not aggregated";
                return false;
            }
            var ids = RobotOrderIds.DecodeAggregatedIntentIds(tag);
            if (ids == null || ids.Count != 2)
            {
                error = $"{IdentityReplayScenarioNames.S7_AGG_TAG}: decode count";
                return false;
            }
            var primary = RobotOrderIds.DecodeIntentId(tag);
            if (primary != ids[0])
            {
                error = $"{IdentityReplayScenarioNames.S7_AGG_TAG}: primary != first";
                return false;
            }

            log?.Invoke($"PASS {IdentityReplayScenarioNames.S7_AGG_TAG}");
            return true;
        }
        catch (Exception ex)
        {
            error = $"{IdentityReplayScenarioNames.S7_AGG_TAG}: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Open journal on disk → new adapter (restart) → real <c>HydrateIntentsFromOpenJournals</c> via reflection
    /// (same implementation as <c>SetNTContext</c> after SIM verify). Asserts id parity and submit path past intent guards.
    /// </summary>
    private static bool RunScenario8_FullHydrationReconstruction(Action<string>? log, out string error)
    {
        error = "";
        var tempDir = Path.Combine(Path.GetTempPath(), "QTSW2_IdentityS8_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        try
        {
            Directory.CreateDirectory(tempDir);
            var robotLog = new QTSW2.Robot.Core.RobotLogger(tempDir, Path.Combine(tempDir, "logs"), "IDENTITY");
            var journal = new ExecutionJournal(tempDir, robotLog);

            var tradeIntent = IdentityReplayScenarioDefinitions.BuildHydrationTradeIntent("IDENTITY_TRADE");
            var originalId = tradeIntent.ComputeIntentId();

            var td = IdentityReplayScenarioDefinitions.DeterministicTradingDate;
            var stream = IdentityReplayScenarioDefinitions.HydrationTradeStream;
            var inst = IdentityReplayScenarioDefinitions.HydrationExecutionInstrument;
            var utc = DateTimeOffset.UtcNow;

            journal.RecordSubmission(originalId, td, stream, inst, "ENTRY", "broker-s8", utc,
                expectedEntryPrice: 4500m, entryPrice: 4500m, stopPrice: 4400m, targetPrice: 4600m, direction: "Long");

            journal.RecordEntryFill(originalId, td, stream, 4500m, 1, utc, 5m, "Long", inst, inst);

            // Restart: new journal + adapter instances; journal files on disk unchanged.
            var journalAfterRestart = new ExecutionJournal(tempDir, robotLog);
            var adapter = new NinjaTraderSimAdapter(tempDir, tempDir, robotLog, journalAfterRestart);
            SetNonPublicField(adapter, "_ieaEngineExecutionInstrument", inst);
            InvokeHydrateIntentsFromOpenJournals(adapter);

            if (!TryGetIntentMap(adapter).TryGetValue(originalId, out var hydrated) || hydrated == null)
            {
                error = $"{IdentityReplayScenarioNames.S8_FULL_HYDRATION_RECONSTRUCTION}: IntentMap missing hydrated intent";
                return false;
            }

            if (!string.Equals(hydrated.ComputeIntentId(), originalId, StringComparison.Ordinal))
            {
                error = $"{IdentityReplayScenarioNames.S8_FULL_HYDRATION_RECONSTRUCTION}: reconstructed id != original";
                return false;
            }

            if (RobotLogsContainForbidden(tempDir, new[] { "INTENT_HYDRATION_ID_MISMATCH" }, out var bad))
            {
                error = $"{IdentityReplayScenarioNames.S8_FULL_HYDRATION_RECONSTRUCTION}: {bad}";
                return false;
            }

            adapter.SetEngineCallbacks(null, null, null, null, null, null, null, null, null, () => td);
            var submit = adapter.SubmitEntryOrder(originalId, inst, "Long", 4500m, 1, "MARKET", null, utc);
            if (string.Equals(submit.ErrorMessage, "INTENT_NOT_IN_MAP", StringComparison.Ordinal) ||
                string.Equals(submit.ErrorMessage, "INTENT_ID_MISMATCH", StringComparison.Ordinal))
            {
                error = $"{IdentityReplayScenarioNames.S8_FULL_HYDRATION_RECONSTRUCTION}: submit rejected at intent guard: {submit.ErrorMessage}";
                return false;
            }

            if (!RobotJsonlContainsOrderSubmitAttemptForIntent(tempDir, originalId))
            {
                error = $"{IdentityReplayScenarioNames.S8_FULL_HYDRATION_RECONSTRUCTION}: missing ORDER_SUBMIT_ATTEMPT for hydrated intent";
                return false;
            }

            log?.Invoke($"PASS {IdentityReplayScenarioNames.S8_FULL_HYDRATION_RECONSTRUCTION}");
            return true;
        }
        catch (Exception ex)
        {
            error = $"{IdentityReplayScenarioNames.S8_FULL_HYDRATION_RECONSTRUCTION}: {ex.Message}";
            return false;
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    private static bool RobotJsonlContainsOrderSubmitAttemptForIntent(string projectRoot, string intentId)
    {
        foreach (var path in Directory.GetFiles(projectRoot, "*.jsonl", SearchOption.AllDirectories))
        {
            string text;
            try { text = File.ReadAllText(path); }
            catch { continue; }
            if (text.IndexOf("ORDER_SUBMIT_ATTEMPT", StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (text.IndexOf(intentId, StringComparison.Ordinal) >= 0)
                return true;
        }
        return false;
    }

    private static ConcurrentDictionary<string, Intent> TryGetIntentMap(NinjaTraderSimAdapter adapter)
    {
        var mapField = typeof(NinjaTraderSimAdapter).GetField("_intentMap", BindingFlags.Instance | BindingFlags.NonPublic);
        if (mapField?.GetValue(adapter) is ConcurrentDictionary<string, Intent> map)
            return map;
        return new ConcurrentDictionary<string, Intent>(StringComparer.Ordinal);
    }

    private static void SetNonPublicField(object target, string fieldName, object? value)
    {
        var f = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        f?.SetValue(target, value);
    }

    private static void InvokeHydrateIntentsFromOpenJournals(NinjaTraderSimAdapter adapter)
    {
        var m = typeof(NinjaTraderSimAdapter).GetMethod("HydrateIntentsFromOpenJournals",
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (m == null)
            throw new InvalidOperationException("HydrateIntentsFromOpenJournals not found");
        m.Invoke(adapter, null);
    }
}
