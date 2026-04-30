using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace QTSW2.Robot.Core;

using QTSW2.Robot.Core.Diagnostics;

public sealed partial class RobotEngine
{

    // BarsRequest completion tracking (per instrument)
    // Tracks pending BarsRequest to prevent range lock before bars arrive
    private readonly Dictionary<string, DateTimeOffset> _barsRequestPending = new Dictionary<string, DateTimeOffset>();
    private readonly Dictionary<string, DateTimeOffset> _barsRequestCompleted = new Dictionary<string, DateTimeOffset>();
    private const int BARSREQUEST_TIMEOUT_MINUTES = 5; // Timeout after 5 minutes if BarsRequest doesn't complete

    // Bar rejection tracking for summary events
    private readonly Dictionary<string, BarRejectionStats> _barRejectionStats = new Dictionary<string, BarRejectionStats>();
    private DateTimeOffset? _lastBarRejectionSummaryUtc = null;
    private const double BAR_REJECTION_SUMMARY_INTERVAL_MINUTES = 5.0;

    // ENGINE_CPU_PROFILE (data/engine_cpu_profile.enabled): accumulate OnBar lock hold time between tick emissions
    private long _cpuProfileOnBarLockMsAccum;
    private int _cpuProfileOnBarCount;
    private DateTimeOffset _lastEngineCpuProfileUtc = DateTimeOffset.MinValue;
    private const double EngineCpuProfileEmitIntervalSeconds = 5.0;

    // Helper class for tracking bar rejection statistics
    private class BarRejectionStats
    {
        public int PartialRejected { get; set; }
        public int DateMismatch { get; set; }
        public int BeforeDateLocked { get; set; }
        public int TotalAccepted { get; set; }
        public DateTimeOffset LastUpdateUtc { get; set; }
    }

    private readonly Dictionary<string, DateTimeOffset> _lastBarDateMismatchDetailLogUtc =
        new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
    private const double BarDateMismatchDetailLogIntervalMinutes = 30.0;

    /// <summary>
    /// Mark BarsRequest as pending for an instrument.
    /// Called when BarsRequest is initiated.
    /// </summary>
    public void MarkBarsRequestPending(string instrument, DateTimeOffset utcNow)
    {
        lock (_engineLock)
        {
            var canonicalInstrument = GetCanonicalInstrument(instrument) ?? instrument;
            _barsRequestPending[canonicalInstrument] = utcNow;
            // Remove from completed if it was there (for restart scenarios)
            _barsRequestCompleted.Remove(canonicalInstrument);

            LogEngineEvent(utcNow, "BARSREQUEST_PENDING_MARKED", new
            {
                instrument = instrument,
                canonical_instrument = canonicalInstrument,
                note = "BarsRequest marked as pending - range lock will wait for completion"
            });
        }
    }

    /// <summary>
    /// Mark BarsRequest as completed for an instrument.
    /// Called when LoadPreHydrationBars receives bars.
    /// </summary>
    public void MarkBarsRequestCompleted(string instrument, DateTimeOffset utcNow)
    {
        lock (_engineLock)
        {
            var canonicalInstrument = GetCanonicalInstrument(instrument) ?? instrument;
            _barsRequestPending.Remove(canonicalInstrument);
            _barsRequestCompleted[canonicalInstrument] = utcNow;

            LogEngineEvent(utcNow, "BARSREQUEST_COMPLETED_MARKED", new
            {
                instrument = instrument,
                canonical_instrument = canonicalInstrument,
                note = "BarsRequest marked as completed - range lock can proceed"
            });
            TryEmitEventDrivenSnapshot(utcNow, "BARSREQUEST_COMPLETE");
        }
    }

    /// <summary>
    /// Check if BarsRequest is pending for an instrument.
    /// Returns true if BarsRequest is pending and hasn't timed out.
    /// </summary>
    public bool IsBarsRequestPending(string instrument, DateTimeOffset utcNow)
    {
        lock (_engineLock)
        {
            var canonicalInstrument = GetCanonicalInstrument(instrument) ?? instrument;

            if (!_barsRequestPending.TryGetValue(canonicalInstrument, out var pendingTime))
            {
                // No pending request - check if it was already completed
                if (_barsRequestCompleted.ContainsKey(canonicalInstrument))
                {
                    return false; // Already completed
                }
                return false; // Never requested
            }

            // Check for timeout
            var elapsedMinutes = (utcNow - pendingTime).TotalMinutes;
            if (elapsedMinutes >= BARSREQUEST_TIMEOUT_MINUTES)
            {
                // Timeout - remove from pending and log warning
                _barsRequestPending.Remove(canonicalInstrument);
                LogEngineEvent(utcNow, "BARSREQUEST_TIMEOUT", new
                {
                    instrument = instrument,
                    canonical_instrument = canonicalInstrument,
                    elapsed_minutes = elapsedMinutes,
                    timeout_minutes = BARSREQUEST_TIMEOUT_MINUTES,
                    note = "BarsRequest timed out - allowing range lock to proceed (may have insufficient bars)"
                });
                return false; // Timed out
            }

            return true; // Still pending
        }
    }

