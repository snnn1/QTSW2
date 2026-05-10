// Multi-day playback scenario contract.
// Run: dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --test PLAYBACK_SCENARIO

using System;
using System.IO;
using System.Security.Cryptography;
using QTSW2.Robot.Core.Execution;

namespace QTSW2.Robot.Core.Tests;

public static class PlaybackScenarioTests
{
    private const string StreamId = "NG2";

    public static (bool Pass, string? Error) RunAll()
    {
        var a = ScenarioManifestLoadsAndSelectsEventClockDate();
        if (!a.Pass) return (false, $"manifest: {a.Error}");
        var p = ScenarioConfigPointerLoadsWhenEnvAbsent();
        if (!p.Pass) return (false, $"pointer: {p.Error}");
        var h = ScenarioManifestPreservesTimetableIdentityHash();
        if (!h.Pass) return (false, $"timetable_hash: {h.Error}");
        var anchor = PreScenarioStartupAnchorCanClampOnlyAtEngineStart();
        if (!anchor.Pass) return (false, $"startup_anchor: {anchor.Error}");
        var b = StreamJournalCarryoverPreservesInterruptedLifecycle();
        if (!b.Pass) return (false, $"carryover: {b.Error}");
        var r = RolloverRetentionEvidenceContract();
        if (!r.Pass) return (false, $"retention_contract: {r.Error}");
        var s = StreamStandDownDoesNotTerminalizeOpenCarriedLifecycle();
        if (!s.Pass) return (false, $"standdown_retention: {s.Error}");
        var c = PlaybackBypassStillIgnoresCommittedJournalWithoutScenario();
        if (!c.Pass) return (false, $"ordinary_playback: {c.Error}");
        var d = ScenarioEnvRejectedForNonPlaybackSim();
        if (!d.Pass) return (false, $"env_guard: {d.Error}");
        return (true, null);
    }

    private static (bool Pass, string? Error) ScenarioManifestLoadsAndSelectsEventClockDate()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"PlaybackScenario_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempRoot);
            Directory.CreateDirectory(Path.Combine(tempRoot, "tt"));
            File.WriteAllText(Path.Combine(tempRoot, "tt", "timetable_2026-05-05.json"), "{}");
            File.WriteAllText(Path.Combine(tempRoot, "tt", "timetable_2026-05-06.json"), "{}");

            var manifestPath = Path.Combine(tempRoot, "playback_scenario.json");
            File.WriteAllText(manifestPath,
                @"{
  ""scenario_id"": ""scenario-test"",
  ""mode"": ""multi_day_carryover"",
  ""run_id"": ""scenario_run"",
  ""dates"": [""2026-05-05"", ""2026-05-06""],
  ""timetables"": {
    ""2026-05-05"": { ""path"": ""tt/timetable_2026-05-05.json"" },
    ""2026-05-06"": { ""path"": ""tt/timetable_2026-05-06.json"" }
  }
}");

            var manifest = PlaybackScenarioManifest.Load(manifestPath);
            var beforeRollover = DateTimeOffset.Parse("2026-05-05T22:30:00+00:00");
            if (!manifest.TryResolveScenarioDate(beforeRollover, out var day1, out var day1Error) || day1 != "2026-05-05")
                return (false, $"expected 2026-05-05 before 18:00 CT, got {day1}, error={day1Error}");

            var afterRollover = DateTimeOffset.Parse("2026-05-05T23:30:00+00:00");
            if (!manifest.TryResolveScenarioDate(afterRollover, out var day2, out var day2Error) || day2 != "2026-05-06")
                return (false, $"expected 2026-05-06 after 18:00 CT, got {day2}, error={day2Error}");

            if (!manifest.TryResolveTimetablePath(tempRoot, "2026-05-06", out var path, out _, out var pathError))
                return (false, $"expected timetable path for 2026-05-06: {pathError}");
            if (!File.Exists(path))
                return (false, "resolved timetable path does not exist");

