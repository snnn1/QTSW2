using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Globalization;
using System.Threading;

namespace QTSW2.Robot.Core;

using QTSW2.Robot.Contracts;
using QTSW2.Robot.Core.Execution;

public sealed partial class StreamStateMachine
{

    /// <summary>
    /// Recompute all time boundaries (Chicago and UTC) for a given trading date.
    /// This is the single source of truth for time boundary computation.
    /// </summary>
    private void RecomputeTimeBoundaries(DateOnly tradingDate)
    {
        RecomputeTimeBoundaries(tradingDate, _spec, _time);
    }
    
    /// <summary>
    /// Recompute all time boundaries (Chicago and UTC) for a given trading date.
    /// Overload for use during construction when instance fields aren't available yet.
    /// </summary>
    private void RecomputeTimeBoundaries(DateOnly tradingDate, ParitySpec spec, TimeService time)
    {
        // PHASE 1: Construct Chicago times directly (authoritative)
        // CRITICAL: Chicago time is the source of truth - UTC is derived from it
        // CRITICAL: Always construct windows using exchange trading hours (DST-aware)
        var rangeStartChicago = spec.sessions[Session].range_start_time;
        RangeStartChicagoTime = time.ConstructChicagoTime(tradingDate, rangeStartChicago);
        SlotTimeChicagoTime = time.ConstructChicagoTime(tradingDate, SlotTimeChicago);
        var marketCloseChicagoTime = time.ConstructChicagoTime(tradingDate, spec.entry_cutoff.market_close_time);
        var marketReopenTimeStr = SessionTimingPolicy.ResolveMarketReopenTime(spec);
        MarketReopenChicagoTime = time.ConstructChicagoTime(DateOnly.FromDateTime(marketCloseChicagoTime.Date), marketReopenTimeStr);
        
        // DIAGNOSTIC: Log RangeStartChicagoTime initialization
        // This proves initialization happens and tells you when and from what it is derived
        // Note: During construction, _log may not be initialized, so we check
        if (_log != null)
        {
            var utcNow = DateTimeOffset.UtcNow;
            _log.Write(RobotEvents.Base(_time, utcNow, tradingDate.ToString("yyyy-MM-dd"), Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                "RANGE_START_INITIALIZED", State.ToString(),
                new
                {
                    stream_id = Stream,
                    range_start_chicago = RangeStartChicagoTime.ToString("o"),
                    range_start_time_string = rangeStartChicago,
                    trading_date = tradingDate.ToString("yyyy-MM-dd"),
                    source = "RecomputeTimeBoundaries",
                    note = "Diagnostic: RangeStartChicagoTime initialized from spec"
                }));
        }
        
        // PHASE 2: Derive UTC times from Chicago times (derived representation)
        RangeStartUtc = time.ConvertChicagoToUtc(RangeStartChicagoTime);
        SlotTimeUtc = time.ConvertChicagoToUtc(SlotTimeChicagoTime);
        MarketCloseUtc = time.ConvertChicagoToUtc(marketCloseChicagoTime);
        
        // CRITICAL: Check for DST transition on this trading date
        // Timezone edge case mitigation: Detect DST transitions that can cause missing/duplicate hours
        var startOffset = RangeStartChicagoTime.Offset;
        var endOffset = SlotTimeChicagoTime.Offset;
        var dstTransitionDetected = startOffset != endOffset;
        
        if (dstTransitionDetected)
        {
            // Log DST transition warning (only if we have logging available)
            // Note: During construction, _log may not be initialized, so we check
            if (_log != null)
            {
                _log.Write(RobotEvents.Base(time, DateTimeOffset.UtcNow, tradingDate.ToString("yyyy-MM-dd"), Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                    "DST_TRANSITION_DETECTED", "STREAM_INIT",
                    new
                    {
                        trading_date = tradingDate.ToString("yyyy-MM-dd"),
                        range_start_chicago = RangeStartChicagoTime.ToString("o"),
                        slot_time_chicago = SlotTimeChicagoTime.ToString("o"),
                        start_offset = startOffset.ToString(),
                        end_offset = endOffset.ToString(),
                        note = "DST transition detected - session may have missing or duplicate hour. Expected bar count may differ from nominal."
                    }));
            }
        }
    }
    
