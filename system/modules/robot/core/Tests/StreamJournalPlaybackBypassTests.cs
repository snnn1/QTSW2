// Stream journal bypass: StreamStateMachine with ignoreExistingStreamJournals=true (mirrors NT Playback-account path).
// Run: dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --test STREAM_JOURNAL_PLAYBACK_BYPASS
//
// Verifies: committed journals on disk are still loaded in normal modes; bypass yields fresh Committed=false.

using System;
using System.IO;
using QTSW2.Robot.Core;
using QTSW2.Robot.Core.Execution;

namespace QTSW2.Robot.Core.Tests;

public static class StreamJournalPlaybackBypassTests
{
    private const string StreamId = "SJB_TEST_S1_ES";

    public static (bool Pass, string? Error) RunAll()
    {
        var (p1, e1) = Test1_LiveModeLoadsCommittedJournal();
        if (!p1) return (false, e1);
        var (p2, e2) = Test2_PlaybackBypassIgnoresCommittedFile();
        if (!p2) return (false, e2);
        var (p3, e3) = Test3_NoJournalFileUnchanged();
        if (!p3) return (false, e3);
        var (p4, e4) = Test4_BarsRequestEligibilityNotCommittedWhenBypass();
        if (!p4) return (false, e4);
        return (true, null);
    }

    private static (bool Pass, string? Error) Test1_LiveModeLoadsCommittedJournal()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"SjbLive_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempRoot);
            var tradingDate = new DateOnly(2026, 6, 15);
            var journals = new JournalStore(tempRoot);
            SaveCommittedJournal(journals, tradingDate, StreamId);

            var sm = CreateSm(tempRoot, journals, tradingDate, ExecutionMode.LIVE, ignoreExistingStreamJournals: false);
            if (!sm.Committed)
                return (false, "Test1: expected Committed=true when journal on disk is committed (normal load)");
            return (true, null);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    private static (bool Pass, string? Error) Test2_PlaybackBypassIgnoresCommittedFile()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"SjbPb_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempRoot);
            var tradingDate = new DateOnly(2026, 6, 15);
            var journals = new JournalStore(tempRoot);
            SaveCommittedJournal(journals, tradingDate, StreamId);

            var sm = CreateSm(tempRoot, journals, tradingDate, ExecutionMode.SIM, ignoreExistingStreamJournals: true);
            if (sm.Committed)
                return (false, "Test2: expected Committed=false when ignoreExistingStreamJournals (playback bypass)");
            return (true, null);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    private static (bool Pass, string? Error) Test3_NoJournalFileUnchanged()
    {
        var tempNormal = Path.Combine(Path.GetTempPath(), $"SjbNoneA_{Guid.NewGuid():N}");
        var tempBypass = Path.Combine(Path.GetTempPath(), $"SjbNoneB_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempNormal);
            Directory.CreateDirectory(tempBypass);
            var tradingDate = new DateOnly(2026, 6, 15);
            var jNormal = new JournalStore(tempNormal);
            var jBypass = new JournalStore(tempBypass);

            var smNormal = CreateSm(tempNormal, jNormal, tradingDate, ExecutionMode.LIVE, ignoreExistingStreamJournals: false);
            var smBypass = CreateSm(tempBypass, jBypass, tradingDate, ExecutionMode.SIM, ignoreExistingStreamJournals: true);
            if (smNormal.Committed || smBypass.Committed)
                return (false, "Test3: expected both paths Committed=false when no journal file exists");
            return (true, null);
        }
        finally
        {
            try { Directory.Delete(tempNormal, recursive: true); } catch { }
            try { Directory.Delete(tempBypass, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// Engine skips arming / BarsRequest-style paths when <see cref="StreamStateMachine.Committed"/> — bypass must keep stream non-committed.
    /// </summary>
    private static (bool Pass, string? Error) Test4_BarsRequestEligibilityNotCommittedWhenBypass()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"SjbElig_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempRoot);
            var tradingDate = new DateOnly(2026, 6, 15);
            var journals = new JournalStore(tempRoot);
            SaveCommittedJournal(journals, tradingDate, StreamId);

            var smBypass = CreateSm(tempRoot, journals, tradingDate, ExecutionMode.SIM, ignoreExistingStreamJournals: true);
            if (smBypass.Committed)
                return (false, "Test4: bypass must not inherit committed terminal state from disk (eligibility proxy)");
            return (true, null);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    private static void SaveCommittedJournal(JournalStore journals, DateOnly tradingDate, string stream)
    {
        var day = tradingDate.ToString("yyyy-MM-dd");
        var journal = new StreamJournal
        {
            TradingDate = day,
            Stream = stream,
            Committed = true,
            CommitReason = "NO_TRADE_MARKET_CLOSE",
            LastState = "DONE",
            LastUpdateUtc = DateTimeOffset.UtcNow.ToString("o"),
            SlotStatus = SlotStatus.NO_TRADE
        };
        journals.Save(journal);
    }

    private static StreamStateMachine CreateSm(
        string tempRoot,
        JournalStore journals,
        DateOnly tradingDate,
        ExecutionMode mode,
        bool ignoreExistingStreamJournals)
    {
        var spec = OrderReconciliationRecoveryTests.LoadMinimalSpecForTests();
        var time = new TimeService(spec.timezone);
        var log = new RobotLogger(tempRoot);
        var directive = new TimetableStream
        {
            stream = StreamId,
            instrument = "ES",
            session = "S1",
            slot_time = "07:30",
            enabled = true,
            decision_time = "07:30"
        };

        return new StreamStateMachine(
            time,
            spec,
            log,
            journals,
            tradingDate,
            "hash",
            directive,
            mode,
            1,
            2,
            tempRoot,
            tempRoot,
            ignoreExistingStreamJournals,
            executionAdapter: new NullExecutionAdapter(log),
            executionJournal: new ExecutionJournal(tempRoot, log));
    }
}
