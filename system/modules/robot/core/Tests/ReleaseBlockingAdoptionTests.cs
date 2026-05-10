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
        var (a, ea) = RegistryTrustedActiveRowsDoNotBecomeAdoptionBlockers();
        if (!a) return (false, ea);
        var (b, eb) = TaggedOpenQtyWithoutRegistryStillBlocks();
        if (!b) return (false, eb);
        var (c, ec) = OpenJournalQtyStillBlocksWithoutTags();
        if (!c) return (false, ec);
        var (d, ed) = RobotTagStillBlocksWithZeroOpenQty();
        if (!d) return (false, ed);
        var (e, ee) = TaggedUnfilledEntryDoesNotBlockWhileBrokerFlat();
        if (!e) return (false, ee);
        return (true, null);
    }

    /// <summary>Normal active exposure: journal open row is already visible in the broker tags and IEA registry.</summary>
    private static (bool Pass, string? Error) RegistryTrustedActiveRowsDoNotBecomeAdoptionBlockers()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"RelBlock_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempRoot);
            var journalDir = RobotRunArtifactPaths.StateExecutionJournals(tempRoot);
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
            if (n != 0)
                return (false, $"Registry-trusted active exposure must not become an adoption blocker; got {n}");

            var audit = journal.BuildReleaseBlockingCandidateAudit("MES", "ES", 2, 2, tag, reg, sampleLimit: 20);
            if (audit.BlockingCandidateCount != 0)
                return (false, $"Audit blocking count expected 0, got {audit.BlockingCandidateCount}");
            if (!audit.ExcludedIntentIdsSample.Contains(liveId, StringComparer.OrdinalIgnoreCase))
                return (false, "Excluded sample should include registry-trusted live intent");
            if (!audit.ExclusionReasonsSample.Contains(ReleaseBlockingExclusionReasons.REGISTRY_TRUSTED_ACTIVE_EXPOSURE, StringComparer.OrdinalIgnoreCase))
                return (false, "Expected REGISTRY_TRUSTED_ACTIVE_EXPOSURE exclusion reason");
            if (audit.RawCandidateCount != 5)
                return (false, $"Raw candidates expected 5, got {audit.RawCandidateCount}");

            return (true, null);
        }
        finally
        {
            try { Directory.Delete(tempRoot, true); } catch { /* */ }
        }
    }

    private static (bool Pass, string? Error) TaggedOpenQtyWithoutRegistryStillBlocks()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"RelBlock_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempRoot);
            var journalDir = RobotRunArtifactPaths.StateExecutionJournals(tempRoot);
            Directory.CreateDirectory(journalDir);

            var tid = "tagopenunowned01";
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
                ExitFilledQuantityTotal = 0,
                Direction = "Long"
            });

            var log = new RobotLogger(Path.Combine(tempRoot, "log"));
            var journal = new ExecutionJournal(tempRoot, log);

            var tag = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { tid };
            var n = journal.CountReleaseBlockingAdoptionCandidates("MES", "ES", 1, 1, tag, Array.Empty<string>());
            if (n != 1)
                return (false, $"Tagged open qty without registry trust must block for adoption; got {n}");

            var diagnostics = journal.BuildReleaseBlockingCandidateDiagnostics("MES", "ES", 1, 1, tag, Array.Empty<string>());
            if (diagnostics.Count != 1 ||
                diagnostics[0].Category != BlockingCandidateCategory.BrokerVisibleAdoptable ||
                !diagnostics[0].RecoveryAdoptionShouldConsume)
                return (false, "Tagged open qty without registry should be classified BrokerVisibleAdoptable");

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
            var journalDir = RobotRunArtifactPaths.StateExecutionJournals(tempRoot);
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
            var journalDir = RobotRunArtifactPaths.StateExecutionJournals(tempRoot);
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

    private static (bool Pass, string? Error) TaggedUnfilledEntryDoesNotBlockWhileBrokerFlat()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"RelBlock_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempRoot);
            var journalDir = RobotRunArtifactPaths.StateExecutionJournals(tempRoot);
            Directory.CreateDirectory(journalDir);

            var tid = "taggedflatentry1";
            WriteJournal(journalDir, tid, new ExecutionJournalEntry
            {
                IntentId = tid,
                TradingDate = "2026-04-01",
                Stream = "T",
                Instrument = "MCL",
                EntrySubmitted = true,
                TradeCompleted = false,
                EntryFilled = false,
                Direction = "Short",
                BrokerOrderId = "broker-flat-working"
            });

            var log = new RobotLogger(Path.Combine(tempRoot, "log"));
            var journal = new ExecutionJournal(tempRoot, log);

            var tag = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { tid };
            var n = journal.CountReleaseBlockingAdoptionCandidates("MCL", "CL", brokerPositionQtyAbs: 0, brokerPositionQtySigned: 0, tag, null);
            if (n != 0)
                return (false, $"Tagged unfilled flat broker entry should not block; got {n}");

            var audit = journal.BuildReleaseBlockingCandidateAudit("MCL", "CL", 0, 0, tag, null, sampleLimit: 20);
            if (audit.BlockingCandidateCount != 0)
                return (false, $"Audit blocking count expected 0, got {audit.BlockingCandidateCount}");
            if (!audit.ExcludedIntentIdsSample.Contains(tid, StringComparer.OrdinalIgnoreCase))
                return (false, "Excluded sample should include tagged unfilled broker-flat entry");
            if (!audit.ExclusionReasonsSample.Contains(ReleaseBlockingExclusionReasons.LIVE_TAGGED_UNFILLED_ENTRY, StringComparer.OrdinalIgnoreCase))
                return (false, "Expected LIVE_TAGGED_UNFILLED_ENTRY exclusion reason");

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
