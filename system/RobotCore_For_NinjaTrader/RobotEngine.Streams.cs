using System;
using System.Collections.Generic;
using System.Linq;

namespace QTSW2.Robot.Core;

using QTSW2.Robot.Core.Execution;

public sealed partial class RobotEngine
{
    /// <summary>
    /// Check if strategy started after range window and log warning if so.
    /// </summary>
    private void CheckStartupTiming(DateTimeOffset utcNow)
    {
        if (_spec is null || _time is null) return;

        foreach (var stream in _streams.Values)
        {
            if (utcNow >= stream.RangeStartUtc)
            {
                var nowChicago = _time.ConvertUtcToChicago(utcNow);
                var rangeStartChicago = _time.ConvertUtcToChicago(stream.RangeStartUtc);

                LogEvent(RobotEvents.Base(_time, utcNow, stream.TradingDate, stream.Stream, stream.Instrument, stream.Session, stream.SlotTimeChicago, stream.SlotTimeUtc,
                    "STARTUP_TIMING_WARNING", "ENGINE",
                    new
                    {
                        warning = "Strategy started after range window — range may be incomplete or unavailable",
                        stream_id = stream.Stream,
                        instrument = stream.Instrument,
                        execution_instrument = stream.ExecutionInstrument,
                        canonical_instrument = stream.CanonicalInstrument,
                        session = stream.Session,
                        now_utc = utcNow.ToString("o"),
                        now_chicago = nowChicago.ToString("o"),
                        range_start_utc = stream.RangeStartUtc.ToString("o"),
                        range_start_chicago = rangeStartChicago.ToString("o"),
                        slot_time_chicago = stream.SlotTimeChicago,
                        note = "Ensure NinjaTrader 'Days to load' setting includes historical data for the range window",
                        fix_hint = "Chart → Right-click → Data Series → set 'Days to load' to cover range window (e.g. 5–10 days)"
                    }));
            }
        }
    }

    /// <summary>
    /// Check if any streams are in active trading state (ARMED, RANGE_BUILDING, or RANGE_LOCKED).
    /// Used for session-aware notification filtering.
    /// </summary>
    private bool HasActiveStreams()
    {
        lock (_engineLock)
        {
            return _streams.Values.Any(s => !s.Committed &&
                (s.State == StreamState.ARMED ||
                 s.State == StreamState.RANGE_BUILDING ||
                 s.State == StreamState.RANGE_LOCKED));
        }
    }

