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
    /// Add a bar to the buffer (thread-safe) with centralized deduplication precedence.
    /// 
    /// PRECEDENCE LADDER (formalized):
    /// LIVE > BARSREQUEST > CSV
    /// 
    /// This ensures deterministic behavior when multiple sources provide the same bar:
    /// - Live bars always win (most current, vendor corrections)
    /// - BarsRequest bars win over CSV (more authoritative historical source)
    /// - CSV bars are lowest priority (fallback/supplement)
    /// 
    /// All deduplication logic is centralized here - call sites only need to specify source.
    /// </summary>
    /// <param name="bar">Bar to add</param>
    /// <param name="source">Bar source (LIVE, BARSREQUEST, or CSV)</param>
    private void AddBarToBuffer(Bar bar, BarSource source)
    {
        // RANGE_BUILDING restore deduplication: skip bars already in snapshot
        if (_restoredFromRangeBuildingSnapshot && _lastProcessedBarTimeUtc.HasValue &&
            bar.TimestampUtc <= _lastProcessedBarTimeUtc.Value)
        {
            return; // Bar already captured in restored snapshot - do not double-count
        }

        lock (_barBufferLock)
        {
            var utcNow = DateTimeOffset.UtcNow;
            
            // Log buffer add attempt
            _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                "BAR_BUFFER_ADD_ATTEMPT", State.ToString(),
                new
                {
                    stream_id = Stream,
                    canonical_instrument = CanonicalInstrument,
                    instrument = Instrument,
                    bar_timestamp_utc = bar.TimestampUtc.ToString("o"),
                    bar_timestamp_chicago = _time.ConvertUtcToChicago(bar.TimestampUtc).ToString("o"),
                    bar_source = source.ToString(),
                    current_buffer_count = _barBuffer.Count,
                    range_start_chicago = RangeStartChicagoTime.ToString("o"),
                    slot_time_chicago = SlotTimeChicagoTime.ToString("o")
                }));
            
            var barAgeMinutes = (utcNow - bar.TimestampUtc).TotalMinutes;
            const double MIN_BAR_AGE_MINUTES = 1.0; // Bar period (1 minute bars)
            
            // Liveness Fix: Bypass age-based rejection for LIVE bars (live feed)
            // BarSource.LIVE means "live feed", not "OnBarClose completeness"
            // Live feeds can deliver bars that appear "young" relative to engine clock
            // (reconnects, scheduler delays, timestamp conventions)
            // Closedness is enforced by NinjaTrader configuration (Calculate = OnBarClose),
            // not by BarSource.LIVE. We no longer enforce age completeness for LIVE;
            // closedness is enforced by NT configuration.
            if (source != BarSource.LIVE && barAgeMinutes < MIN_BAR_AGE_MINUTES)
            {
                // Bar is too recent - likely partial/in-progress, reject it
                // (Only for BARSREQUEST and CSV sources - historical bars may be partial)
                var nowChicago = _time.ConvertUtcToChicago(utcNow);
                var barChicagoTime = _time.ConvertUtcToChicago(bar.TimestampUtc);
                
                if (_enableDiagnosticLogs)
                {
                    _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                        "BAR_PARTIAL_REJECTED_BUFFER", State.ToString(),
                        new
                        {
                            instrument = Instrument,
                            stream_id = Stream,
                            trading_date = TradingDate,
                            bar_timestamp_utc = bar.TimestampUtc.ToString("o"),
                            bar_timestamp_chicago = barChicagoTime.ToString("o"),
                            current_time_utc = utcNow.ToString("o"),
                            current_time_chicago = nowChicago.ToString("o"),
                            bar_age_minutes = barAgeMinutes,
                            min_bar_age_minutes = MIN_BAR_AGE_MINUTES,
                            buffer_count = _barBuffer.Count,
                            stream_state = State.ToString(),
                            rejection_reason = "PARTIAL_BAR",
                            bar_source = source.ToString(),
                            note = "Bar rejected at buffer insert - too recent, likely partial/in-progress bar. Only fully closed bars accepted."
                        }));
                }
                
                // HIGH-SIGNAL WARNING: Partial bar rejection during steady-state (ARMED/RANGE_BUILDING) indicates issue
                if (State == StreamState.ARMED || State == StreamState.RANGE_BUILDING)
                {
                    _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                        "BAR_PARTIAL_REJECTED_STEADY_STATE", State.ToString(),
                        new
                        {
                            instrument = Instrument,
                            stream_id = Stream,
                            stream_state = State.ToString(),
                            bar_source = source.ToString(),
                            warning = "Partial bar rejected during steady-state - may indicate OnBarClose timing issue",
                            note = "OnBarClose bars should be fully closed. Check bar source and timing."
                        }));
                }
                
                // Log buffer rejection
                _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                    "BAR_BUFFER_REJECTED", State.ToString(),
                    new
                    {
                        stream_id = Stream,
                        canonical_instrument = CanonicalInstrument,
                        instrument = Instrument,
                        bar_timestamp_utc = bar.TimestampUtc.ToString("o"),
                        bar_timestamp_chicago = barChicagoTime.ToString("o"),
                        bar_source = source.ToString(),
                        rejection_reason = "PARTIAL_BAR",
                        bar_age_minutes = barAgeMinutes,
                        min_bar_age_minutes = MIN_BAR_AGE_MINUTES,
                        current_buffer_count = _barBuffer.Count,
                        range_start_chicago = RangeStartChicagoTime.ToString("o"),
                        slot_time_chicago = SlotTimeChicagoTime.ToString("o")
                    }));
                
                return; // Reject partial bar
            }
            
            // For LIVE bars with suspicious age, log warning but accept (liveness guarantee)
            if (source == BarSource.LIVE && barAgeMinutes < MIN_BAR_AGE_MINUTES)
            {
                var nowChicago = _time.ConvertUtcToChicago(utcNow);
                var barChicagoTime = _time.ConvertUtcToChicago(bar.TimestampUtc);
                
                // Rate-limit warning to once per stream per 5 minutes
                var shouldLogWarning = !_lastLiveBarAgeWarningUtc.HasValue || 
                    (utcNow - _lastLiveBarAgeWarningUtc.Value).TotalMinutes >= 5.0;
                
                if (shouldLogWarning)
                {
                    _lastLiveBarAgeWarningUtc = utcNow;
                    _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                        "BAR_PARTIAL_WARNING_LIVE_FEED", State.ToString(),
                        new
                        {
                            instrument = Instrument,
                            stream_id = Stream,
                            trading_date = TradingDate,
                            bar_age_minutes = Math.Round(barAgeMinutes, 3),
                            bar_timestamp_utc = bar.TimestampUtc.ToString("o"),
                            bar_timestamp_chicago = barChicagoTime.ToString("o"),
                            current_time_utc = utcNow.ToString("o"),
                            current_time_chicago = nowChicago.ToString("o"),
                            note = "LIVE bar age < 1.0 minute - may indicate timebase issue. Bar accepted for liveness. Closedness enforced by NT OnBarClose configuration."
                        }));
                }
                // Continue to accept bar (liveness guarantee)
            }
            
            // CRITICAL: Deduplicate by (instrument, barStartUtc) with formalized precedence
            // This is REQUIRED in a hybrid system (BarsRequest + CSV + live bars).
            //
            // Boundary-minute ambiguity problem:
            // - Strategy starts at 07:25:00.200 CT
            // - BarsRequest includes 07:25 bar (barStartUtc = 07:25:00)
            // - CSV may also include 07:25 bar
            // - Live feed may emit the 07:25 bar again at 07:26:00 (when bar closes)
            // - Even with <= now filtering, this can happen
            //
            // Historical vs live OHLC mismatch problem:
            // - NinjaTrader historical bars and live bars may have different OHLC values
            // - May differ due to data vendor corrections
            // - Example: Historical high = 4952.25, Live high = 4952.50 for same minute
            // - What breaks: Range values differ depending on startup timing, non-reproducible results
            //
            // FORMALIZED PRECEDENCE LADDER: LIVE > BARSREQUEST > CSV
            // - Live bars always win (most current, vendor corrections)
            // - BarsRequest bars win over CSV (more authoritative historical source)
            // - CSV bars are lowest priority (fallback/supplement)
            //
            // Rule: (instrument, barStartUtc) must be unique in _barBuffer
            // Note: bar.TimestampUtc represents the bar's start time (e.g., 07:25:00 = bar from 07:25 to 07:26)
            
            // Check if a bar with this timestamp (barStartUtc) already exists
            var existingBarIndex = _barBuffer.FindIndex(b => b.TimestampUtc == bar.TimestampUtc);
            
            if (existingBarIndex >= 0)
            {
                // Bar with this barStartUtc already exists - check precedence
                var existingBar = _barBuffer[existingBarIndex];
                var existingSource = _barSourceMap.TryGetValue(bar.TimestampUtc, out var existingBarSource) ? existingBarSource : BarSource.CSV; // Default to CSV if not tracked
                
                // PRECEDENCE CHECK: Only replace if new source has higher precedence
                // LIVE (0) > BARSREQUEST (1) > CSV (2) - lower enum value = higher precedence
                if (source < existingSource)
                {
                    // New bar has higher precedence - replace existing bar
                    _barBuffer[existingBarIndex] = bar;
                    _barSourceMap[bar.TimestampUtc] = source;
                    
                    // Check if OHLC values differ (for logging)
                    var ohlcDiffers = existingBar.Open != bar.Open || 
                                     existingBar.High != bar.High || 
                                     existingBar.Low != bar.Low || 
                                     existingBar.Close != bar.Close;
                    
                    // Log replacement if OHLC differs or at diagnostic level
                    if (ohlcDiffers || _enableDiagnosticLogs)
                    {
                        _log.Write(RobotEvents.Base(_time, DateTimeOffset.UtcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                            "BAR_DUPLICATE_REPLACED", State.ToString(),
                            new
                            {
                                bar_start_utc = bar.TimestampUtc.ToString("o"),
                                existing_source = existingSource.ToString(),
                                new_source = source.ToString(),
                                existing_bar_ohlc = new { O = existingBar.Open, H = existingBar.High, L = existingBar.Low, C = existingBar.Close },
                                new_bar_ohlc = new { O = bar.Open, H = bar.High, L = bar.Low, C = bar.Close },
                                ohlc_differs = ohlcDiffers,
                                buffer_count = _barBuffer.Count,
                                precedence_rule = "LIVE > BARSREQUEST > CSV",
                                note = ohlcDiffers 
                                    ? $"Duplicate bar replaced - {source} bar replaced {existingSource} bar (OHLC values differ)"
                                    : $"Duplicate bar replaced - {source} bar replaced {existingSource} bar (boundary-minute ambiguity)"
                            }));
                    }
                    
                    // Track deduplication
                    _dedupedBarCount++;
                    
                    // Update counters: decrement old source, increment new source
                    // (Only if sources differ - if same source, no counter change needed)
                    if (existingSource != source)
                    {
                        DecrementBarSourceCounter(existingSource);
                        IncrementBarSourceCounter(source);
                    }
                    
                    // Log buffer rejection (replaced, not added)
                    _log.Write(RobotEvents.Base(_time, DateTimeOffset.UtcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                        "BAR_BUFFER_REJECTED", State.ToString(),
                        new
                        {
                            stream_id = Stream,
                            canonical_instrument = CanonicalInstrument,
                            instrument = Instrument,
                            bar_timestamp_utc = bar.TimestampUtc.ToString("o"),
                            bar_timestamp_chicago = _time.ConvertUtcToChicago(bar.TimestampUtc).ToString("o"),
                            bar_source = source.ToString(),
                            rejection_reason = "DUPLICATE_REPLACED",
                            existing_source = existingSource.ToString(),
                            new_source = source.ToString(),
                            current_buffer_count = _barBuffer.Count,
                            range_start_chicago = RangeStartChicagoTime.ToString("o"),
                            slot_time_chicago = SlotTimeChicagoTime.ToString("o"),
                            note = "Bar replaced existing bar with lower precedence"
                        }));
                    
                    return; // Bar replaced, don't add again
                }
                else
                {
                    // Existing bar has higher or equal precedence - reject new bar
                    if (_enableDiagnosticLogs)
                    {
                        _log.Write(RobotEvents.Base(_time, DateTimeOffset.UtcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                            "BAR_DUPLICATE_REJECTED", State.ToString(),
                            new
                            {
                                bar_start_utc = bar.TimestampUtc.ToString("o"),
                                existing_source = existingSource.ToString(),
                                new_source = source.ToString(),
                                precedence_rule = "LIVE > BARSREQUEST > CSV",
                                note = $"Duplicate bar rejected - existing {existingSource} bar has higher or equal precedence than new {source} bar"
                            }));
                    }
                    
                    // Log buffer rejection
                    _log.Write(RobotEvents.Base(_time, DateTimeOffset.UtcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                        "BAR_BUFFER_REJECTED", State.ToString(),
                        new
                        {
                            stream_id = Stream,
                            canonical_instrument = CanonicalInstrument,
                            instrument = Instrument,
                            bar_timestamp_utc = bar.TimestampUtc.ToString("o"),
                            bar_timestamp_chicago = _time.ConvertUtcToChicago(bar.TimestampUtc).ToString("o"),
                            bar_source = source.ToString(),
                            rejection_reason = "DUPLICATE_LOWER_PRECEDENCE",
                            existing_source = existingSource.ToString(),
                            new_source = source.ToString(),
                            current_buffer_count = _barBuffer.Count,
                            range_start_chicago = RangeStartChicagoTime.ToString("o"),
                            slot_time_chicago = SlotTimeChicagoTime.ToString("o"),
                            note = "Duplicate bar rejected - existing bar has higher or equal precedence"
                        }));
                    
                    return; // Reject bar - existing bar has higher precedence
                }
            }
            
            // No duplicate - add bar to buffer
            _barBuffer.Add(bar);
            _barSourceMap[bar.TimestampUtc] = source;
            
            // Track bar source counters (increment for new bar)
            IncrementBarSourceCounter(source);
            
            // Log successful buffer add
            _log.Write(RobotEvents.Base(_time, DateTimeOffset.UtcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                "BAR_BUFFER_ADD_COMMITTED", State.ToString(),
                new
                {
                    stream_id = Stream,
                    canonical_instrument = CanonicalInstrument,
                    instrument = Instrument,
                    bar_timestamp_utc = bar.TimestampUtc.ToString("o"),
                    bar_timestamp_chicago = _time.ConvertUtcToChicago(bar.TimestampUtc).ToString("o"),
                    bar_source = source.ToString(),
                    new_buffer_count = _barBuffer.Count,
                    range_start_chicago = RangeStartChicagoTime.ToString("o"),
                    slot_time_chicago = SlotTimeChicagoTime.ToString("o")
                }));
        }
    }
    
    /// <summary>
    /// Increment bar source counter based on source type.
    /// Helper method to maintain accurate counts for logging.
    /// </summary>
    private void IncrementBarSourceCounter(BarSource source)
    {
        switch (source)
        {
            case BarSource.LIVE:
                _liveBarCount++;
                break;
            case BarSource.BARSREQUEST:
            case BarSource.CSV:
                _historicalBarCount++;
                break;
        }
    }
    
    /// <summary>
    /// Decrement bar source counter based on source type.
    /// Used when replacing a bar with a higher-precedence source.
    /// </summary>
    private void DecrementBarSourceCounter(BarSource source)
    {
        switch (source)
        {
            case BarSource.LIVE:
                _liveBarCount--;
                break;
            case BarSource.BARSREQUEST:
            case BarSource.CSV:
                _historicalBarCount--;
                break;
        }
    }

    /// <summary>
    /// Create a standardized log data object for range information.
    /// </summary>
    private object CreateRangeLogData(decimal? rangeHigh, decimal? rangeLow, decimal? freezeClose, string freezeCloseSource)
    {
        return new
        {
            range_high = rangeHigh,
            range_low = rangeLow,
            range_size = rangeHigh.HasValue && rangeLow.HasValue ? (decimal?)(rangeHigh.Value - rangeLow.Value) : (decimal?)null,
            freeze_close = freezeClose,
            freeze_close_source = freezeCloseSource
        };
    }

    /// <summary>
    /// Check if execution mode is SIM.
    /// Used to determine if BarsRequest pre-hydration is available (SIM mode uses NinjaTrader BarsRequest).
    /// </summary>
    private bool IsSimMode() => _executionMode == ExecutionMode.SIM;

    /// <summary>
    /// Check if stream is stuck in current state and log warning if so.
    /// </summary>
    private void CheckForStuckState(DateTimeOffset utcNow, string stateName, object? context = null)
    {
        // Rate-limit stuck state checks (once per 5 minutes max)
        if (_lastStuckStateCheckUtc.HasValue && 
            (utcNow - _lastStuckStateCheckUtc.Value).TotalMinutes < 5.0)
        {
            return; // Too soon to check again
        }
        
        _lastStuckStateCheckUtc = utcNow;
        
        if (!_stateEntryTimeUtc.HasValue)
        {
            _stateEntryTimeUtc = utcNow; // Initialize if not set
            return;
        }
        
        var timeInState = (utcNow - _stateEntryTimeUtc.Value).TotalMinutes;
        
        // Define expected maximum time in each state (in minutes)
        var maxTimeInState = stateName switch
        {
            "PRE_HYDRATION" => 30.0, // Should complete quickly
            "ARMED" => 60.0, // Can wait up to range_start_time
            "RANGE_BUILDING" => 120.0, // Can take up to slot_time
            "RANGE_LOCKED" => 480.0, // Can last until market close
            _ => 60.0 // Default threshold
        };
        
        if (timeInState > maxTimeInState)
        {
            _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                "STREAM_STATE_STUCK", State.ToString(),
                new
                {
                    state = stateName,
                    time_in_state_minutes = Math.Round(timeInState, 2),
                    max_expected_time_minutes = maxTimeInState,
                    state_entry_time_utc = _stateEntryTimeUtc.Value.ToString("o"),
                    current_time_utc = utcNow.ToString("o"),
                    context = context,
                    note = $"Stream has been in {stateName} state for {Math.Round(timeInState, 1)} minutes, exceeding expected maximum of {maxTimeInState} minutes"
                }));
        }
    }
    
    /// <summary>
    /// PHASE 3: Transition to new state and log with both canonical and execution identities.
    /// </summary>
    private void Transition(DateTimeOffset utcNow, StreamState next, string eventType, object? extra = null)
    {
        var previousState = State;
        var timeInPreviousState = _stateEntryTimeUtc.HasValue 
            ? (utcNow - _stateEntryTimeUtc.Value).TotalMinutes 
            : (double?)null;
        
        var nowChicago = _time.ConvertUtcToChicago(utcNow);
        var barCount = GetBarBufferCount();
        
        // Inverted check: If transitioning to RANGE_LOCKED, verify _rangeLocked == true
        if (next == StreamState.RANGE_LOCKED && !_rangeLocked)
        {
            LogHealth("ERROR", "RANGE_LOCK_TRANSITION_INVALID", 
                "Transitioning to RANGE_LOCKED state without _rangeLocked flag being set",
                new
                {
                    violation = "TRANSITION_WITHOUT_LOCK",
                    range_locked = _rangeLocked,
                    next_state = next.ToString(),
                    note = "State transition to RANGE_LOCKED must only occur after TryLockRange sets _rangeLocked = true"
                });
        }
        
        State = next;
        var stateEntryTimeUtc = utcNow; // Track when we entered this state
        _stateEntryTimeUtc = stateEntryTimeUtc;
        _journal.LastState = next.ToString();
        _journal.LastUpdateUtc = utcNow.ToString("o");
        _journals.Save(_journal);
        
        // Inverted check: If _rangeLocked == true and state is not RANGE_LOCKED after slot time, log CRITICAL.
        // A durable terminal commit intentionally moves RANGE_LOCKED -> DONE and is not a partial lock failure.
        if (_rangeLocked && State != StreamState.RANGE_LOCKED && !(State == StreamState.DONE && _journal.Committed) && utcNow >= SlotTimeUtc)
        {
            LogHealth("CRITICAL", "RANGE_LOCK_TRANSITION_FAILED", 
                "Range lock flag is true but state is not RANGE_LOCKED - partial failure detected",
                new
                {
                    violation = "PARTIAL_LOCK_FAILURE",
                    range_locked = _rangeLocked,
                    current_state = State.ToString(),
                    slot_time_chicago = SlotTimeChicagoTime.ToString("o"),
                    note = "This indicates a fatal error - transition failed after lock was committed"
                });
        }
        
        // Log hydration events for state transitions (edge events only)
        try
        {
            var chicagoNow = _time.ConvertUtcToChicago(utcNow);
            
            // ARMED transition
            if (next == StreamState.ARMED && previousState == StreamState.PRE_HYDRATION)
            {
                var armedData = new Dictionary<string, object>
                {
                    ["previous_state"] = previousState.ToString(),
                    ["transition_reason"] = eventType,
                    ["time_in_previous_state_minutes"] = timeInPreviousState.HasValue ? Math.Round(timeInPreviousState.Value, 2) : (double?)null,
                    ["bar_count"] = barCount
                };
                
                var hydrationEvent = new HydrationEvent(
                    eventType: "ARMED",
                    tradingDay: TradingDate,
                    streamId: Stream,
                    canonicalInstrument: CanonicalInstrument,
                    executionInstrument: ExecutionInstrument,
                    session: Session,
                    slotTimeChicago: SlotTimeChicago,
                    timestampUtc: utcNow,
                    timestampChicago: chicagoNow,
                    state: next.ToString(),
                    data: armedData
                );
                
                _hydrationPersister?.Persist(hydrationEvent);
            }
            // RANGE_BUILDING_START transition
            else if (next == StreamState.RANGE_BUILDING && previousState == StreamState.ARMED)
            {
                var rangeBuildingData = new Dictionary<string, object>
                {
                    ["previous_state"] = previousState.ToString(),
                    ["transition_reason"] = eventType,
                    ["time_in_previous_state_minutes"] = timeInPreviousState.HasValue ? Math.Round(timeInPreviousState.Value, 2) : (double?)null,
                    ["range_start_chicago"] = RangeStartChicagoTime.ToString("o"),
                    ["slot_time_chicago"] = SlotTimeChicagoTime.ToString("o"),
                    ["bar_count"] = barCount
                };
                
                var hydrationEvent = new HydrationEvent(
                    eventType: "RANGE_BUILDING_START",
                    tradingDay: TradingDate,
                    streamId: Stream,
                    canonicalInstrument: CanonicalInstrument,
                    executionInstrument: ExecutionInstrument,
                    session: Session,
                    slotTimeChicago: SlotTimeChicago,
                    timestampUtc: utcNow,
                    timestampChicago: chicagoNow,
                    state: next.ToString(),
                    data: rangeBuildingData
                );
                
                _hydrationPersister?.Persist(hydrationEvent);
            }
        }
        catch (Exception)
        {
            // Fail-safe: hydration logging never throws
        }
        
        // MANDATORY: Emit STREAM_STATE_TRANSITION event for watchdog observability (plan requirement #2)
        // PHASE 3: Include both canonical and execution identities in event payload
        // This event includes only state movement fields, not derived state like range_high/low
        _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc, 
            "STREAM_STATE_TRANSITION", next.ToString(), 
            new
            {
                previous_state = previousState.ToString(),
                new_state = next.ToString(),
                state_entry_time_utc = stateEntryTimeUtc.ToString("o"),
                time_in_previous_state_minutes = timeInPreviousState.HasValue ? Math.Round(timeInPreviousState.Value, 2) : (double?)null,
                execution_instrument = ExecutionInstrument,  // PHASE 3: Execution identity (root name, e.g., "M2K")
                execution_instrument_full_name = ExecutionInstrument,  // Full contract name - use ExecutionInstrument for now (full name not available in StreamStateMachine)
                canonical_instrument = CanonicalInstrument   // PHASE 3: Canonical identity
            }));
        
        // Log state transition with full context (original event for backward compatibility)
        // PHASE 3: Include both identities in full context log
        _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc, eventType, next.ToString(), 
            new
            {
                instrument = Instrument,  // Canonical (top-level field for backward compatibility)
                execution_instrument = ExecutionInstrument,  // PHASE 3: Execution identity
                canonical_instrument = CanonicalInstrument,   // PHASE 3: Canonical identity
                stream_id = Stream,
                trading_date = TradingDate,
                previous_state = previousState.ToString(),
                new_state = next.ToString(),
                time_in_previous_state_minutes = timeInPreviousState.HasValue ? Math.Round(timeInPreviousState.Value, 2) : (double?)null,
                now_chicago = nowChicago.ToString("o"),
                range_start_chicago = RangeStartChicagoTime.ToString("o"),
                bar_count = barCount,
                transition_event = eventType,
                extra_data = extra
            }));
        _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc, "JOURNAL_WRITTEN", next.ToString(),
            new { committed = _journal.Committed, commit_reason = _journal.CommitReason }));
        
        // RANGE_INTENT_ASSERT: Emit once per stream per day when transitioning to ARMED
        if (next == StreamState.ARMED && !_rangeIntentAssertEmitted)
        {
            _rangeIntentAssertEmitted = true;
            _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                "RANGE_INTENT_ASSERT", next.ToString(),
                new
                {
                    trading_date = TradingDate,
                    range_start_chicago = RangeStartChicagoTime.ToString("o"),
                    slot_time_chicago = SlotTimeChicagoTime.ToString("o"),
                    range_start_utc = RangeStartUtc.ToString("o"),
                    slot_time_utc = SlotTimeUtc.ToString("o"),
                    chicago_offset = RangeStartChicagoTime.Offset.ToString(),
                    source = "pre-slot assertion"
                }));
        }
    }
}