            return (true, null);
        }
        finally
        {
            try { Directory.Delete(tempRoot, true); } catch { }
        }
    }

    private static (bool Pass, string? Error) PreScenarioStartupAnchorCanClampOnlyAtEngineStart()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"PlaybackAnchor_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempRoot);
            Directory.CreateDirectory(Path.Combine(tempRoot, "tt"));
            foreach (var date in new[] { "2026-04-28", "2026-04-29", "2026-04-30", "2026-05-01", "2026-05-04" })
                File.WriteAllText(Path.Combine(tempRoot, "tt", $"timetable_{date}.json"), "{}");

            var manifestPath = Path.Combine(tempRoot, "playback_scenario.json");
            File.WriteAllText(manifestPath,
                @"{
  ""scenario_id"": ""anchor-test"",
  ""mode"": ""multi_day_carryover"",
  ""run_id"": ""anchor_run"",
  ""dates"": [""2026-04-28"", ""2026-04-29"", ""2026-04-30"", ""2026-05-01"", ""2026-05-04""],
  ""timetables"": {
    ""2026-04-28"": { ""path"": ""tt/timetable_2026-04-28.json"" },
    ""2026-04-29"": { ""path"": ""tt/timetable_2026-04-29.json"" },
    ""2026-04-30"": { ""path"": ""tt/timetable_2026-04-30.json"" },
    ""2026-05-01"": { ""path"": ""tt/timetable_2026-05-01.json"" },
    ""2026-05-04"": { ""path"": ""tt/timetable_2026-05-04.json"" }
  }
}");

            var manifest = PlaybackScenarioManifest.Load(manifestPath);
            var dataLoadedPreloadAnchor = DateTimeOffset.Parse("2026-04-26T22:01:00+00:00");

            if (manifest.TryResolveScenarioDate(dataLoadedPreloadAnchor, out var strictDate, out _))
                return (false, $"strict resolve should reject pre-scenario preload anchor, got {strictDate}");

            if (!manifest.IsBeforeFirstScenarioDate(dataLoadedPreloadAnchor, out var eventDate, out var firstDate))
                return (false, "expected preload anchor to be before first scenario date");
            if (eventDate != "2026-04-27" || firstDate != "2026-04-28")
                return (false, $"unexpected date classification event={eventDate}, first={firstDate}");

            if (!manifest.TryResolveScenarioDate(dataLoadedPreloadAnchor, allowPreStartClamp: true, out var clampedDate, out var clampError, out var clampedPreStart))
                return (false, $"expected prestart clamp: {clampError}");
            if (!clampedPreStart || clampedDate != "2026-04-28")
                return (false, $"expected clamp to 2026-04-28, got {clampedDate}, clamped={clampedPreStart}");

            var afterScenario = DateTimeOffset.Parse("2026-05-05T23:30:00+00:00");
            if (manifest.TryResolveScenarioDate(afterScenario, allowPreStartClamp: true, out var afterDate, out _, out _))
                return (false, $"post-scenario date must not clamp, got {afterDate}");

            return (true, null);
        }
        finally
        {
            try { Directory.Delete(tempRoot, true); } catch { }
        }
    }

    private static (bool Pass, string? Error) ScenarioConfigPointerLoadsWhenEnvAbsent()
    {
        var previous = Environment.GetEnvironmentVariable(PlaybackScenarioManifest.EnvVarName);
        var tempRoot = Path.Combine(Path.GetTempPath(), $"PlaybackPointer_{Guid.NewGuid():N}");
        try
        {
            Environment.SetEnvironmentVariable(PlaybackScenarioManifest.EnvVarName, null);
            Directory.CreateDirectory(Path.Combine(tempRoot, "configs", "robot"));
            Directory.CreateDirectory(Path.Combine(tempRoot, "tt"));
            File.WriteAllText(Path.Combine(tempRoot, "tt", "timetable_2026-05-05.json"), "{}");
            var manifestPath = Path.Combine(tempRoot, "playback_scenario.json");
            File.WriteAllText(manifestPath,
                @"{
  ""scenario_id"": ""pointer-test"",
  ""mode"": ""multi_day_carryover"",
  ""run_id"": ""pointer_run"",
  ""dates"": [""2026-05-05""],
  ""timetables"": {
    ""2026-05-05"": { ""path"": ""tt/timetable_2026-05-05.json"" }
  }
}");
            var pointerPath = Path.Combine(tempRoot, PlaybackScenarioManifest.ConfigPointerRelativePath);
            File.WriteAllText(pointerPath,
                $@"{{
  ""manifest_path"": ""{manifestPath.Replace("\\", "\\\\")}"",
  ""manifest_sha256"": ""{ComputeSha256(manifestPath)}"",
  ""scenario_id"": ""pointer-test"",
  ""run_id"": ""pointer_run""
}}");

            if (!PlaybackScenarioManifest.HasConfigured(tempRoot))
                return (false, "expected pointer file to mark scenario configured");
            if (!PlaybackScenarioManifest.TryLoadConfigured(tempRoot, out var manifest, out var error))
                return (false, $"expected pointer load: {error}");
            if (manifest?.scenario_id != "pointer-test")
                return (false, $"unexpected scenario_id: {manifest?.scenario_id}");
            return (true, null);
        }
        finally
        {
            Environment.SetEnvironmentVariable(PlaybackScenarioManifest.EnvVarName, previous);
            try { Directory.Delete(tempRoot, true); } catch { }
        }
    }

    private static (bool Pass, string? Error) ScenarioManifestPreservesTimetableIdentityHash()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"PlaybackHash_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempRoot);
            Directory.CreateDirectory(Path.Combine(tempRoot, "tt"));
            var timetablePath = Path.Combine(tempRoot, "tt", "timetable_2026-05-05.json");
            File.WriteAllText(timetablePath, "{}");
            var fileHash = ComputeSha256(timetablePath);
            var manifestPath = Path.Combine(tempRoot, "playback_scenario.json");
            File.WriteAllText(manifestPath,
                $@"{{
  ""scenario_id"": ""hash-test"",
  ""mode"": ""multi_day_carryover"",
  ""run_id"": ""hash_run"",
  ""dates"": [""2026-05-05""],
  ""timetables"": {{
    ""2026-05-05"": {{ ""path"": ""tt/timetable_2026-05-05.json"", ""hash"": ""{fileHash}"", ""timetable_hash"": ""identity-hash-0505"" }}
  }}
}}");

            var manifest = PlaybackScenarioManifest.Load(manifestPath);
            if (!manifest.TryResolveTimetablePath(tempRoot, "2026-05-05", out _, out var expectedFileHash, out var error))
                return (false, $"expected timetable path: {error}");
            if (expectedFileHash != fileHash)
                return (false, $"expected file hash {fileHash}, got {expectedFileHash}");
            if (manifest.GetTimetableIdentityHash("2026-05-05") != "identity-hash-0505")
                return (false, "timetable identity hash was not preserved");

            return (true, null);
        }
        finally
        {
            try { Directory.Delete(tempRoot, true); } catch { }
        }
    }

    private static (bool Pass, string? Error) StreamJournalCarryoverPreservesInterruptedLifecycle()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"PlaybackCarry_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempRoot);
            var previousDate = new DateOnly(2026, 5, 5);
            var nextDate = new DateOnly(2026, 5, 6);
            var journals = new JournalStore(tempRoot);
            var previous = new StreamJournal
            {
                TradingDate = previousDate.ToString("yyyy-MM-dd"),
                Stream = StreamId,
                Committed = false,
                LastState = StreamState.RANGE_LOCKED.ToString(),
                LastUpdateUtc = "2026-05-05T22:00:00+00:00",
                SlotStatus = SlotStatus.ACTIVE,
                SlotInstanceKey = "NG2_11:00_2026-05-05",
                ExecutionInterruptedByClose = true,
                OriginalIntentId = "original-intent",
                ReentrySubmitPending = true,
                ReentryIntentId = "reentry-intent",
                NextSlotTimeUtc = DateTimeOffset.Parse("2026-05-06T16:00:00+00:00")
            };
            journals.Save(previous);

            var sm = CreateSm(tempRoot, journals, previousDate, ignoreExistingStreamJournals: false);
            if (!sm.HasPlaybackCarryoverEvidence)
                return (false, "expected active interrupted stream to have playback carryover evidence");

            var eventUtc = DateTimeOffset.Parse("2026-05-05T23:30:00+00:00");
            sm.UpdateTradingDate(nextDate, eventUtc, allowCarryForwardActive: true);
            var carried = journals.TryLoad(nextDate.ToString("yyyy-MM-dd"), StreamId);
            if (carried == null)
                return (false, "expected cloned journal for next scenario day");
            if (carried.Committed)
                return (false, "carryover journal must remain open");
            if (!carried.ExecutionInterruptedByClose)
                return (false, "ExecutionInterruptedByClose must be preserved");
            if (carried.OriginalIntentId != "original-intent")
                return (false, "OriginalIntentId was not preserved");
            if (carried.ReentryIntentId != "reentry-intent" || !carried.ReentrySubmitPending)
                return (false, "reentry lifecycle was not preserved");
            if (carried.PriorJournalKey != "2026-05-05_NG2")
                return (false, $"PriorJournalKey mismatch: {carried.PriorJournalKey}");
            return (true, null);
        }
        finally
        {
            try { Directory.Delete(tempRoot, true); } catch { }
        }
    }

    private static (bool Pass, string? Error) RolloverRetentionEvidenceContract()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"PlaybackRetention_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempRoot);
            var tradingDate = new DateOnly(2026, 5, 5);
            var journals = new JournalStore(tempRoot);
            journals.Save(new StreamJournal
            {
                TradingDate = tradingDate.ToString("yyyy-MM-dd"),
                Stream = StreamId,
                Committed = false,
                LastState = StreamState.RANGE_LOCKED.ToString(),
                SlotStatus = SlotStatus.ACTIVE,
                ExecutionInterruptedByClose = true,
                OriginalIntentId = "original-intent",
                ReentrySubmitPending = true,
            });

            var retained = CreateSm(tempRoot, journals, tradingDate, ignoreExistingStreamJournals: false);
            if (!retained.HasTimetableRolloverRetentionEvidence)
                return (false, "forced-flatten interrupted lifecycle must be retained across rollover");

            var terminalDate = new DateOnly(2026, 5, 6);
            journals.Save(new StreamJournal
            {
                TradingDate = terminalDate.ToString("yyyy-MM-dd"),
                Stream = StreamId,
                Committed = true,
                CommitReason = "TARGET",
                LastState = StreamState.DONE.ToString(),
                SlotStatus = SlotStatus.COMPLETE,
            });
            var terminal = CreateSm(tempRoot, journals, terminalDate, ignoreExistingStreamJournals: false);
            if (terminal.HasTimetableRolloverRetentionEvidence)
                return (false, "terminal prior-day stream must not block new-day same stream id");

            return (true, null);
        }
        finally
        {
            try { Directory.Delete(tempRoot, true); } catch { }
        }
    }

    private static (bool Pass, string? Error) StreamStandDownDoesNotTerminalizeOpenCarriedLifecycle()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"PlaybackStandDown_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempRoot);
            var previousDate = new DateOnly(2026, 5, 5);
            var nextDate = new DateOnly(2026, 5, 6);
            var journals = new JournalStore(tempRoot);
            var log = new RobotLogger(tempRoot);
            var executionJournal = new ExecutionJournal(tempRoot, log);
            const string reentryIntentId = "reentry-open-intent";

            executionJournal.RecordSubmission(reentryIntentId, previousDate.ToString("yyyy-MM-dd"), StreamId, "MNG",
                "ENTRY", "broker-reentry", DateTimeOffset.Parse("2026-05-05T22:30:00+00:00"),
                entryPrice: 2.70m, stopPrice: 2.60m, targetPrice: 2.90m, beTriggerPrice: 2.80m, direction: "Long");
            executionJournal.RecordEntryFill(reentryIntentId, previousDate.ToString("yyyy-MM-dd"), StreamId, 2.70m, 1,
                DateTimeOffset.Parse("2026-05-05T22:30:01+00:00"), 10000m, "Long", "MNG", "NG");

            journals.Save(new StreamJournal
            {
                TradingDate = nextDate.ToString("yyyy-MM-dd"),
                Stream = StreamId,
                Committed = false,
                LastState = StreamState.RANGE_LOCKED.ToString(),
                LastUpdateUtc = "2026-05-06T00:00:00+00:00",
                SlotStatus = SlotStatus.ACTIVE,
                SlotInstanceKey = "NG2_11:00_2026-05-05",
                ExecutionInterruptedByClose = false,
                OriginalIntentId = "original-intent",
                ReentryIntentId = reentryIntentId,
                ReentrySubmitted = true,
                ReentryFilled = true,
                ProtectionSubmitted = true,
                ProtectionAccepted = true,
                PriorJournalKey = "2026-05-05_NG2"
            });

            var sm = CreateSm(tempRoot, journals, nextDate, ignoreExistingStreamJournals: false, executionJournal);
            sm.EnterRecoveryManage(DateTimeOffset.Parse("2026-05-06T01:00:00+00:00"), "TEST_STAND_DOWN");

            var retained = journals.TryLoad(nextDate.ToString("yyyy-MM-dd"), StreamId);
            if (retained == null)
                return (false, "expected retained journal after stand-down");
            if (retained.Committed)
                return (false, $"stand-down must not commit open carried lifecycle, got {retained.CommitReason}");
            if (retained.SlotStatus != SlotStatus.ACTIVE)
                return (false, $"stand-down must preserve ACTIVE slot status, got {retained.SlotStatus}");

            return (true, null);
        }
        finally
        {
            try { Directory.Delete(tempRoot, true); } catch { }
        }
    }

    private static (bool Pass, string? Error) PlaybackBypassStillIgnoresCommittedJournalWithoutScenario()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"PlaybackBypassStill_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempRoot);
            var tradingDate = new DateOnly(2026, 5, 5);
            var journals = new JournalStore(tempRoot);
            journals.Save(new StreamJournal
            {
                TradingDate = tradingDate.ToString("yyyy-MM-dd"),
                Stream = StreamId,
                Committed = true,
                CommitReason = "NO_TRADE_MARKET_CLOSE",
                LastState = StreamState.DONE.ToString(),
                SlotStatus = SlotStatus.NO_TRADE
            });

            var sm = CreateSm(tempRoot, journals, tradingDate, ignoreExistingStreamJournals: true);
            if (sm.Committed)
                return (false, "ordinary playback bypass must still ignore committed stream journal rows");
            return (true, null);
        }
        finally
        {
            try { Directory.Delete(tempRoot, true); } catch { }
        }
    }

    private static (bool Pass, string? Error) ScenarioEnvRejectedForNonPlaybackSim()
    {
        var previous = Environment.GetEnvironmentVariable("QTSW2_PLAYBACK_SCENARIO");
        var tempRoot = Path.Combine(Path.GetTempPath(), $"PlaybackEnvGuard_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempRoot);
            Environment.SetEnvironmentVariable("QTSW2_PLAYBACK_SCENARIO", Path.Combine(tempRoot, "playback_scenario.json"));
            try
            {
                _ = new RobotEngine(
                    tempRoot,
                    TimeSpan.FromSeconds(1),
                    ExecutionMode.SIM,
                    ignoreExistingStreamJournals: false,
                    playbackAccountDetected: false);
                return (false, "expected QTSW2_PLAYBACK_SCENARIO to fail closed for non-playback SIM");
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("Playback-account", StringComparison.OrdinalIgnoreCase))
            {
                return (true, null);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("QTSW2_PLAYBACK_SCENARIO", previous);
            try { Directory.Delete(tempRoot, true); } catch { }
        }
    }

    private static StreamStateMachine CreateSm(
        string tempRoot,
        JournalStore journals,
        DateOnly tradingDate,
        bool ignoreExistingStreamJournals,
        ExecutionJournal? executionJournal = null)
    {
        var spec = OrderReconciliationRecoveryTests.LoadMinimalSpecForTests();
        var time = new TimeService(spec.timezone);
        var log = new RobotLogger(tempRoot);
        executionJournal ??= new ExecutionJournal(tempRoot, log);
        var directive = new TimetableStream
        {
            stream = StreamId,
            instrument = "NG",
            session = "S2",
            slot_time = "11:00",
            enabled = true,
            decision_time = "11:00"
        };

        return new StreamStateMachine(
            time,
            spec,
            log,
            journals,
            tradingDate,
            "hash",
            directive,
            ExecutionMode.SIM,
            1,
            2,
            tempRoot,
            tempRoot,
            ignoreExistingStreamJournals,
            executionAdapter: new NullExecutionAdapter(log),
            executionJournal: executionJournal);
    }

    private static string ComputeSha256(string path)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(File.ReadAllBytes(path));
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }
}