    /// <summary>
    /// Reset all daily state flags for a new trading day.
    /// Called during trading date rollover to clear accumulated state.
    /// </summary>
    private void ResetDailyState()
    {
        // Range tracking
        RangeHigh = null;
        RangeLow = null;
        FreezeClose = null;
        FreezeCloseSource = "UNSET";
        
        // Entry tracking
        _entryDetected = false;
        _intendedDirection = null;
        _intendedEntryPrice = null;
        _rangeLocked = false;
        _rangeLockCommitted = false;
        _rangeLockAttemptedAtUtc = null;
        _rangeLockFailureCount = 0;
        _breakoutLevelsMissing = false;
        _preHydrationComplete = false;
        
        // Reset bar source tracking counters
        lock (_barBufferLock)
        {
            _historicalBarCount = 0;
            _liveBarCount = 0;
            _dedupedBarCount = 0;
            _barSourceMap.Clear(); // Clear source tracking map
        }
        _filteredFutureBarCount = 0;
        _filteredPartialBarCount = 0;
        
        // Assertion flags
        _rangeIntentAssertEmitted = false;
        _firstBarAcceptedAssertEmitted = false;
        _rangeLockAssertEmitted = false;
        
        // Pre-hydration and gap tracking
        // CRITICAL: Since we clear the bar buffer, we need to re-run pre-hydration
        _preHydrationComplete = false;
        _largestSingleGapMinutes = 0.0;
        _totalGapMinutes = 0.0;
        _lastBarOpenChicago = null;
        _rangeInvalidated = false;
        _rangeInvalidatedNotified = false;

        _postSlotExcursionHasSamples = false;
        _postSlotMaxHighSinceSlot = decimal.MinValue;
        _postSlotMinLowSinceSlot = decimal.MaxValue;
        
        // Logging state
        _slotEndSummaryLogged = false;
        _lastHeartbeatUtc = null;
        _lastBarReceivedUtc = null;
        _lastBarTimestampUtc = null;
        _lastRangeComputeFailedLogUtc = null;
    }

    /// <summary>
    /// Get the current bar buffer count (thread-safe).
    /// </summary>
    private int GetBarBufferCount()
    {
        lock (_barBufferLock)
        {
            return _barBuffer.Count;
        }
    }

    /// <summary>
    /// Get a snapshot of the bar buffer (thread-safe copy).
    /// </summary>
    private List<Bar> GetBarBufferSnapshot()
    {
        lock (_barBufferLock)
        {
            return new List<Bar>(_barBuffer);
        }
    }
    
    /// <summary>
    /// Calculate next slot time UTC (next occurrence of slot_time).
    /// Reference: Analyzer's get_next_slot_time() logic.
    /// Assumes standard trading calendar. Early closes/holidays may cause slot expiry timing drift.
    /// </summary>
    private DateTimeOffset CalculateNextSlotTimeUtc(DateOnly tradingDate, string slotTimeChicago, TimeService time)
    {
        var currentChicago = time.ConstructChicagoTime(tradingDate, slotTimeChicago);
        var currentDate = DateOnly.FromDateTime(currentChicago.Date);
        var currentDateAsDateTime = currentChicago.Date; // For DayOfWeek property
        
        // Determine next trading day (handle Friday→Monday skip)
        DateOnly nextDate;
        if (currentDateAsDateTime.DayOfWeek == DayOfWeek.Friday)
        {
            // Friday → Monday (skip weekend)
            nextDate = currentDate.AddDays(3);
        }
        else
        {
            // Regular day → next day
            nextDate = currentDate.AddDays(1);
        }
        
        // Construct next slot time in Chicago timezone
        var nextSlotTimeChicago = time.ConstructChicagoTime(nextDate, slotTimeChicago);
        
        // Convert to UTC
        var nextSlotTimeUtc = time.ConvertChicagoToUtc(nextSlotTimeChicago);
        
        return nextSlotTimeUtc;
    }
    
