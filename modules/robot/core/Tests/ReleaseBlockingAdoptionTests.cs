// Release-blocking adoption policy (reconnect / stale journal vs live exposure).
// Run: dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --test RELEASE_BLOCKING_ADOPTION

using System;
using System.Collections.Generic;
using System.IO;
using QTSW2.Robot.Core;
using QTSW2.Robot.Core.Execution;

namespace QTSW2.Robot.Core.Tests;

public static class ReleaseBlockingAdoptionTests
{
    public static (bool Pass, string? Error) RunReleaseBlockingAdoptionTests()
    {
        var (a, ea) = StaleLongRowsExcludedWhenLiveTaggedElsewhere();
        if (!a) return (false, ea);
        var (b, eb) = OpenJournalQtyStillBlocksWithoutTags();
        if (!b) return (false, eb);
        var (c, ec) = RobotTagStillBlocksWithZeroOpenQty();
        if (!c) return (false, ec);
        return (true, null);
    }

    /// <summary>Reconnect class: several stale long rows + one live row; only live ties tag/registry.</summary>
    private static (bool Pass, string? Error) StaleLongRowsExcludedWhenLiveTaggedElsewhere()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"RelBlock_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempRoot);
            var journalDir = Path.Combine(tempRoot, "data", "execution_journals");
            Directory.CreateDirectory(journalDir);

            var liveId = "liveintent00001";
            var staleIds = new[] { "staleaaaa00001", "staleaaaa00002", "staleaaaa00003", "staleaaaa00004" };

            foreach (var sid in staleIds)
            {
                WriteJournal(journalDir, sid, new ExecutionJournalEntry
                {
                    IntentId = sid,
                    TradingDate = "2026-04-01",
                    Stream = "T",
                    Instrument = "MES",
                    EntrySubmitted = true,
                    TradeCompleted = false,
                    EntryFilled = true,
                    EntryFilledQuantityTotal = 1,
                    ExitFilledQuantityTotal = 1,
                    Direction = "Long"
                });
            }

            WriteJournal(journalDir, liveId, new ExecutionJournalEntry
            {
                IntentId = liveId,
                TradingDate = "2026-04-01",
                Stream = "T",
                Instrument = "MES",
                EntrySubmitted = true,
                TradeCompleted = false,
                EntryFilled = true,
                EntryFilledQuantityTotal = 2,
                ExitFilledQuantityTotal = 1,
                Direction = "Long"
            });

            var log = new RobotLogger(Path.Combine(tempRoot, "log"));
            var journal = new ExecutionJournal(tempRoot, log);

            var tag = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { liveId };
            var reg = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { liveId };

            var n = journal.CountReleaseBlockingAdoptionCandidates("MES", "ES", brokerPositionQtyAbs: 2, brokerPositionQtySigned: 2,
                tag, reg);
            if (n != 1)
                return (false, $"Expected 1 blocking candidate (live only), got {n}");

            var audit = journal.BuildReleaseBlockingCandidateAudit("MES", "ES", 2, 2, tag, reg, sampleLimit: 20);
            if (audit.BlockingCandidateCount != 1)
                return (false, $"Audit blocking count expected 1, got {audit.BlockingCandidateCount}");
            if (!audit.BlockingIntentIdsSample.Contains(liveId, StringComparer.OrdinalIgnoreCase))
                return (false, "Blocking sample should include live intent");
            if (audit.RawCandidateCount != 5)
                return (false, $"Raw candidates expected 5, got {audit.RawCandidateCount}");

            return (true, null);
        }
        finally
        {
            try { Directory.Delete(tempRoot, true); } catch { /* */ }
        }
    }

    private static (bool Pass, string? Error) OpenJournalQtyStillBlocksWithoutTags()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"RelBlock_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempRoot);
            var journalDir = Path.Combine(tempRoot, "data", "execution_journals");
            Directory.CreateDirectory(journalDir);

            var orphan = "orphan000000001";
            WriteJournal(journalDir, orphan, new ExecutionJournalEntry
            {
                IntentId = orphan,
                TradingDate = "2026-04-01",
                Stream = "T",
                Instrument = "MES",
                EntrySubmitted = true,
                TradeCompleted = false,
                EntryFilled = true,
                EntryFilledQuantityTotal = 1,
                ExitFilledQuantityTotal = 0,
                Direction = "Long"
            });

            var log = new RobotLogger(Path.Combine(tempRoot, "log"));
            var journal = new ExecutionJournal(tempRoot, log);

            var n = journal.CountReleaseBlockingAdoptionCandidates("MES", "ES", 1, 1,
                Array.Empty<string>(), null);
            if (n != 1)
                return (false, $"Open qty must block without tag/registry: expected 1, got {n}");
            return (true, null);
        }
        finally
        {
            try { Directory.Delete(tempRoot, true); } catch { /* */ }
        }
    }

    private static (bool Pass, string? Error) RobotTagStillBlocksWithZeroOpenQty()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"RelBlock_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempRoot);
            var journalDir = Path.Combine(tempRoot, "data", "execution_journals");
            Directory.CreateDirectory(journalDir);

            var tid = "tag000000000001";
            WriteJournal(journalDir, tid, new ExecutionJournalEntry
            {
                IntentId = tid,
                TradingDate = "2026-04-01",
                Stream = "T",
                Instrument = "MES",
                EntrySubmitted = true,
                TradeCompleted = false,
                EntryFilled = true,
                EntryFilledQuantityTotal = 1,
                ExitFilledQuantityTotal = 1,
                Direction = "Long"
            });

            var log = new RobotLogger(Path.Combine(tempRoot, "log"));
            var journal = new ExecutionJournal(tempRoot, log);

            var tag = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { tid };
            var n = journal.CountReleaseBlockingAdoptionCandidates("MES", "ES", 1, 1, tag, tag);
            if (n != 1)
                return (false, $"Tagged intent should block even with 0 open journal qty; got {n}");
            return (true, null);
        }
        finally
        {
            try { Directory.Delete(tempRoot, true); } catch { /* */ }
        }
    }

    private static void WriteJournal(string journalDir, string intentId, ExecutionJournalEntry entry)
    {
        var path = Path.Combine(journalDir, $"2026-04-01_T_{intentId}.json");
        File.WriteAllText(path, JsonUtil.Serialize(entry));
    }
}