    /// <summary>
    /// Process a bar update from NinjaTrader.
    /// </summary>
    /// <param name="barUtc">Bar timestamp in UTC. CONTRACT: This represents the bar open time.
    /// NinjaTrader emits bars on close; this system normalizes all bars to open time for Analyzer parity and deterministic range logic.
    /// This assumes 1-minute bars and is guarded by an invariant.</param>
    /// <param name="instrument">Instrument symbol (e.g., "ES")</param>
    /// <param name="open">Bar open price</param>
    /// <param name="high">Bar high price</param>
    /// <param name="low">Bar low price</param>
    /// <param name="close">Bar close price</param>
    /// <param name="utcNow">Current UTC time for age validation</param>
    public void OnBar(DateTimeOffset barUtc, string instrument, decimal open, decimal high, decimal low, decimal close, DateTimeOffset utcNow)
    {
        lock (_engineLock)
        {
            var cpuBarWatch = EngineCpuProfile.IsEnabled() ? Stopwatch.StartNew() : null;
            try
            {
            if (_spec is null || _time is null) return;

        // CRITICAL: Reject future bars and validate bar timing
        // FIX #2: For OnBarClose sources, bars are already closed, so we don't need strict age requirements
        // - BarsRequest returns fully closed bars (good)
        // - OnBarClose (NinjaTrader) provides closed bars when callback fires
        // - Only reject future bars (negative age) which indicate clock/timezone issues
        //
        // Rule: Accept bars with age >= 0 (current or past), reject future bars (age < 0)
        // This allows OnBarClose bars to be processed immediately while preventing future bar contamination
        var barAgeMinutes = (utcNow - barUtc).TotalMinutes;
        const double FUTURE_BAR_THRESHOLD_MINUTES = -0.1; // Reject bars more than 0.1 minutes in the future

        if (barAgeMinutes < FUTURE_BAR_THRESHOLD_MINUTES)
        {
            // Bar is in the future - indicates clock/timezone issue, reject it
            // Track rejection statistics
            if (!_barRejectionStats.TryGetValue(instrument, out var stats))
            {
                stats = new BarRejectionStats();
                _barRejectionStats[instrument] = stats;
            }
            stats.PartialRejected++;
            stats.LastUpdateUtc = utcNow;

            var nowChicago = _time.ConvertUtcToChicago(utcNow);
            var barChicagoTimeRejected = _time.ConvertUtcToChicago(barUtc);

            // Find all streams matching this instrument for diagnostic purposes
            var matchingStreamIds = new List<string>();
            foreach (var s in _streams.Values)
            {
                if (s.IsSameInstrument(instrument))
                {
                    matchingStreamIds.Add(s.Stream);
                }
            }

            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "BAR_PARTIAL_REJECTED", state: "ENGINE",
                new
                {
                    instrument = instrument,
                    stream_id = matchingStreamIds.Count > 0 ? string.Join(",", matchingStreamIds) : "NO_STREAMS",
                    trading_date = TradingDateString,
                    bar_timestamp_utc = barUtc.ToString("o"),
                    bar_timestamp_chicago = barChicagoTimeRejected.ToString("o"),
                    current_time_utc = utcNow.ToString("o"),
                    current_time_chicago = nowChicago.ToString("o"),
                    bar_age_minutes = Math.Round(barAgeMinutes, 3),
                    future_bar_threshold_minutes = FUTURE_BAR_THRESHOLD_MINUTES,
                    rejection_reason = "FUTURE_BAR",
                    note = "Bar rejected - bar timestamp is in the future relative to engine time. This indicates a clock synchronization or timezone conversion issue."
                }));

            // HIGH-SIGNAL WARNING: If future bar rejection occurs continuously after trading date is locked
            if (_activeTradingDate.HasValue && stats.PartialRejected >= 10)
            {
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "BAR_REJECTION_CONTINUOUS_FUTURE", state: "ENGINE",
                    new
                    {
                        instrument = instrument,
                        future_bar_rejection_count = stats.PartialRejected,
                        trading_date = TradingDateString,
                        warning = "Continuous future bar rejections detected - indicates persistent clock/timezone issue",
                        note = "This may indicate a system clock synchronization problem or timezone conversion error"
                    }));
            }