    /// <summary>
    /// Check if entry fill exists for given intent ID (helper for post-entry verification).
    /// </summary>
    private bool HasEntryFillForIntentId(string intentId, string? tradingDate = null)
    {
        if (_executionJournal == null || string.IsNullOrWhiteSpace(intentId)) return false;
        
        // Try to load ExecutionJournalEntry by scanning journal directory
        // If tradingDate is null, search across all dates (for cross-date scenarios)
        var pattern = tradingDate != null
            ? $"{tradingDate}_*_{intentId}.json"
            : $"*_*_{intentId}.json";
        var journalDir = RobotRunArtifactPaths.StateExecutionJournals(_robotStateRoot);
        
        try
        {
            if (!Directory.Exists(journalDir)) return false;
            
            var files = Directory.GetFiles(journalDir, pattern);
            foreach (var file in files)
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var entry = JsonUtil.Deserialize<ExecutionJournalEntry>(json);
                    if (entry != null && (entry.EntryFilled || entry.EntryFilledQuantityTotal > 0))
                    {
                        return true;
                    }
                }
                catch
                {
                    // Skip corrupted files
                }
            }
        }
        catch
        {
            // Fail-safe: return false on error
        }
        
        return false;
    }

    private ExecutionJournalEntry? LoadExecutionJournalEntryForIntentId(string intentId, string? tradingDate = null)
    {
        if (string.IsNullOrWhiteSpace(intentId)) return null;

        var pattern = tradingDate != null
            ? $"{tradingDate}_*_{intentId}.json"
            : $"*_*_{intentId}.json";
        var journalDir = RobotRunArtifactPaths.StateExecutionJournals(_robotStateRoot);

        try
        {
            if (!Directory.Exists(journalDir)) return null;

            foreach (var file in Directory.GetFiles(journalDir, pattern))
            {
                try
                {
                    var entry = JsonUtil.Deserialize<ExecutionJournalEntry>(File.ReadAllText(file));
                    if (entry != null && string.Equals(entry.IntentId, intentId, StringComparison.Ordinal))
                        return entry;
                }
                catch
                {
                    // Skip corrupted files
                }
            }
        }
        catch
        {
            // Fail-safe: return null on error
        }

        return null;
    }

    private List<ExecutionJournalEntry> LoadExecutionJournalEntriesForStream(string tradingDate, string stream)
    {
        var entries = new List<ExecutionJournalEntry>();
        if (string.IsNullOrWhiteSpace(tradingDate) || string.IsNullOrWhiteSpace(stream))
            return entries;

        var journalDir = RobotRunArtifactPaths.StateExecutionJournals(_robotStateRoot);
        var pattern = $"{tradingDate}_{stream}_*.json";

        try
        {
            if (!Directory.Exists(journalDir)) return entries;

            foreach (var file in Directory.GetFiles(journalDir, pattern))
            {
                try
                {
                    var entry = JsonUtil.Deserialize<ExecutionJournalEntry>(File.ReadAllText(file));
                    if (entry != null)
                        entries.Add(entry);
                }
                catch
                {
                    // Skip corrupted files
                }
            }
        }
        catch
        {
            // Fail-safe: return any entries loaded before the error
        }

        return entries;
    }

    private bool TryFindOpenExecutionJournalEntryForStream(string tradingDate, string stream, out ExecutionJournalEntry? openEntry)
    {
        openEntry = null;
        var journalDir = RobotRunArtifactPaths.StateExecutionJournals(_robotStateRoot);
        var pattern = $"{tradingDate}_{stream}_*.json";

        try
        {
            if (!Directory.Exists(journalDir)) return false;

            foreach (var file in Directory.GetFiles(journalDir, pattern))
            {
                try
                {
                    var entry = JsonUtil.Deserialize<ExecutionJournalEntry>(File.ReadAllText(file));
                    if (entry == null || !entry.EntryFilled || entry.TradeCompleted)
                        continue;
                    if (ExecutionJournal.GetEntryRemainingOpenQuantity(entry) <= 0)
                        continue;

                    openEntry = entry;
                    return true;
                }
                catch
                {
                    // Skip corrupted files
                }
            }
        }
        catch
        {
            // Fail-safe: no open entry found
        }

        return false;
    }

    private static bool IsFlattenRequestAcceptedPendingBroker(FlattenResult? r)
    {
        if (r == null) return false;
        if (r.Success) return true;
        var msg = r.ErrorMessage ?? "";
        return msg.IndexOf("Enqueued", StringComparison.OrdinalIgnoreCase) >= 0
               || string.Equals(msg, "SESSION_CLOSE_FLATTEN_ENQUEUED", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPendingUnfilledEntryJournal(ExecutionJournalEntry? entry)
        => entry != null &&
           entry.EntrySubmitted &&
           !entry.TradeCompleted &&
           !entry.EntryFilled &&
           entry.EntryFilledQuantityTotal <= 0 &&
           !entry.Rejected;

}