    /// <summary>
    /// PHASE 1: Emit prominent operator banner log with execution mode, account, environment, timetable info.
    /// Called after trading date is locked from first session-valid bar.
    /// </summary>
    private void EmitStartupBanner(DateTimeOffset utcNow)
    {
        var enabledStreams = _streams.Values.Where(s => !s.Committed).ToList();
        var enabledInstruments = enabledStreams.Select(s => s.Instrument).Distinct().ToList();

        // PHASE 4: Build quantity mapping for enabled streams (from policy)
        // Note: Single-argument GetOrderQuantity() is acceptable here (banner/logging only, not stream creation)
        var quantityMapping = enabledStreams
            .GroupBy(s => s.ExecutionInstrument)
            .Select(g => new
            {
                execution_instrument = g.Key,
                quantity = GetOrderQuantity(g.Key), // Single-arg overload OK for logging
                stream_count = g.Count()
            })
            .ToList();

        var bannerData = new Dictionary<string, object?>
        {
            ["execution_mode"] = _executionMode.ToString(),
            ["account_name"] = _accountName ?? "UNKNOWN",
            ["environment"] = _environment ?? _executionMode.ToString(),
            ["timetable_hash"] = _lastTimetableHash ?? "NOT_LOADED",
            ["timetable_path"] = _timetablePath,
            ["enabled_stream_count"] = enabledStreams.Count,
            ["enabled_instruments"] = enabledInstruments,
            ["enabled_streams"] = enabledStreams.Select(s => new { stream = s.Stream, instrument = s.Instrument, session = s.Session, slot_time = s.SlotTimeChicago }).ToList(),
            ["spec_name"] = _spec?.spec_name ?? "NOT_LOADED",
            ["spec_revision"] = _spec?.spec_revision ?? "NOT_LOADED",
            ["kill_switch_enabled"] = _killSwitch != null && _killSwitch.IsEnabled(),
            ["health_monitor_enabled"] = _healthMonitor != null,
            // PHASE 4: Order quantity control (from policy file)
            ["order_quantity_mapping"] = quantityMapping,
            ["order_quantity_source"] = "EXECUTION_POLICY_FILE",
            ["chart_trader_quantity_ignored"] = true
        };

        LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString,
            eventType: "OPERATOR_BANNER", state: "ENGINE", bannerData));

        // PHASE 4: Explicit log stating Chart Trader quantity is ignored (policy-controlled)
        LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString,
            eventType: "EXECUTION_QUANTITY_CONTROL", state: "ENGINE",
            new
            {
                order_quantity_source = "EXECUTION_POLICY_FILE",
                chart_trader_quantity_ignored = true,
                execution_instrument_quantity_mapping = quantityMapping,
                note = "Execution quantity is controlled by execution policy file; Chart Trader quantity is ignored. All orders use instrument-specific quantities from policy."
            }));
    }

    /// <summary>
    /// Ensure streams are created after trading date is locked.
    /// Called from OnBar() after trading date is locked from first session-valid bar.
    /// </summary>
    private void EnsureStreamsCreated(DateTimeOffset utcNow)
    {
        if (_spec is null || _time is null || !_activeTradingDate.HasValue)
        {
            // CRITICAL FIX: Enhanced diagnostic logging
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "STREAMS_CREATION_SKIPPED", state: "ENGINE",
                new
                {
                    reason = "MISSING_REQUIREMENTS",
                    spec_is_null = _spec is null,
                    time_is_null = _time is null,
                    trading_date_has_value = _activeTradingDate.HasValue,
                    trading_date = _activeTradingDate.HasValue ? _activeTradingDate.Value.ToString("yyyy-MM-dd") : null,
                    note = "Cannot create streams - missing spec, time service, or trading date"
                }));
            return;
        }

        // If streams already exist, skip creation
        if (_streams.Count > 0)
        {
            return;
        }

        // Validate timetable structure
        if (_lastTimetable == null)
        {
            // CRITICAL FIX: Enhanced diagnostic logging
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: _activeTradingDate.Value.ToString("yyyy-MM-dd"), eventType: "STREAMS_CREATION_FAILED", state: "ENGINE",
                new
                {
                    reason = "NO_TIMETABLE_LOADED",
                    trading_date = _activeTradingDate.Value.ToString("yyyy-MM-dd"),
                    timetable_path = _timetablePath,
                    last_timetable_hash = _lastTimetableHash,
                    note = "Cannot create streams - timetable not loaded. Check if ReloadTimetableIfChanged() completed successfully."
                }));
            return;
        }

        // Validate timetable timezone matches
        if (_lastTimetable.timezone != "America/Chicago")
        {
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: _activeTradingDate.Value.ToString("yyyy-MM-dd"), eventType: "STREAMS_CREATION_FAILED", state: "ENGINE",
                new { reason = "TIMEZONE_MISMATCH", timezone = _lastTimetable.timezone }));
            return;
        }

        // Apply timetable to create streams with locked trading date
        ApplyTimetable(_lastTimetable, utcNow);

        // Log stream creation with detailed stream information
        // PHASE 1: Include both execution and canonical instruments for observability
        var streamDetails = _streams.Values.Select(s => new
        {
            stream_id = s.Stream,
            instrument = s.Instrument,
            execution_instrument = s.ExecutionInstrument,
            canonical_instrument = s.CanonicalInstrument,
            session = s.Session,
            slot_time = s.SlotTimeChicago,
            committed = s.Committed,
            state = s.State.ToString()
        }).ToList();

        // Group by instrument and session for summary
        var streamsByInstrument = _streams.Values
            .GroupBy(s => s.Instrument)
            .ToDictionary(g => g.Key, g => g.Select(s => new { session = s.Session, slot_time = s.SlotTimeChicago, committed = s.Committed }).ToList());

        LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: _activeTradingDate.Value.ToString("yyyy-MM-dd"), eventType: "STREAMS_CREATED", state: "ENGINE",
            new
            {
                stream_count = _streams.Count,
                trading_date = _activeTradingDate.Value.ToString("yyyy-MM-dd"),
                streams = streamDetails,
                streams_by_instrument = streamsByInstrument,
                note = "Streams created after trading date locked from timetable"
            }));

        // Verify all streams use the same trading date (invariant check)
        foreach (var stream in _streams.Values)
        {
            if (stream.TradingDate != _activeTradingDate.Value.ToString("yyyy-MM-dd"))
            {
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: _activeTradingDate.Value.ToString("yyyy-MM-dd"), eventType: "INVARIANT_VIOLATION", state: "ENGINE",
                    new
                    {
                        error = "STREAM_TRADING_DATE_MISMATCH",
                        expected_trading_date = _activeTradingDate.Value.ToString("yyyy-MM-dd"),
                        stream_trading_date = stream.TradingDate,
                        stream_id = stream.Stream
                    }));
            }
        }

        // PHASE 3: Emit canonicalization self-test diagnostic
        EmitCanonicalizationSelfTest(utcNow);

        // Check startup timing now that streams exist
        CheckStartupTiming(utcNow);
    }

    /// <summary>
    /// PHASE 3: Emit canonicalization self-test diagnostic event.
    /// Provides observability for identity mapping verification.
    /// </summary>
    private void EmitCanonicalizationSelfTest(DateTimeOffset utcNow)
    {
        if (_spec is null || _time is null || !_activeTradingDate.HasValue)
            return;

        var canonicalStreamIds = _streams.Values
            .Select(s => s.Stream)
            .OrderBy(s => s)
            .ToList();

        var instrumentMappings = _streams.Values
            .GroupBy(s => s.ExecutionInstrument)
            .Select(g => new
            {
                execution_instrument = g.Key,
                canonical_instrument = g.First().CanonicalInstrument,
                stream_count = g.Count(),
                streams = g.Select(s => s.Stream).OrderBy(s => s).ToList()
            })
            .OrderBy(m => m.execution_instrument)
            .ToList();

        LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: _activeTradingDate.Value.ToString("yyyy-MM-dd"), eventType: "CANONICALIZATION_SELF_TEST", state: "ENGINE",
            new
            {
                trading_date = _activeTradingDate.Value.ToString("yyyy-MM-dd"),
                total_streams = _streams.Count,
                canonical_stream_ids = canonicalStreamIds,
                instrument_mappings = instrumentMappings,
                note = "PHASE 3: Canonicalization self-test - verifies execution/canonical identity mapping"
            }));
    }

    /// <summary>
    /// PHASE 3.1: Check identity invariants periodically and emit status event.
    /// Rate-limited to once per 60 seconds and on-change.
    /// </summary>
    private void CheckIdentityInvariantsIfNeeded(DateTimeOffset utcNow)
    {
        // Rate limit: check every 60 seconds or on-change
        var shouldCheck = !_lastIdentityInvariantsCheckUtc.HasValue ||
                         (utcNow - _lastIdentityInvariantsCheckUtc.Value).TotalSeconds >= IDENTITY_INVARIANTS_CHECK_INTERVAL_SECONDS;

        if (!shouldCheck && _lastIdentityInvariantsPass)
            return; // Too soon and last check passed - skip

        lock (_engineLock)
        {
            // Re-check time inside lock (may have changed)
            if (_lastIdentityInvariantsCheckUtc.HasValue &&
                (utcNow - _lastIdentityInvariantsCheckUtc.Value).TotalSeconds < IDENTITY_INVARIANTS_CHECK_INTERVAL_SECONDS &&
                _lastIdentityInvariantsPass)
            {
                return; // Still too soon and passing
            }

            var violations = new List<string>();
            var canonicalInstrument = "";
            var executionInstrument = _executionInstrument ?? "";
            var streamIds = new List<string>();

            // Check 1: All stream IDs are canonical (no execution instrument in stream ID)
            if (_spec != null && !string.IsNullOrWhiteSpace(executionInstrument))
            {
                canonicalInstrument = GetCanonicalInstrument(executionInstrument);

                foreach (var stream in _streams.Values)
                {
                    streamIds.Add(stream.Stream);

                    // Check: Stream ID should not contain execution instrument if different from canonical
                    if (executionInstrument != canonicalInstrument &&
                        stream.Stream.IndexOf(executionInstrument, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        violations.Add($"Stream ID '{stream.Stream}' contains execution instrument '{executionInstrument}' (should be canonical '{canonicalInstrument}')");
                    }

                    // Check: Stream.Instrument should equal CanonicalInstrument
                    if (stream.Instrument != stream.CanonicalInstrument)
                    {
                        violations.Add($"Stream '{stream.Stream}': Instrument property '{stream.Instrument}' does not match CanonicalInstrument '{stream.CanonicalInstrument}'");
                    }

                    // Check: ExecutionInstrument is present
                    if (string.IsNullOrWhiteSpace(stream.ExecutionInstrument))
                    {
                        violations.Add($"Stream '{stream.Stream}': ExecutionInstrument is null or empty");
                    }
                }
            }

            var pass = violations.Count == 0;
            var shouldEmit = !_lastIdentityInvariantsCheckUtc.HasValue || // First check
                            pass != _lastIdentityInvariantsPass; // Status changed

            if (shouldEmit)
            {
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "IDENTITY_INVARIANTS_STATUS", state: "ENGINE",
                    new
                    {
                        pass = pass,
                        violations = violations,
                        canonical_instrument = canonicalInstrument,
                        execution_instrument = executionInstrument,
                        stream_ids = streamIds.OrderBy(s => s).ToList(),
                        checked_at_utc = utcNow.ToString("o"),
                        note = pass
                            ? "All identity invariants passed - canonical and execution identities are consistent"
                            : $"Identity violations detected: {string.Join("; ", violations)}"
                    }));

                _lastIdentityInvariantsPass = pass;
            }

            _lastIdentityInvariantsCheckUtc = utcNow;
        }
    }

    /// <summary>
    /// PHASE 1: Get canonical instrument for a given execution or root symbol.
    /// Maps micro futures (MES, MNQ, M2K, etc.) to base instruments (ES, NQ, RTY, etc.).
    /// Returns the uppercased symbol when not a known micro in spec. Empty input yields empty string.
    /// </summary>
    private string GetCanonicalInstrument(string instrument)
    {
        if (string.IsNullOrWhiteSpace(instrument))
            return "";

        instrument = instrument.ToUpperInvariant();

        if (_spec != null &&
            _spec.TryGetInstrument(instrument, out var inst))
        {
            if (inst.is_micro && !string.IsNullOrWhiteSpace(inst.base_instrument))
            {
                return inst.base_instrument.ToUpperInvariant();
            }
        }

        return instrument;
    }

    /// <summary>
    /// PHASE 4: Get order quantity for execution instrument from execution policy (canonical + execution overload).
    /// PRIMARY PATH: Use this overload for stream creation to avoid GetCanonicalInstrument divergence risk.
    /// </summary>
    /// <param name="canonicalInstrument">Canonical instrument (e.g., "ES", "NQ")</param>
    /// <param name="executionInstrument">Execution instrument (e.g., "ES", "MES")</param>
    /// <returns>Order quantity for the instrument (base_size from policy)</returns>
    /// <exception cref="ArgumentException">If instrument is null or empty</exception>
    /// <exception cref="InvalidOperationException">If policy not loaded, instrument unknown, or quantity invalid</exception>
    public int GetOrderQuantity(string canonicalInstrument, string executionInstrument)
    {
        if (string.IsNullOrWhiteSpace(canonicalInstrument))
        {
            throw new ArgumentException("Canonical instrument cannot be null or empty", nameof(canonicalInstrument));
        }
        if (string.IsNullOrWhiteSpace(executionInstrument))
        {
            throw new ArgumentException("Execution instrument cannot be null or empty", nameof(executionInstrument));
        }

        if (_executionPolicy == null)
        {
            throw new InvalidOperationException("PHASE 4: Execution policy not loaded. Cannot resolve order quantity.");
        }

        var canonicalUpper = canonicalInstrument.Trim().ToUpperInvariant();
        var execUpper = executionInstrument.Trim().ToUpperInvariant();

        // Get execution instrument policy
        var execInstPolicy = _executionPolicy.GetExecutionInstrumentPolicy(canonicalUpper, execUpper);
        if (execInstPolicy == null)
        {
            throw new InvalidOperationException(
                $"PHASE 4: Execution instrument '{executionInstrument}' not found in execution policy for canonical market '{canonicalInstrument}'. " +
                $"Execution blocked.");
        }

        if (!execInstPolicy.enabled)
        {
            throw new InvalidOperationException(
                $"PHASE 4: Execution instrument '{executionInstrument}' is disabled in execution policy for canonical market '{canonicalInstrument}'. " +
                $"Execution blocked.");
        }

        // Return base_size (policy-controlled quantity)
        var quantity = execInstPolicy.base_size;

        if (quantity <= 0)
        {
            throw new InvalidOperationException(
                $"PHASE 4: Invalid quantity {quantity} for execution instrument '{executionInstrument}' in policy. " +
                $"Quantity must be positive.");
        }

        return quantity;
    }

    /// <summary>
    /// PHASE 4: Get order quantity for execution instrument from execution policy (single-argument overload).
    /// SECONDARY PATH: Use only for banner/logging, never for stream creation.
    /// Relies on GetCanonicalInstrument() which could diverge from directive's canonical identity.
    /// </summary>
    /// <param name="executionInstrument">Execution instrument (e.g., "MES", "ES", "NQ")</param>
    /// <returns>Order quantity for the instrument (base_size from policy)</returns>
    /// <exception cref="ArgumentException">If instrument is null or empty</exception>
    /// <exception cref="InvalidOperationException">If policy not loaded, instrument unknown, or quantity invalid</exception>
    public int GetOrderQuantity(string executionInstrument)
    {
        if (string.IsNullOrWhiteSpace(executionInstrument))
        {
            throw new ArgumentException("Execution instrument cannot be null or empty", nameof(executionInstrument));
        }

        // GetCanonicalInstrument is pure and safe, but use canonical+execution overload for stream creation
        var canonicalInstrument = GetCanonicalInstrument(executionInstrument);
        return GetOrderQuantity(canonicalInstrument, executionInstrument);
    }

    /// <summary>
    /// Get order quantity info (baseSize, maxSize) from execution policy.
    /// </summary>
    /// <param name="canonicalInstrument">Canonical instrument (e.g., "ES", "NQ")</param>
    /// <param name="executionInstrument">Execution instrument (e.g., "ES", "MES")</param>
    /// <returns>Tuple of (baseSize, maxSize) from policy</returns>
    /// <exception cref="InvalidOperationException">If policy not loaded, instrument not found, not enabled, or sizes invalid</exception>
    public (int baseSize, int maxSize) GetOrderQuantityInfo(string canonicalInstrument, string executionInstrument)
    {
        if (_executionPolicy == null)
        {
            throw new InvalidOperationException("Execution policy not loaded");
        }

        var policy = _executionPolicy.GetExecutionInstrumentPolicy(canonicalInstrument, executionInstrument);
        if (policy == null)
        {
            throw new InvalidOperationException(
                $"Execution instrument {executionInstrument} not found in policy for {canonicalInstrument}");
        }

        if (!policy.enabled)
        {
            throw new InvalidOperationException(
                $"Execution instrument {executionInstrument} not enabled for {canonicalInstrument}");
        }

        if (policy.base_size <= 0 || policy.max_size <= 0)
        {
            throw new InvalidOperationException(
                $"Invalid policy sizes: baseSize={policy.base_size}, maxSize={policy.max_size}");
        }

        if (policy.base_size > policy.max_size)
        {
            throw new InvalidOperationException(
                $"Policy violation: baseSize ({policy.base_size}) > maxSize ({policy.max_size})");
        }

        return (policy.base_size, policy.max_size);
    }

    /// Phase 4A: Called when forced flatten fails or exposure remains after flatten.
    /// Freezes instrument and stands down streams — no silent continuation.
    /// </summary>
    public void OnForcedFlattenFailed(string instrument, string reason, DateTimeOffset utcNow)
    {
        if (string.IsNullOrWhiteSpace(instrument)) return;
        if (string.Equals(reason, "FORCED_FLATTEN_BROKER_TIMEOUT", StringComparison.Ordinal) &&
            HasInterruptedSessionCloseStreamForInstrument(instrument.Trim()))
        {
            var inst = instrument.Trim();
            _frozenInstruments.Add(inst);
            var latchCreateAuthority = UnifiedExecutionAuthority.EvaluateAction(new ExecutionAuthorityActionEvaluationRequest
            {
                Action = ExecutionAuthorityAction.LatchCreate,
                Source = "RobotEngine.OnForcedFlattenFailed",
                Instrument = inst,
                DurableLatchReason = reason,
                UtcNow = utcNow
            });
            if (latchCreateAuthority.Allowed)
            {
                _riskLatchManager?.Persist(inst, reason);
            }
            else
            {
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: _activeTradingDate?.ToString("yyyy-MM-dd") ?? "",
                    eventType: "RISK_LATCH_CREATE_BLOCKED", state: "ENGINE",
                    new
                    {
                        instrument = inst,
                        reason,
                        authority_gate = latchCreateAuthority.GateName,
                        deny_reason = latchCreateAuthority.DenyReason,
                        note = latchCreateAuthority.Detail
                    }));
            }
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: _activeTradingDate?.ToString("yyyy-MM-dd") ?? "",
                eventType: "FORCED_FLATTEN_TIMEOUT_REENTRY_DEFERRED", state: "ENGINE",
                new
                {
                    instrument = inst,
                    reason,
                    note = "Forced-flatten submit was accepted but broker-flat confirmation was slow; keeping interrupted stream active and risk-latched for broker-flat-gated reentry."
                }));
            return;
        }
        StandDownStreamsForInstrument(instrument.Trim(), utcNow, reason);
    }

    private bool HasInterruptedSessionCloseStreamForInstrument(string instrument)
    {
        lock (_engineLock)
        {
            foreach (var stream in _streams.Values)
            {
                var streamInst = stream.Instrument ?? "";
                var streamExec = stream.ExecutionInstrument ?? "";
                if (!string.Equals(streamInst, instrument, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(streamExec, instrument, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (stream.ExecutionInterruptedByClose)
                    return true;
            }
        }

        return false;
    }

    /// PHASE 2: Stand down a specific stream (for protective order failure recovery).
    /// </summary>
    public void StandDownStream(string streamId, DateTimeOffset utcNow, string reason)
    {
        lock (_engineLock)
        {
            if (_streams.TryGetValue(streamId, out var stream))
            {
                stream.EnterRecoveryManage(utcNow, reason);
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: _activeTradingDate?.ToString("yyyy-MM-dd") ?? "",
                    eventType: "STREAM_STAND_DOWN", state: "ENGINE",
                    new { stream_id = streamId, reason = reason }));
            }
        }
    }

    /// <summary>P2 Phase 1 (P2.1-C): stand down one stream for ownership ambiguity; siblings unaffected.</summary>
    public void StandDownSingleStreamForOwnershipAmbiguity(string streamId, DateTimeOffset utcNow, StateOwnershipAttributionResult attribution)
    {
        if (string.IsNullOrEmpty(streamId)) return;
        var gateEngaged = _mismatchCoordinator != null &&
                          _mismatchCoordinator.IsInstrumentBlockedByMismatch(attribution.ExecutionInstrumentKey);
        lock (_engineLock)
        {
            if (_streams.TryGetValue(streamId, out var stream))
            {
                stream.EnterRecoveryManage(utcNow, "P2_OWNERSHIP_AMBIGUITY");
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: _activeTradingDate?.ToString("yyyy-MM-dd") ?? "",
                    eventType: "STREAM_SCOPED_STAND_DOWN", state: "ENGINE",
                    new
                    {
                        stream_id = streamId,
                        execution_instrument_key = attribution.ExecutionInstrumentKey,
                        implicated_streams = attribution.ImplicatedStreams,
                        implicated_intent_ids = attribution.ImplicatedIntentIds,
                        unattributed_order_ids = attribution.UnattributedBrokerOrderIds,
                        recovery_scope = "StreamScoped",
                        reason = "OWNERSHIP_AMBIGUITY_P2",
                        gate_state = gateEngaged ? "engaged" : "not_engaged",
                        sibling_streams_preserved = true,
                        destructive_action_blocked = true
                    }));
            }
        }
    }

    /// <summary>P2 Phase 1: per-stream open exposure from journals for reconciliation attribution.</summary>
    private List<(string Stream, int OpenQty)> GetStreamOpenExposureForInstrument(string instrument)
    {
        var inst = (instrument ?? "").Trim();
        var execVariant = inst.StartsWith("M") && inst.Length > 1 ? inst : "M" + inst;
        var byStream = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in _executionJournal.GetOpenJournalEntriesByInstrument())
        {
            var jinst = kvp.Key?.Trim() ?? "";
            if (string.IsNullOrEmpty(jinst)) continue;
            if (!string.Equals(jinst, inst, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(jinst, execVariant, StringComparison.OrdinalIgnoreCase))
                continue;
            foreach (var (_, stream, _, entry) in kvp.Value)
            {
                var qty = entry.EntryFilledQuantityTotal;
                if (qty <= 0) continue;
                var s = stream ?? "";
                byStream.TryGetValue(s, out var q);
                byStream[s] = q + qty;
            }
        }
        return byStream.Select(k => (k.Key, k.Value)).ToList();
    }

    /// <summary>

    /// Log a periodic summary of all stream states (diagnostic only, rate-limited from Tick).
    /// Independent of event-driven snapshots - never suppressed.
    /// </summary>
    private void LogStreamStatusSummary(DateTimeOffset utcNow)
    {
        if (_streams.Count == 0) return;
        var streams = _streams.Values.Select(s => s.GetStatusForLogging(utcNow)).ToList();
        var payload = new Dictionary<string, object>
        {
            ["streams"] = streams,
            ["stream_count"] = streams.Count,
            ["trading_date"] = TradingDateString,
            ["trigger"] = "PERIODIC",
            ["note"] = "Periodic snapshot of all stream states (diagnostic)"
        };
        LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "STREAM_STATUS_SUMMARY", state: "ENGINE", payload));
    }

    /// <summary>
    /// Emit event-driven stream snapshot if 60s rate limit allows.
    /// Call from: stream Transition, stream Commit, MarkBarsRequestCompleted.
    /// Rate limit applies only to event-driven; periodic snapshot remains independent.
    /// </summary>
    public void TryEmitEventDrivenSnapshot(DateTimeOffset utcNow, string trigger)
    {
        lock (_engineLock)
        {
            if (_lastEventDrivenSnapshotUtc.HasValue &&
                (utcNow - _lastEventDrivenSnapshotUtc.Value).TotalSeconds < EVENT_DRIVEN_SNAPSHOT_RATE_LIMIT_SECONDS)
                return;
            if (_streams.Count == 0) return;

            _lastEventDrivenSnapshotUtc = utcNow;
            var streams = _streams.Values.Select(s => s.GetStatusForLogging(utcNow)).ToList();
            var payload = new Dictionary<string, object>
            {
                ["streams"] = streams,
                ["stream_count"] = streams.Count,
                ["trading_date"] = TradingDateString,
                ["trigger"] = trigger,
                ["note"] = "Event-driven snapshot"
            };
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "STREAM_STATUS_SUMMARY", state: "ENGINE", payload));
        }
    }

    /// <summary>
    /// Log a one-time snapshot of all stream states (e.g., when Realtime is reached).
    /// Call from strategy when transitioning to Realtime state.
    /// </summary>
    public void LogStreamStateSnapshot(DateTimeOffset utcNow)
    {
        lock (_engineLock)
        {
            if (_streams.Count == 0) return;
            var streams = _streams.Values.Select(s => s.GetStatusForLogging(utcNow)).ToList();
            var payload = new Dictionary<string, object>
            {
                ["streams"] = streams,
                ["stream_count"] = streams.Count,
                ["trading_date"] = TradingDateString,
                ["note"] = "Snapshot of all stream states at Realtime transition"
            };
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "STREAM_STATE_SNAPSHOT", state: "ENGINE", payload));
        }
    }

    /// <summary>
    /// Log invariant check for each active stream: chart/canonical instrument vs execution policy mapping vs stream execution instrument.
    /// Prevents silent regressions (e.g., CL chart passing "CL" to BE filter when intents have "MCL").
    /// Call from strategy at Realtime transition, after LogStreamStateSnapshot.
    /// </summary>
    public void LogStreamInstrumentInvariantCheck(DateTimeOffset utcNow)
    {
        lock (_engineLock)
        {
            if (_streams.Count == 0 || _executionPolicy == null) return;
            foreach (var stream in _streams.Values)
            {
                var chartInstrument = stream.CanonicalInstrument;
                var executionPolicyMapped = _executionPolicy.GetEnabledExecutionInstrument(chartInstrument) ?? "N/A";
                var streamExecutionInstrument = stream.ExecutionInstrument;
                var status = string.Equals(executionPolicyMapped, streamExecutionInstrument, StringComparison.OrdinalIgnoreCase) ? "OK" : "MISMATCH";
                var payload = new Dictionary<string, object>
                {
                    ["chartInstrument"] = chartInstrument,
                    ["executionPolicyMapped"] = executionPolicyMapped,
                    ["streamExecutionInstrument"] = streamExecutionInstrument,
                    ["stream_id"] = stream.Stream,
                    ["status"] = status
                };
                var eventType = status == "OK" ? "STREAM_INSTRUMENT_INVARIANT_CHECK" : "STREAM_INSTRUMENT_INVARIANT_MISMATCH";
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: eventType, state: "ENGINE", payload));
            }
        }
    }

    /// <summary>
    /// Get number of streams created from timetable for this instrument (for UI/logging).
    /// </summary>
    public int GetStreamCount()
    {
        lock (_engineLock)
        {
            return _streams?.Count ?? 0;
        }
    }
}