            // Log rejection summary if threshold exceeded (rate-limited)
            LogBarRejectionSummaryIfNeeded(utcNow);
            return; // Reject future bar
        }

        // Health monitor: record bar reception (early, before other processing)
        _healthMonitor?.OnBar(instrument, barUtc);

        // Update last tick timestamp for broker synchronization check
        // Bar updates indicate connection health, so track them for recovery sync
        _lastTickUtc = utcNow;

        // Validate bar date against locked trading date from timetable
        var barChicagoTime = _time.ConvertUtcToChicago(barUtc);
        var barChicagoDate = DateOnly.FromDateTime(barChicagoTime.DateTime);

        // Trading date should be locked from timetable - if not, log error and ignore bar
        if (!_activeTradingDate.HasValue)
        {
            // Track rejection statistics
            if (!_barRejectionStats.TryGetValue(instrument, out var stats))
            {
                stats = new BarRejectionStats();
                _barRejectionStats[instrument] = stats;
            }
            stats.BeforeDateLocked++;
            stats.LastUpdateUtc = utcNow;

            var nowChicago = _time.ConvertUtcToChicago(utcNow);
            var timetableTradingDate = _lastTimetable?.trading_date ?? "NOT_LOADED";

            // Find all streams matching this instrument for diagnostic purposes
            var matchingStreamIds = new List<string>();
            foreach (var s in _streams.Values)
            {
                if (s.IsSameInstrument(instrument))
                {
                    matchingStreamIds.Add(s.Stream);
                }
            }

            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "BAR_RECEIVED_BEFORE_DATE_LOCKED", state: "ENGINE",
                new
                {
                    instrument = instrument,
                    stream_id = matchingStreamIds.Count > 0 ? string.Join(",", matchingStreamIds) : "NO_STREAMS",
                    trading_date = "",
                    bar_timestamp_utc = barUtc.ToString("o"),
                    bar_timestamp_chicago = barChicagoTime.ToString("o"),
                    bar_trading_date = barChicagoDate.ToString("yyyy-MM-dd"),
                    current_time_utc = utcNow.ToString("o"),
                    current_time_chicago = nowChicago.ToString("o"),
                    timetable_trading_date = timetableTradingDate,
                    timetable_path = _timetablePath,
                    rejection_reason = "TRADING_DATE_NOT_LOCKED",
                    note = "Bar received before trading date locked from timetable - this should not happen in normal operation"
                }));

            // HIGH-SIGNAL WARNING: Bars rejected after engine start indicates timetable loading failure
            if (stats.BeforeDateLocked >= 5)
            {
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "BAR_REJECTION_CONTINUOUS_NO_DATE_LOCK", state: "ENGINE",
                    new
                    {
                        instrument = instrument,
                        rejection_count = stats.BeforeDateLocked,
                        timetable_path = _timetablePath,
                        timetable_trading_date = timetableTradingDate,
                        warning = "Multiple bars rejected due to trading date not locked - timetable may have failed to load",
                        note = "Check timetable file exists and is valid. Engine should lock trading date at startup."
                    }));
            }

            // Log rejection summary if threshold exceeded (rate-limited)
            LogBarRejectionSummaryIfNeeded(utcNow);
            return; // Ignore bar - trading date should be locked from timetable
        }

        // Validate bar falls within trading session window for active trading date
        // Session window: [previous_day session_start CST, trading_date 16:00 CST)
        // This replaces calendar date comparison which was invalid for futures (session starts evening before)
        // PHASE 3: Use canonical instrument for session window lookup (session windows are per canonical market)
        var canonicalInstrumentForSession = GetCanonicalInstrument(instrument);
        var (sessionStartChicago, sessionEndChicago) = GetSessionWindow(_activeTradingDate.Value, canonicalInstrumentForSession);

        // CRITICAL FIX: Allow historical bars from dates before the trading date
        // Bars from dates before the trading date are historical data and should be accepted
        // Only reject bars that are:
        // 1. From dates after the trading date (future bars)
        // 2. From dates that are too far in the past (more than 7 days before trading date)
        // 3. From dates within the trading date but outside the session window
        var tradingDateStr = _activeTradingDate.Value.ToString("yyyy-MM-dd");
        var barTradingDateStr = barChicagoDate.ToString("yyyy-MM-dd");
        var barDate = barChicagoDate;
        var tradingDate = _activeTradingDate.Value;

        // Check if bar is from a date before the trading date (historical data)
        var isHistoricalBar = barDate < tradingDate;
        // Calculate days difference (compatible with older .NET versions that don't have DayNumber)
        // Convert DateOnly to DateTime at midnight for subtraction
        var tradingDateTime = new DateTime(tradingDate.Year, tradingDate.Month, tradingDate.Day);
        var barDateTime = new DateTime(barDate.Year, barDate.Month, barDate.Day);
        var daysBeforeTradingDate = isHistoricalBar ? (tradingDateTime - barDateTime).Days : 0;

        // Allow historical bars from up to 7 days before trading date
        // This handles cases where NinjaTrader sends historical bars from previous days
        const int MAX_HISTORICAL_DAYS = 7;
        var isTooOldHistorical = isHistoricalBar && daysBeforeTradingDate > MAX_HISTORICAL_DAYS;

        // Check if bar is from a date after the trading date (future bar)
        var isFutureBar = barDate > tradingDate;

        // Check if bar is within session window (for bars from trading date)
        var isWithinSessionWindow = barChicagoTime >= sessionStartChicago && barChicagoTime < sessionEndChicago;

        // Reject bar if:
        // 1. It's a future bar (from date after trading date)
        // 2. It's too old historical (more than 7 days before trading date)
        // 3. It's from the trading date but outside the session window
        var shouldReject = isFutureBar || isTooOldHistorical || (!isHistoricalBar && !isWithinSessionWindow);

        if (shouldReject)
        {
            // Bar is outside session window or invalid - log mismatch and reject
            // BAR_DATE_MISMATCH now means "bar outside trading session window" (not calendar date mismatch)

            // Track rejection statistics
            if (!_barRejectionStats.TryGetValue(instrument, out var stats))
            {
                stats = new BarRejectionStats();
                _barRejectionStats[instrument] = stats;
            }
            stats.DateMismatch++;
            stats.LastUpdateUtc = utcNow;

            // Enhanced diagnostic logging
            var nowChicago = _time.ConvertUtcToChicago(utcNow);
            var timetableTradingDate = _lastTimetable?.trading_date ?? "NOT_LOADED";

            // Find all streams matching this instrument for diagnostic purposes
            var matchingStreamIds = new List<string>();
            foreach (var s in _streams.Values)
            {
                if (s.IsSameInstrument(instrument))
                {
                    matchingStreamIds.Add(s.Stream);
                }
            }

            // Determine rejection reason
            string rejectionReason;
            if (isFutureBar)
            {
                rejectionReason = "FUTURE_DATE";
            }
            else if (isTooOldHistorical)
            {
                rejectionReason = "TOO_OLD_HISTORICAL";
            }
            else if (barChicagoTime < sessionStartChicago)
            {
                rejectionReason = "BEFORE_SESSION_START";
            }
            else
            {
                rejectionReason = "AFTER_SESSION_END";
            }

            if (ShouldLogBarDateMismatchDetail(instrument, rejectionReason, utcNow))
            {
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: tradingDateStr, eventType: "BAR_DATE_MISMATCH", state: "ENGINE",
                    new
                    {
                        // Existing fields (kept for backward compatibility)
                        locked_trading_date = tradingDateStr,
                        bar_trading_date = barTradingDateStr,
                        bar_timestamp_utc = barUtc.ToString("o"),
                        bar_timestamp_chicago = barChicagoTime.ToString("o"),
                        instrument = instrument,
                        rejection_reason = rejectionReason,
                        date_alignment_note = $"Bar is outside trading session window for trading date {tradingDateStr}",
                        note = "Bar ignored - outside trading session window (BAR_DATE_MISMATCH now means 'bar outside session window', not 'calendar date mismatch')",
                        detail_log_interval_minutes = BarDateMismatchDetailLogIntervalMinutes,

                        // DIAGNOSTIC FIELDS (high priority)
                        active_trading_date = tradingDateStr, // Engine's active trading date
                        bar_utc = barUtc.ToString("o"), // Canonical bar timestamp passed into engine
                        bar_chicago = barChicagoTime.ToString("o"), // Derived Chicago time from bar_utc
                        bar_chicago_date = barChicagoDate.ToString("yyyy-MM-dd"), // Date-only from bar_chicago
                        now_utc = utcNow.ToString("o"), // Engine tick time (not DateTimeOffset.UtcNow from strategy)
                        now_chicago = nowChicago.ToString("o"), // Derived Chicago time from now_utc
                        timetable_trading_date = timetableTradingDate, // Raw string read from timetable
                        stream_id = matchingStreamIds.Count > 0 ? string.Join(",", matchingStreamIds) : "NO_STREAMS", // All streams matching this instrument

                        // NEW: Session window fields
                        session_start_chicago = sessionStartChicago.ToString("o"),
                        session_end_chicago = sessionEndChicago.ToString("o"),

                        // NEW: Historical bar detection fields
                        is_historical_bar = isHistoricalBar,
                        days_before_trading_date = daysBeforeTradingDate,
                        is_future_bar = isFutureBar,
                        is_too_old_historical = isTooOldHistorical,
                        max_historical_days = MAX_HISTORICAL_DAYS
                    }));
            }

            // Log rejection summary if threshold exceeded (rate-limited)
            LogBarRejectionSummaryIfNeeded(utcNow);
            return; // Ignore bar outside session window
        }

        // Track acceptance statistics
        if (!_barRejectionStats.TryGetValue(instrument, out var acceptanceStats))
        {
            acceptanceStats = new BarRejectionStats();
            _barRejectionStats[instrument] = acceptanceStats;
        }
        acceptanceStats.TotalAccepted++;
        acceptanceStats.LastUpdateUtc = utcNow;

        // Bar date matches - log acceptance (rate-limited to avoid spam)
        var shouldLogAcceptance = !_lastBarHeartbeatPerInstrument.TryGetValue(instrument, out var lastAcceptance) ||
                                 (utcNow - lastAcceptance).TotalMinutes >= BAR_HEARTBEAT_RATE_LIMIT_MINUTES;
        if (shouldLogAcceptance)
        {
            _lastBarHeartbeatPerInstrument[instrument] = utcNow;
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "BAR_ACCEPTED", state: "ENGINE",
                new
                {
                    instrument = instrument,
                    execution_instrument_full_name = instrument, // Full contract name from NinjaTrader (e.g., "MES 03-26")
                    bar_timestamp_utc = barUtc.ToString("o"),
                    bar_timestamp_chicago = barChicagoTime.ToString("o"),
                    bar_trading_date = barChicagoDate.ToString("yyyy-MM-dd"),
                    locked_trading_date = _activeTradingDate.Value.ToString("yyyy-MM-dd"),
                    note = "Bar accepted - date matches locked trading date"
                }));
        }

        // Log rejection summary if threshold exceeded (rate-limited)
        LogBarRejectionSummaryIfNeeded(utcNow);

        // Only process bars if streams exist (they should exist after EnsureStreamsCreated)
        if (_streams.Count == 0)
        {
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "BAR_RECEIVED_NO_STREAMS", state: "ENGINE",
                new
                {
                    instrument = instrument,
                    execution_instrument_full_name = instrument, // Full contract name from NinjaTrader (e.g., "MES 03-26")
                    bar_timestamp_utc = barUtc.ToString("o"),
                    note = "Bar received but streams not yet created - this should not happen"
                }));
            return;
        }

        // PHASE 3: Assert bar instrument is execution (from NT)
        // Note: Bar routing uses canonical matching, but input must be execution instrument
        // This assertion ensures we're not accidentally passing canonical instrument to OnBar

        // 🔴 Diagnostic Point #2: Bar routing summary (canonical mapping verification)
        var rawInstrument = instrument;
        var canonicalInstrument = _spec != null ? GetCanonicalInstrument(instrument) : instrument;
        var streamsReceivingBar = new List<string>();
        var streamsFilteredOut = new List<string>();
        var streamsChecked = 0;

        foreach (var s in _streams.Values)
        {
            streamsChecked++;
            // PHASE 3: IsSameInstrument receives execution instrument, compares canonical internally
            if (s.IsSameInstrument(instrument))
            {
                streamsReceivingBar.Add($"{s.Session}_{s.SlotTimeChicago}");
                s.OnBar(barUtc, open, high, low, close, utcNow);

                // Log bar delivery to stream (rate-limited, diagnostic only)
                if (_loggingConfig.DiagnosticsEnabled)
                {
                    var deliveryKey = $"bar_delivery_{instrument}_{s.Session}_{s.SlotTimeChicago}";
                    var shouldLogDelivery = !_lastBarDeliveryLogUtc.TryGetValue(deliveryKey, out var lastDelivery) ||
                                          (utcNow - lastDelivery).TotalMinutes >= 5.0;
                    if (shouldLogDelivery)
                    {
                        _lastBarDeliveryLogUtc[deliveryKey] = utcNow;
                        LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "BAR_DELIVERY_TO_STREAM", state: "ENGINE",
                            new
                            {
                                instrument = instrument,
                                stream = $"{s.Session}_{s.SlotTimeChicago}",
                                bar_timestamp_utc = barUtc.ToString("o"),
                                bar_timestamp_chicago = barChicagoTime.ToString("o"),
                                note = "Bar delivered to stream"
                            }));
                    }
                }
            }
            else
            {
                streamsFilteredOut.Add($"{s.Session}_{s.SlotTimeChicago}");
            }
        }

        // 🔴 Diagnostic Point #2: Log bar routing summary (rate-limited: once per 5 min per instrument)
        if (_loggingConfig.DiagnosticsEnabled)
        {
            var shouldLogRouting = !_lastBarRoutingDiagnosticUtc.TryGetValue(instrument, out var lastRouting) ||
                                   (utcNow - lastRouting).TotalMinutes >= 5.0;
            if (shouldLogRouting)
            {
                _lastBarRoutingDiagnosticUtc[instrument] = utcNow;
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "BAR_ROUTING_DIAGNOSTIC", state: "ENGINE",
                    new
                    {
                        raw_instrument = rawInstrument,
                        canonical_instrument = canonicalInstrument,
                        streams_checked = streamsChecked,
                        streams_matched = streamsReceivingBar.Count,
                        streams_receiving_bar = streamsReceivingBar,
                        note = "Diagnostic Point #2: Bar routing summary - shows canonical mapping and stream matching"
                    }));
            }
        }

        // Log bar delivery summary periodically (rate-limited)
        LogBarDeliverySummaryIfNeeded(utcNow, instrument, streamsReceivingBar, streamsFilteredOut);

            }
            finally
            {
                if (cpuBarWatch != null)
                {
                    Interlocked.Add(ref _cpuProfileOnBarLockMsAccum, cpuBarWatch.ElapsedMilliseconds);
                    Interlocked.Increment(ref _cpuProfileOnBarCount);
                }
            }
        } // Close lock (_engineLock)
    }

    /// <summary>
    /// Log bar rejection summary if interval has elapsed (rate-limited to every 5 minutes).
    /// </summary>
    private void LogBarRejectionSummaryIfNeeded(DateTimeOffset utcNow)
    {
        if (_lastBarRejectionSummaryUtc.HasValue &&
            (utcNow - _lastBarRejectionSummaryUtc.Value).TotalMinutes < BAR_REJECTION_SUMMARY_INTERVAL_MINUTES)
        {
            return; // Too soon to log summary
        }

        _lastBarRejectionSummaryUtc = utcNow;

        // Build summary for each instrument
        var summary = new Dictionary<string, object>();
        foreach (var kvp in _barRejectionStats)
        {
            var instrument = kvp.Key;
            var stats = kvp.Value;
            var totalRejected = stats.PartialRejected + stats.DateMismatch + stats.BeforeDateLocked;
            var totalProcessed = totalRejected + stats.TotalAccepted;
            var rejectionRate = totalProcessed > 0 ? (double)totalRejected / totalProcessed * 100.0 : 0.0;

            summary[instrument] = new
            {
                total_accepted = stats.TotalAccepted,
                partial_rejected = stats.PartialRejected,
                date_mismatch = stats.DateMismatch,
                before_date_locked = stats.BeforeDateLocked,
                total_rejected = totalRejected,
                total_processed = totalProcessed,
                rejection_rate_percent = Math.Round(rejectionRate, 2),
                last_update_utc = stats.LastUpdateUtc.ToString("o")
            };

            // Log warning if rejection rate exceeds threshold
            if (rejectionRate > 50.0 && totalProcessed >= 10)
            {
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "BAR_REJECTION_RATE_HIGH", state: "ENGINE",
                    new
                    {
                        instrument = instrument,
                        rejection_rate_percent = Math.Round(rejectionRate, 2),
                        total_processed = totalProcessed,
                        total_rejected = totalRejected,
                        partial_rejected = stats.PartialRejected,
                        date_mismatch = stats.DateMismatch,
                        before_date_locked = stats.BeforeDateLocked,
                        note = $"High rejection rate detected - {rejectionRate:F1}% of bars rejected. Check timezone conversion and date alignment."
                    }));
            }
        }

        if (summary.Count > 0)
        {
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "BAR_REJECTION_SUMMARY", state: "ENGINE",
                new
                {
                    summary = summary,
                    note = $"Bar rejection statistics (last {BAR_REJECTION_SUMMARY_INTERVAL_MINUTES} minutes)"
                }));
        }
    }

    private bool ShouldLogBarDateMismatchDetail(string instrument, string rejectionReason, DateTimeOffset utcNow)
    {
        var key = $"{instrument?.Trim().ToUpperInvariant()}|{rejectionReason}";
        if (!_lastBarDateMismatchDetailLogUtc.TryGetValue(key, out var last) ||
            (utcNow - last).TotalMinutes >= BarDateMismatchDetailLogIntervalMinutes)
        {
            _lastBarDateMismatchDetailLogUtc[key] = utcNow;
            return true;
        }

        return false;
    }

    // Rate-limiting for bar delivery logging (per stream)
    private readonly Dictionary<string, DateTimeOffset> _lastBarDeliveryLogUtc = new Dictionary<string, DateTimeOffset>();
    private readonly Dictionary<string, DateTimeOffset> _lastBarRoutingDiagnosticUtc = new Dictionary<string, DateTimeOffset>();
    private DateTimeOffset? _lastBarDeliverySummaryUtc = null;

    // Bar acceptance heartbeat tracking (rate-limited)
    private readonly Dictionary<string, DateTimeOffset> _lastBarHeartbeatPerInstrument = new Dictionary<string, DateTimeOffset>();
    private const int BAR_HEARTBEAT_RATE_LIMIT_MINUTES = 5; // Log bar acceptance heartbeat every 5 minutes per instrument
    private const double BAR_DELIVERY_SUMMARY_INTERVAL_MINUTES = 5.0;

    /// <summary>
    /// Log bar delivery summary if interval has elapsed (rate-limited to every 5 minutes).
    /// </summary>
    private void LogBarDeliverySummaryIfNeeded(DateTimeOffset utcNow, string instrument, List<string> streamsReceivingBar, List<string> streamsFilteredOut)
    {
        if (!_loggingConfig.DiagnosticsEnabled) return;

        if (_lastBarDeliverySummaryUtc.HasValue &&
            (utcNow - _lastBarDeliverySummaryUtc.Value).TotalMinutes < BAR_DELIVERY_SUMMARY_INTERVAL_MINUTES)
        {
            return; // Too soon to log summary
        }

        _lastBarDeliverySummaryUtc = utcNow;

        LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "BAR_DELIVERY_SUMMARY", state: "ENGINE",
            new
            {
                instrument = instrument,
                streams_receiving_bars = streamsReceivingBar,
                streams_filtered_out = streamsFilteredOut,
                total_streams_receiving = streamsReceivingBar.Count,
                total_streams_filtered = streamsFilteredOut.Count,
                note = $"Bar delivery distribution across streams (last {BAR_DELIVERY_SUMMARY_INTERVAL_MINUTES} minutes)"
            }));
    }


    /// <summary>
    /// Load pre-hydration bars for SIM mode from NinjaTrader BarsRequest API.
    /// This method allows the NinjaTrader strategy to request historical bars and feed them to streams.
    /// Streams must exist before calling this method (created in Start()).
    /// Filters out bars that are in the future relative to current time to avoid duplicates with live bars.
    /// See docs/PRE_HYDRATION_DEEP_DIVE.md for flow, Operator Decision Tree, and troubleshooting.
    /// </summary>
    public void LoadPreHydrationBars(string instrument, List<Bar> bars, DateTimeOffset utcNow)
    {
        lock (_engineLock)
        {
            // BINARY TRUTH EVENT: Prove LoadPreHydrationBars is called and show matching results
            // Count matching streams before any early returns or filtering
            var matchingStreams = new List<StreamStateMachine>();
            var enabledStreamsTotal = 0;
            string? canonicalOfInstrument = null;

            if (_spec != null && _time != null && bars != null && bars.Count > 0 && _streams.Count > 0)
            {
                canonicalOfInstrument = GetCanonicalInstrument(instrument);
                enabledStreamsTotal = _streams.Values.Count(s => !s.Committed);

                // Count streams that match this instrument via IsSameInstrument
                foreach (var stream in _streams.Values)
                {
                    if (stream.IsSameInstrument(instrument))
                    {
                        matchingStreams.Add(stream);
                    }
                }
            }

            // Emit LOADPREHYDRATIONBARS_ENTERED event (always, even if early return)
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "LOADPREHYDRATIONBARS_ENTERED", state: "ENGINE",
                new
                {
                    instrumentName = instrument,
                    bars_count_input = bars?.Count ?? 0,
                    streams_matched_count = matchingStreams.Count,
                    enabled_streams_total = enabledStreamsTotal,
                    canonical_of_instrument = canonicalOfInstrument ?? "N/A"
                }));

            if (_spec is null || _time is null) return;
            if (bars == null || bars.Count == 0) return;

        // Ensure streams exist (they should be created in Start())
        if (_streams.Count == 0)
        {
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "PRE_HYDRATION_BARS_SKIPPED", state: "ENGINE",
                new
                {
                    instrument = instrument,
                    bar_count = bars.Count,
                    reason = "Streams not yet created",
                    note = "Bars will be buffered when streams are created or via OnBar()"
                }));
            return;
        }

        // Race hardening: BarsRequest may complete on a thread pool worker before DataLoaded finishes
        // WireNTContextToAdapter(). Do not feed pre-hydration bars into streams until SIM verify + NT context exist.
        if (_executionAdapter != null && !_executionAdapter.IsExecutionContextReady)
        {
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "PRE_HYDRATION_DEFERRED_EXECUTION_CONTEXT_NOT_READY", state: "ENGINE",
                new
                {
                    instrument = instrument,
                    bar_count = bars?.Count ?? 0,
                    note = "LoadPreHydrationBars skipped — execution adapter not ready (SetNTContext/SIM verify). Bars will arrive via OnBar when strategy is ready."
                }));
            return;
        }

        // CRITICAL: Filter out bars that are in the future or partial/in-progress
        // This prevents duplicate bars when live bars arrive later
        // BarsRequest might return bars up to slotTimeChicago, but we only want bars up to "now"
        // Also ensure bars are fully closed (at least 1 minute old)
        //
        // Partial-bar contamination problem:
        // - BarsRequest returns fully closed bars (good, but we verify)
        // - If you start mid-minute, BarsRequest gives completed prior bar
        // - Live feed may later emit a bar that partially overlaps expectations
        // - What breaks: Off-by-one minute range errors, incomplete data
        //
        // Rule: Only accept bars that are at least 1 minute old (bar period)
        // This ensures the bar is fully closed before we use it
        var filteredBars = new List<Bar>();
        var barsFilteredFuture = 0;
        var barsFilteredPartial = 0;

        foreach (var bar in bars)
        {
            // Filter 1: Reject future bars
            if (bar.TimestampUtc > utcNow)
            {
                barsFilteredFuture++;
                continue;
            }

                // Filter 2: Reject bars that are too recent (less than 0.1 minutes old)
            // Note: BarsRequest should return historical bars that are old enough, but we add a small buffer
            // to handle edge cases where BarsRequest might return very recent bars
            var barAgeMinutes = (utcNow - bar.TimestampUtc).TotalMinutes;
            const double MIN_BARSREQUEST_BAR_AGE_MINUTES = 0.1; // Small buffer for BarsRequest bars
            if (barAgeMinutes < MIN_BARSREQUEST_BAR_AGE_MINUTES)
            {
                barsFilteredPartial++;
                continue;
            }

            // Bar passed all filters - add to filtered list
            filteredBars.Add(bar);
        }

        var totalFiltered = barsFilteredFuture + barsFilteredPartial;

        // Log filtering summary (always log, even if no filtering occurred)
        LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString,
            eventType: "BARSREQUEST_FILTER_SUMMARY", state: "ENGINE",
            new
            {
                instrument = instrument,
                raw_bar_count = bars.Count,
                accepted_bar_count = filteredBars.Count,
                filtered_future_count = barsFilteredFuture,
                filtered_partial_count = barsFilteredPartial,
                accepted_first_bar_utc = filteredBars.Count > 0 ? filteredBars[0].TimestampUtc.ToString("o") : null,
                accepted_last_bar_utc = filteredBars.Count > 0 ? filteredBars[filteredBars.Count - 1].TimestampUtc.ToString("o") : null
            }));

        if (totalFiltered > 0)
        {
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "PRE_HYDRATION_BARS_FILTERED", state: "ENGINE",
                new
                {
                    instrument = instrument,
                    total_bars = bars.Count,
                    filtered_future = barsFilteredFuture,
                    filtered_partial = barsFilteredPartial,
                    total_filtered = totalFiltered,
                    bars_loaded = filteredBars.Count,
                    current_time_utc = utcNow.ToString("o"),
                    min_bar_age_minutes = 0.1, // Small buffer for BarsRequest bars
                    note = "Filtered out future bars and very recent bars (< 0.1 min old). Only fully closed bars accepted."
                }));
        }

        if (filteredBars.Count == 0)
        {
            // Get current Chicago time for diagnostic
            var nowChicago = _time?.ConvertUtcToChicago(utcNow) ?? utcNow;

            // Log zero-bars diagnostic with actionable suggestions
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString,
                eventType: "BARSREQUEST_ZERO_BARS_DIAGNOSTIC", state: "ENGINE",
                new
                {
                    instrument = instrument,
                    trading_date = TradingDateString,
                    requested_start_chicago = bars.Count > 0 ? "N/A" : "See BARSREQUEST_REQUESTED log",
                    requested_end_chicago = bars.Count > 0 ? "N/A" : "See BARSREQUEST_REQUESTED log",
                    now_chicago = nowChicago.ToString("o"),
                    trading_hours_template = "See BARSREQUEST_REQUESTED log",
                    execution_mode = "SIM",
                    raw_bar_count = bars.Count,
                    filtered_future_count = barsFilteredFuture,
                    filtered_partial_count = barsFilteredPartial,
                    suggested_checks = new[]
                    {
                        "Check NinjaTrader 'Days to load' setting",
                        "Verify instrument has historical data",
                        "Confirm trading hours template",
                        "Confirm data provider connection"
                    }
                }));

            // All bars filtered out - this is unusual and should be logged as warning
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "PRE_HYDRATION_NO_BARS_AFTER_FILTER", state: "ENGINE",
                new
                {
                    instrument = instrument,
                    total_bars = bars.Count,
                    filtered_future = barsFilteredFuture,
                    filtered_partial = barsFilteredPartial,
                    total_filtered = totalFiltered,
                    current_time_utc = utcNow.ToString("o"),
                    note = "All bars were filtered out (all were future or partial/in-progress). " +
                           "This may indicate timing issues or data feed problems. " +
                           "Range computation will rely on live bars only - may be incomplete."
                }));
            // Don't return - allow degraded operation, but make it visible
            // Streams will start without historical bars
        }

        // CRITICAL: Mark BarsRequest as completed BEFORE feeding bars
        // This ensures streams can transition immediately when bars arrive
        // If we mark after feeding, streams check IsBarsRequestPending() during feeding and still see pending
        // Each OnBar() call triggers Tick() which calls HandlePreHydrationState(), so we need to mark completed first
        MarkBarsRequestCompleted(instrument, utcNow);

        // Feed filtered bars to matching streams
        var streamsFed = 0;
        foreach (var stream in _streams.Values)
        {
            if (stream.IsSameInstrument(instrument))
            {
                // CRITICAL: Verify stream is ready to receive bars
                // Streams should exist and be in a state that buffers bars
                // PRE_HYDRATION, ARMED, and RANGE_BUILDING all buffer bars
                if (stream.State != StreamState.PRE_HYDRATION &&
                    stream.State != StreamState.ARMED &&
                    stream.State != StreamState.RANGE_BUILDING)
                {
                    var nowChicago = _time?.ConvertUtcToChicago(utcNow) ?? utcNow;

                    LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "PRE_HYDRATION_BARS_SKIPPED_STREAM_STATE", state: "ENGINE",
                        new
                        {
                            instrument = instrument,
                            stream_id = stream.Stream,
                            trading_date = TradingDateString,
                            stream_state = stream.State.ToString(),
                            bar_count = filteredBars.Count,
                            current_time_chicago = nowChicago.ToString("o"),
                            rejection_reason = $"STREAM_STATE_{stream.State}",
                            note = $"Stream is in {stream.State} state - bars will not be buffered. " +
                                   "This may indicate a timing issue or stream already progressed past pre-hydration."
                        }));

                    // HIGH-SIGNAL WARNING: Bars skipped during active range-building indicates state machine issue
                    if (stream.State == StreamState.RANGE_LOCKED || stream.State == StreamState.RANGE_LOCKED || stream.State == StreamState.DONE)
                    {
                        LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "PRE_HYDRATION_BARS_SKIPPED_ACTIVE_STREAM", state: "ENGINE",
                            new
                            {
                                instrument = instrument,
                                stream_id = stream.Stream,
                                stream_state = stream.State.ToString(),
                                warning = "Pre-hydration bars skipped for stream in active state - may indicate state machine issue",
                                note = "Stream has progressed beyond pre-hydration state. Bars should be received via OnBar() instead."
                            }));
                    }

                    continue; // Skip this stream
                }

                // Feed bars directly to stream's buffer for pre-hydration
                // Mark as historical bars (from BarsRequest)
                // NOTE: BarsRequest already marked as completed above, so streams can transition immediately
                foreach (var bar in filteredBars)
                {
                    stream.OnBar(bar.TimestampUtc, bar.Open, bar.High, bar.Low, bar.Close, utcNow, isHistorical: true);
                }
                streamsFed++;
            }
        }

            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "PRE_HYDRATION_BARS_LOADED", state: "ENGINE",
                new
                {
                    instrument = instrument,
                    bar_count = filteredBars.Count,
                    filtered_future = barsFilteredFuture,
                    filtered_partial = barsFilteredPartial,
                    total_filtered = totalFiltered,
                    streams_fed = streamsFed,
                    first_bar_utc = filteredBars.Count > 0 ? filteredBars[0].TimestampUtc.ToString("o") : null,
                    last_bar_utc = filteredBars.Count > 0 ? filteredBars[filteredBars.Count - 1].TimestampUtc.ToString("o") : null,
                    source = "NinjaTrader_BarsRequest",
                    note = "Only fully closed bars loaded (filtered future and partial bars)"
                }));
        }
    }

    /// <summary>
    /// Get time range covering all enabled streams for an instrument (for BarsRequest).
    /// Returns the earliest range_start and latest slot_time across all enabled streams for the instrument.
    /// </summary>
    /// <summary>
    /// Check if streams are ready for an instrument (exist and have at least one enabled stream).
    /// Used to gate BarsRequest on stream readiness - deterministic, no retries needed.
    /// </summary>
    public bool AreStreamsReadyForInstrument(string instrument)
    {
        lock (_engineLock)
        {
            if (_spec is null || _time is null || !_activeTradingDate.HasValue)
            {
                // Diagnostic logging for readiness check failure
                LogEvent(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: TradingDateString, eventType: "ARESTREAMSREADY_DECISION", state: "ENGINE",
                    new
                    {
                        execution_instrument = instrument,
                        canonical_of_execution = GetCanonicalInstrument(instrument),
                        result = false,
                        reasons = new[] { "SPEC_NULL_OR_TIME_NULL_OR_NO_TRADING_DATE" }
                    }));
                return false;
            }

            // FIXED CONTRACT: Use canonical-matched streams for BarsRequest readiness
            // Get canonical of execution instrument (e.g., MCL → CL)
            var canonicalOfExecution = GetCanonicalInstrument(instrument);

            // Match streams where stream.CanonicalInstrument equals canonical of execution instrument
            // This ensures AreStreamsReadyForInstrument("MCL") matches CL streams
            var allStreams = _streams.Values.ToList();
            var allStreamsForInstrument = allStreams
                .Where(s => string.Equals(s.CanonicalInstrument, canonicalOfExecution, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var matchedStreamIds = allStreamsForInstrument.Select(s => s.Stream).ToList();

            if (allStreamsForInstrument.Count == 0)
            {
                // Diagnostic logging for no matched streams
                var reasons = new List<string> { "NO_MATCHED_STREAMS" };
                if (allStreams.Count == 0) reasons.Add("NO_STREAMS_EXIST");
                else if (allStreams.All(s => s.Committed)) reasons.Add("ALL_STREAMS_COMMITTED");

                LogEvent(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: TradingDateString, eventType: "ARESTREAMSREADY_DECISION", state: "ENGINE",
                    new
                    {
                        execution_instrument = instrument,
                        canonical_of_execution = canonicalOfExecution,
                        enabled_streams_considered = allStreams.Count,
                        matched_streams_count = 0,
                        matched_stream_ids = new string[0],
                        result = false,
                        reasons = reasons.ToArray()
                    }));
                return false;
            }

            // Check if at least one stream is enabled (not committed)
            var enabledStreams = allStreamsForInstrument
                .Where(s => !s.Committed)
                .ToList();

            var result = enabledStreams.Count > 0;

            // Diagnostic logging for readiness decision
            var resultReasons = new List<string>();
            if (!result)
            {
                resultReasons.Add("ALL_MATCHED_STREAMS_COMMITTED");
            }

            LogEvent(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: TradingDateString, eventType: "ARESTREAMSREADY_DECISION", state: "ENGINE",
                new
                {
                    execution_instrument = instrument,
                    canonical_of_execution = canonicalOfExecution,
                    enabled_streams_considered = allStreams.Count,
                    matched_streams_count = allStreamsForInstrument.Count,
                    matched_stream_ids = matchedStreamIds,
                    result = result,
                    reasons = resultReasons.ToArray()
                }));

            return result;
        }
    }

    /// <summary>
    /// Get all unique execution instruments from enabled streams.
    /// Used to determine which instruments need BarsRequest for pre-hydration.
    /// CRITICAL: Maps base instruments (YM, CL) to micro futures (MYM, MCL) for BarsRequest.
    /// </summary>
    public List<string> GetAllExecutionInstrumentsForBarsRequest()
    {
        lock (_engineLock)
        {
            if (_spec is null || _time is null || !_activeTradingDate.HasValue) return new List<string>();

            // Get all enabled streams (not committed)
            var enabledStreams = _streams.Values
                .Where(s => !s.Committed)
                .ToList();

            // Extract unique execution instruments from streams
            var executionInstruments = enabledStreams
                .Where(s => !string.IsNullOrEmpty(s.ExecutionInstrument))
                .Select(s => s.ExecutionInstrument.ToUpperInvariant())
                .Distinct()
                .ToList();

            // CRITICAL: Map base instruments to micro futures for BarsRequest
            // ExecutionInstrument is already the execution instrument (e.g., MGC, MYM, MCL)
            // If ExecutionInstrument is a base instrument (e.g., YM, CL), map to micro (MYM, MCL)
            // Otherwise, use as-is (already micro or no mapping available)
            var barsRequestInstruments = new List<string>();
            foreach (var execInst in executionInstruments)
            {
                // Check if this is already a micro future
                var microFuture = GetMicroFutureForBaseInstrument(execInst);
                if (!string.IsNullOrEmpty(microFuture))
                {
                    barsRequestInstruments.Add(microFuture);
                }
                else
                {
                    // Not a base instrument that maps to micro, use as-is (e.g., already a micro or no mapping)
                    barsRequestInstruments.Add(execInst);
                }
            }

            return barsRequestInstruments
                .Distinct()
                .OrderBy(i => i)
                .ToList();
        }
    }

    /// <summary>
    /// Maps base instruments to their micro future equivalents for BarsRequest.
    /// Returns micro future if available, otherwise returns null.
    /// </summary>
    private string? GetMicroFutureForBaseInstrument(string baseInstrument)
    {
        if (_spec?.instruments == null) return null;

        var baseInstUpper = baseInstrument.ToUpperInvariant();

        // Look for a micro future that maps to this base instrument
        foreach (var kvp in _spec.instruments)
        {
            var inst = kvp.Value;
            if (inst.is_micro &&
                !string.IsNullOrWhiteSpace(inst.base_instrument) &&
                inst.base_instrument.ToUpperInvariant() == baseInstUpper)
            {
                return kvp.Key.ToUpperInvariant(); // Return micro future name (e.g., MYM, MCL)
            }
        }

        return null; // No micro future found for this base instrument
    }

    /// <summary>
    /// Check if BarsRequest should be called for restart for any streams.
    /// Returns list of instruments that need BarsRequest due to restart.
    /// Strategy should call this after Realtime state is reached and call RequestHistoricalBarsForPreHydration for each instrument.
    /// </summary>
    public List<string> GetInstrumentsNeedingRestartBarsRequest()
    {
        lock (_engineLock)
        {
            var instrumentsNeedingBarsRequest = new HashSet<string>();
            var utcNow = DateTimeOffset.UtcNow;

            // Check all streams for restart state
            // Streams in PRE_HYDRATION or ARMED state that are not committed may need BarsRequest
            // This handles the case where streams restart mid-session and need historical bars
            foreach (var stream in _streams.Values)
            {
                // Check if stream is in PRE_HYDRATION, ARMED, or RANGE_BUILDING and not committed
                // RANGE_BUILDING: may need more bars to lock; PRE_HYDRATION/ARMED: restore-fallback or slot not expired
                if ((stream.State == StreamState.PRE_HYDRATION || stream.State == StreamState.ARMED || stream.State == StreamState.RANGE_BUILDING) &&
                    !stream.Committed)
                {
                    var canonicalInstrument = stream.CanonicalInstrument;
                    if (!string.IsNullOrEmpty(canonicalInstrument))
                    {
                        instrumentsNeedingBarsRequest.Add(canonicalInstrument);

                        LogEngineEvent(utcNow, "RESTART_BARSREQUEST_DETECTED", new
                        {
                            instrument = canonicalInstrument,
                            stream_id = stream.Stream,
                            state = stream.State.ToString(),
                            committed = stream.Committed,
                            note = "Stream detected in PRE_HYDRATION/ARMED state - BarsRequest may be needed for restart"
                        });
                    }
                }
            }

            if (instrumentsNeedingBarsRequest.Count > 0)
            {
                LogEngineEvent(utcNow, "RESTART_BARSREQUEST_SUMMARY", new
                {
                    instruments_count = instrumentsNeedingBarsRequest.Count,
                    instruments = instrumentsNeedingBarsRequest.ToList(),
                    note = "Instruments detected that may need BarsRequest for restart"
                });
            }

            return instrumentsNeedingBarsRequest.ToList();
        }
    }

    public (string earliestRangeStart, string latestSlotTime)? GetBarsRequestTimeRange(
        string instrument,
        bool preferEarliestSlot = false,
        bool logDiagnostics = true)
    {
        lock (_engineLock)
        {
            if (_spec is null || _time is null || !_activeTradingDate.HasValue) return null;

            var instrumentUpper = instrument.ToUpperInvariant();
            var utcNow = DateTimeOffset.UtcNow;

            // PHASE 3: Use IsSameInstrument() to handle canonical mapping (e.g., MNQ → NQ)
            // Log all streams for this instrument for diagnostics
            var allStreamsForInstrument = _streams.Values
                .Where(s => s.IsSameInstrument(instrument))
                .ToList();

            if (allStreamsForInstrument.Count == 0)
            {
                if (logDiagnostics)
                {
                    LogEngineEvent(utcNow, "BARSREQUEST_RANGE_CHECK", new
                    {
                        instrument = instrumentUpper,
                        result = "NO_STREAMS_FOUND",
                        total_streams_in_engine = _streams.Count,
                        note = "No streams found for this instrument. Check timetable configuration."
                    });
                }
                return null;
            }

            // Log stream details for diagnostics
            var streamDetails = allStreamsForInstrument.Select(s => new
            {
                stream_id = s.Stream,
                session = s.Session,
                instrument = s.Instrument,
                slot_time = s.SlotTimeChicago,
                committed = s.Committed,
                state = s.State.ToString()
            }).ToList();

            if (logDiagnostics)
            {
                LogEngineEvent(utcNow, "BARSREQUEST_STREAM_STATUS", new
                {
                    instrument = instrumentUpper,
                    total_streams = allStreamsForInstrument.Count,
                    streams = streamDetails
                });
            }

            var enabledStreams = allStreamsForInstrument
                .Where(s => !s.Committed)
                .ToList();

            if (enabledStreams.Count == 0)
            {
                if (logDiagnostics)
                {
                    LogEngineEvent(utcNow, "BARSREQUEST_RANGE_CHECK", new
                    {
                        instrument = instrumentUpper,
                        result = "ALL_STREAMS_COMMITTED",
                        total_streams = allStreamsForInstrument.Count,
                        note = "All streams are committed - no active streams for BarsRequest"
                    });
                }
                return null;
            }

            // CRITICAL FIX: Find the union of all [range_start, slot_time) windows for all enabled streams
            // Previous logic incorrectly mixed earliest range_start with latest slot_time from different sessions
            // This caused bars to be requested for wrong time windows (e.g., 02:00-09:30 instead of 08:00-11:00)

            var sessionsUsed = enabledStreams.Select(s => s.Session).Distinct().ToList();
            var sessionRangeStarts = new Dictionary<string, string>();
            var streamWindows = new List<(string rangeStart, string slotTime, string streamId, string session)>();

            // Build list of [range_start, slot_time) windows for each enabled stream
            foreach (var stream in enabledStreams)
            {
                if (_spec.sessions.TryGetValue(stream.Session, out var sessionInfo))
                {
                    var rangeStart = sessionInfo.range_start_time;
                    var slotTime = stream.SlotTimeChicago;

                    if (!string.IsNullOrWhiteSpace(rangeStart) && !string.IsNullOrWhiteSpace(slotTime))
                    {
                        sessionRangeStarts[stream.Session] = rangeStart;
                        streamWindows.Add((rangeStart, slotTime, stream.Stream, stream.Session));
                    }
                }
            }

            if (streamWindows.Count == 0)
            {
                if (logDiagnostics)
                {
                    LogEngineEvent(utcNow, "BARSREQUEST_RANGE_CHECK", new
                    {
                        instrument = instrumentUpper,
                        result = "NO_VALID_WINDOWS_FOUND",
                        enabled_streams = enabledStreams.Count,
                        sessions_used = sessionsUsed,
                        note = "No valid [range_start, slot_time) windows found for enabled streams"
                    });
                }
                return null;
            }

            // Find the union: earliest range_start and latest slot_time across all windows
            // This ensures we request bars covering all streams' needs
            var earliestRangeStart = streamWindows
                .Select(w => w.rangeStart)
                .OrderBy(rs => rs, StringComparer.Ordinal)
                .First();

            var selectedSlotTime = preferEarliestSlot
                ? streamWindows
                    .Select(w => w.slotTime)
                    .OrderBy(st => st, StringComparer.Ordinal)
                    .First()
                : streamWindows
                    .Select(w => w.slotTime)
                    .OrderByDescending(st => st, StringComparer.Ordinal)
                    .First();

            // Log successful range determination with stream details
            if (logDiagnostics)
            {
                LogEngineEvent(utcNow, "BARSREQUEST_RANGE_DETERMINED", new
                {
                    instrument = instrumentUpper,
                    earliest_range_start = earliestRangeStart,
                    latest_slot_time = selectedSlotTime,
                    slot_selection = preferEarliestSlot ? "EARLIEST_ACTIVE_SLOT" : "LATEST_ACTIVE_SLOT",
                    enabled_stream_count = enabledStreams.Count,
                    sessions_used = sessionsUsed,
                    session_range_starts = sessionRangeStarts,
                    stream_windows = streamWindows.Select(w => new {
                        stream_id = w.streamId,
                        session = w.session,
                        range_start = w.rangeStart,
                        slot_time = w.slotTime
                    }).ToList(),
                    note = preferEarliestSlot
                        ? "Earliest active slot selected for due scheduling; prevents later sibling slot from delaying first-slot hydration."
                        : "Union of all stream windows - ensures bars cover all streams' needs."
                });
            }

            return (earliestRangeStart, selectedSlotTime);
        }
    }
}
