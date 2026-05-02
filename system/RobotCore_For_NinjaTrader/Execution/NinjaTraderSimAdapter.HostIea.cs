// CRITICAL: Define NINJATRADER for NinjaTrader's compiler
// NinjaTrader compiles to tmp folder and may not respect .csproj DefineConstants
#define NINJATRADER

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using QTSW2.Robot.Contracts;
using QTSW2.Robot.Core.Diagnostics;

namespace QTSW2.Robot.Core.Execution;

public sealed partial class NinjaTraderSimAdapter
{
    /// <summary>
    /// PHASE 2: Set callbacks for stream stand-down and notification service access.
    /// G5: <paramref name="isExecutionAllowedCallback"/> is recovery readiness only; <paramref name="isGlobalKillSwitchActive"/> is the kill predicate.
    /// Gap 5: blockInstrumentCallback invoked when IEA EnqueueAndWait fails (timeout/overflow); engine freezes instrument and stands down streams.
    /// </summary>
    public void SetEngineCallbacks(
        Action<string, DateTimeOffset, string>? standDownStreamCallback,
        Func<object?>? getNotificationServiceCallback,
        Func<bool>? isExecutionAllowedCallback = null,
        Action<string, DateTimeOffset, string>? blockInstrumentCallback = null,
        Action<string, string, object>? onSupervisoryCriticalCallback = null,
        Action<string, DateTimeOffset>? onReentryFillCallback = null,
        Action<string, DateTimeOffset>? onReentryProtectionAcceptedCallback = null,
        Func<string, string, (bool ShouldCancel, string? Reason)>? shouldCancelEntryOrdersForStreamCallback = null,
        Func<string, bool>? hasSlotJournalWithEntryStopsForInstrumentCallback = null,
        Func<string?>? getActiveTradingDateString = null,
        Func<string, bool>? journalIntegrityRepairActiveForInstrumentCallback = null,
        Func<bool>? isGlobalKillSwitchActive = null,
        Func<string, bool>? isMismatchExecutionBlocked = null,
        Func<string, string?, bool>? isMismatchExecutionBlockedForSubmit = null,
        Func<string, string?, bool>? isInstrumentFrozenOrEpaBlocked = null,
        Action<string, DateTimeOffset, bool, string?>? onReentrySubmitCompletedCallback = null,
        Action<string, string, DateTimeOffset>? onSessionCloseFlattenConfirmedLateCallback = null,
        Func<bool>? isPlaybackStallNtCallBlocked = null)
    {
        _standDownStreamCallback = standDownStreamCallback;
        _getNotificationServiceCallback = getNotificationServiceCallback;
        _isRecoveryExecutionAllowedCallback = isExecutionAllowedCallback;
        _blockInstrumentCallback = blockInstrumentCallback;
        _onSupervisoryCriticalCallback = onSupervisoryCriticalCallback;
        _onReentryFillCallback = onReentryFillCallback;
        _onReentryProtectionAcceptedCallback = onReentryProtectionAcceptedCallback;
        _onReentrySubmitCompletedCallback = onReentrySubmitCompletedCallback;
        _onSessionCloseFlattenConfirmedLateCallback = onSessionCloseFlattenConfirmedLateCallback;
        _shouldCancelEntryOrdersForStreamCallback = shouldCancelEntryOrdersForStreamCallback;
        _hasSlotJournalWithEntryStopsForInstrumentCallback = hasSlotJournalWithEntryStopsForInstrumentCallback;
        _getActiveTradingDateString = getActiveTradingDateString;
        _journalIntegrityRepairActiveForInstrumentCallback = journalIntegrityRepairActiveForInstrumentCallback;
        _isGlobalKillSwitchActive = isGlobalKillSwitchActive;
        _isMismatchExecutionBlocked = isMismatchExecutionBlocked;
        _isMismatchExecutionBlockedForSubmit = isMismatchExecutionBlockedForSubmit;
        _isInstrumentFrozenOrEpaBlocked = isInstrumentFrozenOrEpaBlocked;
        _isPlaybackStallNtCallBlockedCallback = isPlaybackStallNtCallBlocked;
    }

    /// <summary>
    /// Integration/harness tests: supply account snapshots for execution-safety evaluation without NT context.
    /// Pair with <see cref="TryExecutionSafetyGateForOrderSubmitIntegration"/> / structural diagnostics. Clear when done.
    /// </summary>
    public void SetExecutionSafetyTestAccountSnapshotFactory(Func<DateTimeOffset, AccountSnapshot?>? factory) =>
        _executionSafetyTestGetAccountSnapshot = factory;

    /// <summary>True after any session identity mismatch (until process restart). No automatic reset.</summary>
    public bool IsSessionIdentityLatched => Volatile.Read(ref _sessionMismatchBlocked) != 0;

    /// <summary>Harness/tests: number of SESSION_IDENTITY_MISMATCH_BLOCKED emissions this process (0 or 1 per episode design).</summary>
    public int SessionIdentityMismatchCriticalEmitCount => Volatile.Read(ref _sessionIdentityMismatchCriticalEmitCount);

    /// <summary>Total blocked submit attempts (identity mismatch + subsequent latched rejects). Dashboard / alerts.</summary>
    public long SessionIdentityBlockCount => Volatile.Read(ref _sessionIdentityBlockCount);

    private void RecordSessionIdentityBlockAttempt() => Interlocked.Increment(ref _sessionIdentityBlockCount);

    /// <summary>
    /// IEA entry-stop aggregation: every intent in the bundle must pass the same gate as single-path submits.
    /// Empty or all-invalid id lists are invariant failures (non-latching); real date mismatches use <see cref="TrySessionIdentityGate"/> latch behavior unchanged.
    /// </summary>
    internal bool TrySessionIdentityGateForIntentBundle(
        IReadOnlyList<string> intentIds,
        string instrument,
        string submitPath,
        DateTimeOffset utcNow,
        out OrderSubmissionResult? failure)
    {
        failure = null;
        if (intentIds == null || intentIds.Count == 0)
        {
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "SESSION_IDENTITY_BUNDLE_GATE_REJECTED", state: "ENGINE",
                new
                {
                    reason = "empty_bundle",
                    instrument,
                    submit_path = submitPath,
                    session_identity_latched_armed = false,
                    note = "Bundle gate requires at least one intent id"
                }));
            failure = OrderSubmissionResult.FailureResult("INVALID_INTENT_BUNDLE_EMPTY", utcNow);
            return false;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var validatedIntentChecks = 0;
        foreach (var raw in intentIds)
        {
            var id = (raw ?? "").Trim();
            if (string.IsNullOrEmpty(id) || !seen.Add(id)) continue;
            validatedIntentChecks++;
            if (!TrySessionIdentityGate(id, instrument, submitPath, utcNow, null, out failure))
            {
                _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "SESSION_IDENTITY_BUNDLE_GATE_REJECTED", state: "ENGINE",
                    new
                    {
                        reason = "session_identity_gate_failed",
                        failed_intent_id = id,
                        instrument,
                        submit_path = submitPath,
                        session_identity_latched_armed = Volatile.Read(ref _sessionMismatchBlocked) != 0,
                        failure_error = failure?.ErrorMessage
                    }));
                return false;
            }
        }

        if (validatedIntentChecks == 0)
        {
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "SESSION_IDENTITY_BUNDLE_GATE_REJECTED", state: "ENGINE",
                new
                {
                    reason = "all_intent_ids_invalid",
                    instrument,
                    submit_path = submitPath,
                    session_identity_latched_armed = false,
                    raw_intent_id_count = intentIds.Count,
                    note = "Every id was blank or duplicate-only — invariant failure"
                }));
            failure = OrderSubmissionResult.FailureResult("INVALID_INTENT_BUNDLE_NO_VALID_IDS", utcNow);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Flatten / emergency-close: no stream intent; align session line to engine active day so we do not open wrong-day intent while still blocking when latched.
    /// </summary>
    private bool TrySessionIdentityGateDestructiveFlatten(string instrument, DateTimeOffset utcNow, out OrderSubmissionResult? failure)
    {
        failure = null;
        var getActive = _getActiveTradingDateString;
        if (getActive == null)
            return true;
        var active = (getActive() ?? "").Trim();
        if (string.IsNullOrEmpty(active))
            return true;
        return TrySessionIdentityGate("", instrument, "flatten", utcNow, explicitAttemptedTradingDate: active, out failure);
    }

    /// <summary>
    /// Hard gate: when engine active trading date is non-empty, submission must resolve an attempted date that matches it.
    /// Blank attempted after explicit + map resolution: non-latching <c>SESSION_IDENTITY_UNRESOLVED</c> (ENGINE log, no latch).
    /// Resolved attempted ≠ active: latch, <c>SESSION_IDENTITY_MISMATCH_BLOCKED</c> CRITICAL once, <c>SESSION_IDENTITY_MISMATCH</c>. When latched: <c>SESSION_IDENTITY_LATCHED</c>.
    /// </summary>
    private bool TrySessionIdentityGate(
        string intentId,
        string instrument,
        string submitPath,
        DateTimeOffset utcNow,
        string? explicitAttemptedTradingDate,
        out OrderSubmissionResult? failure)
    {
        failure = null;
        if (Volatile.Read(ref _sessionMismatchBlocked) != 0)
        {
            RecordSessionIdentityBlockAttempt();
            failure = OrderSubmissionResult.FailureResult("SESSION_IDENTITY_LATCHED", utcNow);
            return false;
        }

        var getActive = _getActiveTradingDateString;
        if (getActive == null)
            return true;

        var active = (getActive() ?? "").Trim();
        if (string.IsNullOrEmpty(active))
            return true;

        string? attempted = explicitAttemptedTradingDate?.Trim();
        Intent? intentFromMap = null;
        var mapLookupSucceeded = false;
        if (string.IsNullOrEmpty(attempted) && !string.IsNullOrEmpty(intentId) &&
            IntentMap.TryGetValue(intentId, out var intent) && intent != null)
        {
            mapLookupSucceeded = true;
            intentFromMap = intent;
            attempted = intent.TradingDate?.Trim();
        }

        if (string.IsNullOrEmpty(attempted))
        {
            var intentIdEmpty = string.IsNullOrEmpty(intentId);
            var intentMapLookupFailed = !intentIdEmpty && !mapLookupSucceeded;
            var intentTradingDateBlank = mapLookupSucceeded && string.IsNullOrEmpty(intentFromMap?.TradingDate?.Trim());

            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: active, eventType: "SESSION_IDENTITY_UNRESOLVED", state: "ENGINE",
                new
                {
                    active_trading_date = active,
                    attempted_trading_date = (string?)null,
                    intent_id = intentIdEmpty ? null : intentId,
                    instrument,
                    submit_path = submitPath,
                    reason = "session_identity_unresolved",
                    intent_id_empty = intentIdEmpty,
                    intent_map_lookup_failed = intentMapLookupFailed,
                    intent_trading_date_blank = intentTradingDateBlank
                }));
            failure = OrderSubmissionResult.FailureResult("SESSION_IDENTITY_UNRESOLVED", utcNow);
            return false;
        }

        if (string.Equals(attempted, active, StringComparison.Ordinal))
            return true;

        if (Interlocked.CompareExchange(ref _sessionMismatchBlocked, 1, 0) == 0)
        {
            Interlocked.Increment(ref _sessionIdentityMismatchCriticalEmitCount);
            RecordSessionIdentityBlockAttempt();
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: active, eventType: "SESSION_IDENTITY_MISMATCH_BLOCKED", state: "CRITICAL",
                new
                {
                    active_trading_date = active,
                    attempted_trading_date = attempted,
                    intent_id = string.IsNullOrEmpty(intentId) ? null : intentId,
                    instrument,
                    submit_path = submitPath,
                    reason = "session_identity_mismatch",
                    session_identity_block_count = Volatile.Read(ref _sessionIdentityBlockCount)
                }));
        }
        else
            RecordSessionIdentityBlockAttempt();
        failure = OrderSubmissionResult.FailureResult("SESSION_IDENTITY_MISMATCH", utcNow);
        return false;
    }

    /// <summary>
    /// Non-latching: rejects a single submission when <paramref name="intentId"/> is non-empty but not in <see cref="IntentMap"/>,
    /// or when it does not equal <see cref="Intent.ComputeIntentId"/> for the intent stored under that key.
    /// Empty <paramref name="intentId"/> skips this guard (flatten-only paths). Does not touch <c>_sessionMismatchBlocked</c>.
    /// </summary>
    private bool TryIntentIdConsistencyGuard(
        string intentId,
        string instrument,
        string submitPath,
        DateTimeOffset utcNow,
        out OrderSubmissionResult? failure)
    {
        failure = null;
        if (string.IsNullOrEmpty(intentId))
            return true;

        if (!IntentMap.TryGetValue(intentId, out var intent) || intent == null)
        {
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "INTENT_LOOKUP_MISS_AT_SUBMIT_REJECTED", state: "CRITICAL",
                new { intent_id = intentId, submit_path = submitPath, instrument }));
            failure = OrderSubmissionResult.FailureResult("INTENT_NOT_IN_MAP", utcNow);
            return false;
        }

        var canonicalId = intent.ComputeIntentId();
        if (string.Equals(intentId, canonicalId, StringComparison.Ordinal))
            return true;

        _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: intent.TradingDate ?? "", eventType: "INTENT_ID_MISMATCH_REJECTED", state: "ERROR",
            new
            {
                provided_intent_id = intentId,
                canonical_intent_id = canonicalId,
                stream = intent.Stream ?? "",
                instrument,
                submit_path = submitPath,
                trading_date = intent.TradingDate ?? "",
                session = intent.Session ?? "",
                slot_time_chicago = intent.SlotTimeChicago ?? "",
                direction = intent.Direction,
                trigger_reason = intent.TriggerReason
            }));
        failure = OrderSubmissionResult.FailureResult("INTENT_ID_MISMATCH", utcNow);
        return false;
    }

    /// <summary>Logs explicit multi-intent ids when tag uses QTSW2:AGG: — downstream still resolves primary via <see cref="RobotOrderIds.DecodeIntentId"/>.</summary>
    private void LogAggregatedTagAttributionIfNeeded(string? tag, string context, DateTimeOffset utcNow)
    {
        if (string.IsNullOrEmpty(tag) || !RobotOrderIds.IsAggregatedTag(tag)) return;
        var ids = RobotOrderIds.DecodeAggregatedIntentIds(tag);
        _log.Write(RobotEvents.ExecutionBase(utcNow, ids?.Count > 0 ? ids[0]! : "", "", "AGG_TAG_ATTRIBUTION",
            new
            {
                context,
                intent_ids = ids,
                primary_intent_id = ids?.Count > 0 ? ids[0] : null,
                note = "Multi-intent tag; single-stream resolution uses primary (first) id unless caller allocates"
            }));
    }

    /// <summary>
    /// Set intent exposure coordinator.
    /// </summary>
    public void SetCoordinator(InstrumentIntentCoordinator coordinator)
    {
        _coordinator = coordinator;
    }

    /// <summary>
    /// Set NinjaTrader context (Account, Instrument) from Strategy host.
    /// Called by RobotSimStrategy after NT context is available.
    /// </summary>
    /// <param name="account">NT Account.</param>
    /// <param name="instrument">NT Instrument.</param>
    /// <param name="engineExecutionInstrument">Engine's execution instrument (e.g., MNQ) — used for IEA key when IEA enabled.</param>
    public void SetNTContext(object account, object instrument, string? engineExecutionInstrument = null)
    {
        _ntAccount = account;
        _ntInstrument = instrument;
        _ntContextSet = true;
        _ieaEngineExecutionInstrument = engineExecutionInstrument;
        
        // Resolve IEA when use_instrument_execution_authority is enabled
        var accountName = GetAccountName(account);
        var executionInstrumentKey = ExecutionInstrumentResolver.ResolveExecutionInstrumentKey(accountName, instrument, engineExecutionInstrument);
        if (_useInstrumentExecutionAuthority)
        {
            _ntActionQueue = new StrategyThreadExecutor(_log);
            _ieaAccountName = accountName;
            _iea = InstrumentExecutionAuthorityRegistry.GetOrCreate(accountName, executionInstrumentKey,
                () => new InstrumentExecutionAuthority(accountName, executionInstrumentKey, this, _log, _aggregationPolicy));
            // Registry caches IEA across strategy restarts; always bind to this adapter instance (SIM verify is on this, not the stale executor).
            _iea.RebindExecutor(this, _strategyInstanceIdForAudit);
            _iea.SetEventWriter(_eventWriter);
            _iea.SetOnEnqueueFailureCallback(_blockInstrumentCallback);
            _iea.SetOnRecoveryRequestedCallback(OnRecoveryRequested);
            _iea.SetOnRecoveryFlattenRequestedCallback(OnRecoveryFlattenRequestedFromIeCallback);
            _iea.SetOnBootstrapSnapshotRequestedCallback(OnBootstrapSnapshotRequested);
            _iea.SetOnSupervisoryCriticalCallback(_onSupervisoryCriticalCallback);
            _iea.SetAggregationSiblingCancelGuardCallback(key => _instrumentMismatchGateEngaged?.Invoke(key) == true);
            _iea.SetOnP2StreamContainmentCallback((attr, now) => _p2StreamContainmentEngineCallback?.Invoke(attr, now));
            _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", eventType: "IEA_BINDING", state: "ENGINE",
                new
                {
                    account_name = accountName,
                    execution_instrument_key = executionInstrumentKey,
                    iea_instance_id = _iea.InstanceId,
                    note = "IEA bound for execution routing"
                }));
        }

        // Deterministic routing: register endpoint for (account, executionInstrumentKey)
        // GC chart with MGC execution registers for MGC so fills route correctly regardless of which strategy receives the callback
        // Phase 1: TryRegisterEndpoint - fail closed on conflict (no overwrite)
        if (!string.IsNullOrEmpty(accountName) && !string.IsNullOrEmpty(executionInstrumentKey))
        {
            var instanceId = _useInstrumentExecutionAuthority && _iea != null
                ? $"IEA:{_iea.InstanceId}"
                : $"ADAPTER:{_adapterInstanceId}";
            var endpoint = _useInstrumentExecutionAuthority && _iea != null
                ? (Action<object, object>)_iea.EnqueueExecutionUpdate
                : HandleExecutionUpdate;
            if (!ExecutionUpdateRouter.TryRegisterEndpoint(accountName, executionInstrumentKey, endpoint, instanceId, _log))
                throw new InvalidOperationException($"EXEC_ROUTER_ENDPOINT_CONFLICT: Cannot register for ({accountName}, {executionInstrumentKey}) - different instance already owns. Fail closed.");
        }

        // STEP 1: SIM Account Verification (MANDATORY) - now with real NT account
        VerifySimAccount();

        // One-time: Re-register intents from open journal entries BEFORE bootstrap so runtime snapshot and adoption have correct IntentMap.
        HydrateIntentsFromOpenJournals();

        // Phase 4: Bootstrap after hydration. Centralizes startup/reconnect determinism.
        if (_useInstrumentExecutionAuthority && _iea != null)
        {
            _iea.BeginBootstrapForInstrument(executionInstrumentKey, BootstrapReason.STRATEGY_START, DateTimeOffset.UtcNow);
        }

        TryEmitInvestigationRuntimeFingerprint(accountName, executionInstrumentKey);
    }

    /// <summary>
    /// One-time hydration: Register intents from open journal entries so BE detection works after restart.
    /// Only registers intents matching this adapter's execution instrument. Idempotent.
    /// </summary>
    private void HydrateIntentsFromOpenJournals()
    {
        var ourExecutionInstrument = (_iea?.ExecutionInstrumentKey ?? _ieaEngineExecutionInstrument ?? "").Trim();
        _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", eventType: "INTENTS_HYDRATION_ATTEMPT", state: "ENGINE",
            new { execution_instrument = ourExecutionInstrument ?? "(empty)", iea_set = _iea != null, note = "Hydration attempt (runs on SetNTContext)" }));
        if (string.IsNullOrEmpty(ourExecutionInstrument))
            return;
        var ourRoot = ourExecutionInstrument.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? ourExecutionInstrument;

        var byInst = _executionJournal.GetOpenJournalEntriesByInstrument();
        var count = 0;
        foreach (var kvp in byInst)
        {
            var journalInstrument = kvp.Key?.Trim() ?? "";
            if (string.IsNullOrEmpty(journalInstrument)) continue;
            var journalRoot = journalInstrument.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? journalInstrument;
            if (!string.Equals(journalRoot, ourRoot, StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var (tradingDate, stream, intentId, entry) in kvp.Value)
            {
                if (IntentMap.ContainsKey(intentId)) continue;
                var intent = CreateIntentFromJournalEntry(tradingDate, stream, intentId, entry);
                if (intent == null) continue;
                var computedId = intent.ComputeIntentId();
                if (!string.Equals(computedId, intentId, StringComparison.Ordinal))
                {
                    _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: tradingDate, eventType: "INTENT_HYDRATION_ID_MISMATCH", state: "CRITICAL",
                        new
                        {
                            filename_intent_id = intentId,
                            computed_intent_id = computedId,
                            stream,
                            instrument = entry.Instrument,
                            note = "Reconstructed Intent hash does not match journal filename id — skipping registration to avoid silent identity drift"
                        }));
                    continue;
                }
                RegisterIntent(intent);
                count++;
            }
        }
        // Also hydrate adoption candidates (EntrySubmitted, !TradeCompleted) so adopted-order fills can resolve IntentMap
        var execVariant = ourExecutionInstrument.StartsWith("M", StringComparison.OrdinalIgnoreCase) && ourExecutionInstrument.Length > 1 ? ourExecutionInstrument : "M" + ourExecutionInstrument;
        var canonical = DeriveCanonicalFromExecutionInstrument(execVariant);
        var adoptionIds = _executionJournal.GetAdoptionCandidateIntentIdsForInstrument(ourExecutionInstrument, canonical);
        foreach (var id in _executionJournal.GetAdoptionCandidateIntentIdsForInstrument(execVariant, canonical))
            adoptionIds.Add(id);
        foreach (var intentId in adoptionIds)
        {
            if (IntentMap.ContainsKey(intentId)) continue;
            var adoptionEntry = _executionJournal.TryGetAdoptionCandidateEntry(intentId, ourExecutionInstrument, canonical)
                ?? _executionJournal.TryGetAdoptionCandidateEntry(intentId, execVariant, canonical);
            if (!adoptionEntry.HasValue) continue;
            var (td, st, ent) = adoptionEntry.Value;
            var intent = CreateIntentFromJournalEntry(td, st, intentId, ent);
            if (intent == null) continue;
            var computedId = intent.ComputeIntentId();
            if (!string.Equals(computedId, intentId, StringComparison.Ordinal))
            {
                _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: td, eventType: "INTENT_HYDRATION_ID_MISMATCH", state: "CRITICAL",
                    new
                    {
                        filename_intent_id = intentId,
                        computed_intent_id = computedId,
                        stream = st,
                        instrument = ent.Instrument,
                        adoption_candidate = true,
                        note = "Reconstructed Intent hash does not match journal id — skipping registration to avoid silent identity drift"
                    }));
                continue;
            }
            RegisterIntent(intent);
            count++;
        }
        if (count > 0)
        {
            _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", eventType: "INTENTS_HYDRATED_FROM_JOURNAL", state: "ENGINE",
                new { intent_count = count, execution_instrument = ourRoot, note = "Re-registered intents from open journals for BE detection and adoption fill resolution" }));
        }
        else if (byInst.Count > 0)
        {
            _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", eventType: "INTENTS_HYDRATION_SKIPPED", state: "ENGINE",
                new { execution_instrument = ourRoot, open_journal_instruments = string.Join(",", byInst.Keys), note = "Hydration ran but no matching open journals for this chart" }));
        }
    }

    /// <summary>
    /// Create Intent from journal entry for hydration. Returns null if required fields missing.
    /// </summary>
    private static Intent? CreateIntentFromJournalEntry(string tradingDate, string stream, string intentId, ExecutionJournalEntry entry)
    {
        if (string.IsNullOrWhiteSpace(tradingDate) || string.IsNullOrWhiteSpace(stream) || string.IsNullOrWhiteSpace(entry.Instrument))
            return null;
        var executionInstrument = entry.Instrument.Trim();
        var canonicalInstrument = DeriveCanonicalFromStream(stream);
        var session = DeriveSessionFromStream(stream);
        var slotTimeChicago = ParseSlotFromOcoGroup(entry.OcoGroup) ?? "09:00";
        var direction = entry.Direction ?? "Long";
        var entryPrice = entry.EntryPrice ?? entry.FillPrice ?? 0;
        var stopPrice = entry.StopPrice ?? entryPrice;
        var targetPrice = entry.TargetPrice ?? entryPrice;
        var beTrigger = ComputeBeTrigger(entryPrice, targetPrice, direction);
        var entryTimeUtc = DateTimeOffset.UtcNow;
        if (!string.IsNullOrEmpty(entry.EntryFilledAtUtc) && DateTimeOffset.TryParse(entry.EntryFilledAtUtc, out var parsed))
            entryTimeUtc = parsed;

        return new Intent(
            tradingDate,
            stream,
            canonicalInstrument,
            executionInstrument,
            session,
            slotTimeChicago,
            direction,
            entryPrice,
            stopPrice,
            targetPrice,
            beTrigger,
            entryTimeUtc,
            "JOURNAL_REHYDRATION");
    }

    private static string DeriveCanonicalFromStream(string stream)
    {
        if (string.IsNullOrEmpty(stream)) return "UNKNOWN";
        var upper = stream.ToUpperInvariant();
        if (upper.StartsWith("RTY")) return "RTY";
        if (upper.StartsWith("YM")) return "YM";
        if (upper.StartsWith("ES")) return "ES";
        if (upper.StartsWith("NQ")) return "NQ";
        if (upper.StartsWith("GC")) return "GC";
        if (upper.StartsWith("NG")) return "NG";
        if (upper.StartsWith("CL")) return "CL";
        return stream.Length >= 2 ? stream.Substring(0, 2).ToUpperInvariant() : "UNKNOWN";
    }

    private static string DeriveSessionFromStream(string stream)
    {
        if (string.IsNullOrEmpty(stream)) return "S1";
        var last = stream[stream.Length - 1];
        return char.IsDigit(last) ? $"S{last}" : "S1";
    }

    private static string? ParseSlotFromOcoGroup(string? ocoGroup)
    {
        if (string.IsNullOrEmpty(ocoGroup)) return null;
        var parts = ocoGroup.Split(':');
        if (parts.Length >= 5 && parts[4].Length >= 5)
            return parts[4];
        return null;
    }

    private static decimal? ComputeBeTrigger(decimal entryPrice, decimal targetPrice, string direction)
    {
        var dist = Math.Abs(targetPrice - entryPrice);
        var bePts = dist * 0.65m;
        return direction == "Long" ? entryPrice + bePts : entryPrice - bePts;
    }
    
    /// <summary>Get account name from NT account object.</summary>
    private static string GetAccountName(object account)
    {
        if (account == null) return "";
        try
        {
            dynamic dyn = account;
            return dyn.Name as string ?? "";
        }
        catch { return ""; }
    }
    
    /// <summary>
    /// Phase 3: Request recovery for instrument. Routes to IEA when bound.
    /// </summary>
    public void RequestRecoveryForInstrument(string instrument, string reason, object context, DateTimeOffset utcNow)
    {
        if (string.IsNullOrEmpty(_ieaAccountName)) return;
        var execKey = ExecutionInstrumentResolver.ResolveExecutionInstrumentKey(_ieaAccountName, instrument, null);
        if (string.IsNullOrEmpty(execKey)) execKey = (instrument ?? "").Trim().ToUpperInvariant();
        if (InstrumentExecutionAuthorityRegistry.TryGet(_ieaAccountName, execKey, out var iea))
            iea.RequestRecovery(instrument, reason, context, utcNow);
    }

    /// <summary>
    /// Phase 5: Request supervisory action for instrument. Routes to IEA when bound.
    /// </summary>
    public void RequestSupervisoryActionForInstrument(string instrument, SupervisoryTriggerReason reason, SupervisorySeverity severity, object? context, DateTimeOffset utcNow)
    {
        if (string.IsNullOrEmpty(_ieaAccountName)) return;
        var execKey = ExecutionInstrumentResolver.ResolveExecutionInstrumentKey(_ieaAccountName, instrument, null);
        if (string.IsNullOrEmpty(execKey)) execKey = (instrument ?? "").Trim().ToUpperInvariant();
        if (InstrumentExecutionAuthorityRegistry.TryGet(_ieaAccountName, execKey, out var iea))
            iea.RequestSupervisoryAction(instrument, reason, severity, context, utcNow);
    }

    /// <summary>
    /// Retry deferred adoption scan when candidates were empty but broker had orders.
    /// Call from periodic path so retry does not depend only on execution updates.
    /// </summary>
    public void TryRetryDeferredAdoptionScan()
    {
        if (_useInstrumentExecutionAuthority && !string.IsNullOrEmpty(_ieaAccountName))
            InstrumentExecutionAuthorityRegistry.RetryDeferredAdoptionScansForAccount(_ieaAccountName!, _log);
        else
            _iea?.TryRetryDeferredAdoptionScanIfDeferred();
    }

    /// <summary>Playback quiescence guard: pending or in-flight IEA work means execution state is not idle yet.</summary>
    public void GetTotalIeaPendingExecutionWorkload(out int pendingTotal, out string pendingInstruments)
    {
        pendingTotal = 0;
        var rows = new List<string>();

        if (_useInstrumentExecutionAuthority && !string.IsNullOrEmpty(_ieaAccountName))
        {
            foreach (var iea in InstrumentExecutionAuthorityRegistry.GetAllForAccount(_ieaAccountName!))
            {
                var count = iea.PendingExecutionWorkloadCount;
                if (count <= 0)
                    continue;

                pendingTotal += count;
                rows.Add($"{iea.ExecutionInstrumentKey}:{count}");
            }
        }
        else if (_iea != null)
        {
            pendingTotal = _iea.PendingExecutionWorkloadCount;
            if (pendingTotal > 0)
                rows.Add($"{_iea.ExecutionInstrumentKey}:{pendingTotal}");
        }

        pendingInstruments = string.Join(",", rows);
    }

    /// <summary>
    /// All IEAs for this instrument: reclassify recoverable UNOWNED + attempt broker-id alias links from snapshot before mismatch assembly.
    /// </summary>
    public void PrepareOrderRegistryForMismatchAssembly(string instrument, AccountSnapshot snap, DateTimeOffset utcNow)
    {
        if (!_useInstrumentExecutionAuthority) return;
        var account = GetCoordinationAccountName();
        if (string.IsNullOrEmpty(account) || string.IsNullOrWhiteSpace(instrument) || snap == null) return;
        var inst = instrument.Trim();
        foreach (var iea in InstrumentExecutionAuthorityRegistry.GetAllForAccount(account))
        {
            if (ExecutionInstrumentResolver.IsSameInstrument(iea.ExecutionInstrumentKey, inst))
                iea.PrepareRegistryForMismatchAssemblyFromSnapshot(inst, snap, utcNow);
        }
    }

    /// <summary>
    /// Enqueue execution command. Forwards to IEA when bound; no-op when IEA not enabled.
    /// Strategy layers should use this instead of calling adapter.Flatten/SubmitOrders/CancelOrders directly.
    /// Enqueues cancel + flatten only; does not drain. Broker-flat confirmation is delivered by
    /// the downstream fill/reconciliation path so callers can return without blocking playback.
    /// </summary>
    public FlattenResult? RequestSessionCloseFlattenImmediate(string intentId, string instrument, DateTimeOffset utcNow)
    {
        if (_ntActionQueue == null || !(this is INtActionExecutor)) return null;
        var cidCancel = $"SESSION_CLOSE_CANCEL:{intentId}:{utcNow:yyyyMMddHHmmssfff}";
        var cidFlatten = $"SESSION_CLOSE_FLATTEN:{intentId}:{utcNow:yyyyMMddHHmmssfff}";
        _ntActionQueue.EnqueueNtAction(new NtCancelOrdersCommand(cidCancel, intentId, instrument, false,
            "FORCED_FLATTEN_CANCEL", utcNow, preferUrgentDrain: true), out _);
        EnqueueNtActionInternal(new NtFlattenInstrumentCommand(cidFlatten, intentId, instrument, "SESSION_FORCED_FLATTEN", utcNow,
            DestructiveActionSource.MANUAL, DestructiveTriggerReason.MANUAL, preferUrgentDrain: true));
        return FlattenResult.FailureResult("SESSION_CLOSE_FLATTEN_ENQUEUED", utcNow);
    }

    /// <summary>
    /// Queue cancel-only session-close cleanup for sibling intents. Used when one broker-level flatten
    /// closes aggregate exposure but several stream intents may own protective orders.
    /// </summary>
    public int RequestSessionCloseCancelIntents(IEnumerable<string> intentIds, string instrument, DateTimeOffset utcNow)
    {
        if (_ntActionQueue == null || !(this is INtActionExecutor)) return 0;

        var count = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in intentIds ?? Array.Empty<string>())
        {
            var intentId = raw?.Trim() ?? "";
            if (string.IsNullOrEmpty(intentId) || !seen.Add(intentId))
                continue;

            var cid = $"SESSION_CLOSE_CANCEL:{intentId}:{utcNow:yyyyMMddHHmmssfff}:{count}";
            _ntActionQueue.EnqueueNtAction(new NtCancelOrdersCommand(cid, intentId, instrument, false,
                "FORCED_FLATTEN_CANCEL", utcNow, preferUrgentDrain: true), out _);
            count++;
        }

        return count;
    }

    /// <summary>
    /// Protective / timer-path emergency flatten: enqueue EMERGENCY NtFlattenInstrumentCommand (strategy thread drain). Safe from any thread.
    /// </summary>
    public void EnqueueEmergencyFlattenProtective(string instrument, DateTimeOffset utcNow)
    {
        var cmd = new NtFlattenInstrumentCommand(
            $"FLATTEN:EMERGENCY:{utcNow:yyyyMMddHHmmssfff}",
            "EMERGENCY_BLOCK",
            instrument ?? "",
            "IEA_BLOCK_EMERGENCY",
            utcNow,
            DestructiveActionSource.EMERGENCY,
            DestructiveTriggerReason.IEA_ENQUEUE_FAILURE,
            preferUrgentDrain: true);
        EnqueueNtActionInternal(cmd);
    }

    /// <inheritdoc />
    public bool TryEnqueueEmergencyFlattenProtective(string instrument, DateTimeOffset utcNow)
    {
        EnqueueEmergencyFlattenProtective(instrument, utcNow);
        return true;
    }

    /// <summary>One best-effort re-enqueue after stale-owner TTL so takeover can admit a critical flatten.</summary>
    private void ScheduleCriticalFlattenCoordinationRetryIfNeeded(NtFlattenInstrumentCommand skippedCmd, string gateContext)
    {
        if (!IsCriticalFlattenCommand(skippedCmd)) return;
        var account = GetCoordinationAccountName();
        var canonical = BrokerPositionResolver.NormalizeCanonicalKey(skippedCmd.Instrument);
        if (string.IsNullOrEmpty(canonical)) return;
        var key = $"{account}|{canonical}";
        if (!_criticalFlattenCoordinationRetryInflight.TryAdd(key, 0)) return;

        var delay = FlattenCoordinationTracker.DefaultStaleOwnerTtl.Add(TimeSpan.FromSeconds(2));
        _ = Task.Delay(delay).ContinueWith(antecedent =>
        {
            try
            {
                var utc = DateTimeOffset.UtcNow;
                var retryCid = $"{skippedCmd.CorrelationId}:COORD_RETRY:{utc:yyyyMMddHHmmssfff}";
                var retry = new NtFlattenInstrumentCommand(
                    retryCid,
                    skippedCmd.IntentId,
                    skippedCmd.Instrument ?? "",
                    skippedCmd.Reason,
                    utc,
                    skippedCmd.DestructiveSource,
                    skippedCmd.ExplicitPolicyTrigger,
                    skippedCmd.AllowAccountWideCancelFallback,
                    skippedCmd.HasRecoveryPolicySeal,
                    skippedCmd.RecoveryPolicySealAllowInstrument,
                    skippedCmd.RecoveryPolicySealCode,
                    skippedCmd.RecoveryPolicySealAttributionScope,
                    skippedCmd.ExplicitCancelBrokerOrderIds,
                    isVerifyRetryFlatten: false);
                EnqueueNtActionInternal(retry);
                _log.Write(RobotEvents.EngineBase(utc, tradingDate: "", eventType: "FLATTEN_COORDINATION_CRITICAL_RETRY_ENQUEUED", state: "ENGINE",
                    new
                    {
                        account,
                        canonical_broker_key = canonical,
                        original_correlation_id = skippedCmd.CorrelationId,
                        gate = gateContext
                    }));
            }
            catch (Exception ex)
            {
                _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", eventType: "FLATTEN_COORDINATION_CRITICAL_RETRY_ERROR", state: "ENGINE",
                    new { error = ex.Message, gate = gateContext }));
            }
            finally
            {
                _criticalFlattenCoordinationRetryInflight.TryRemove(key, out _);
            }
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    // FlattenEmergency: single implementation in NinjaTraderSimAdapter.NT.cs (NT build).

    private static bool IsCriticalFlattenCommand(NtFlattenInstrumentCommand cmd)
    {
        if (cmd == null) return false;
        if (string.Equals(cmd.Reason, "SESSION_FORCED_FLATTEN", StringComparison.OrdinalIgnoreCase))
            return true;
        return cmd.DestructiveSource switch
        {
            DestructiveActionSource.FAIL_CLOSED => true,
            DestructiveActionSource.EMERGENCY => true,
            DestructiveActionSource.RECOVERY => true,
            _ => false
        };
    }

    public void EnqueueExecutionCommand(ExecutionCommandBase command)
    {
        if (command == null) return;
        if (!_useInstrumentExecutionAuthority || _iea == null)
        {
            _log.Write(RobotEvents.ExecutionBase(command.TimestampUtc, command.IntentId ?? "", command.Instrument, "EXECUTION_COMMAND_SKIPPED",
                new { commandId = command.CommandId, reason = "IEA not enabled", commandType = command.GetType().Name }));
            return;
        }
        var instrument = command.Instrument ?? "";
        var execKey = !string.IsNullOrEmpty(_ieaAccountName)
            ? ExecutionInstrumentResolver.ResolveExecutionInstrumentKey(_ieaAccountName, instrument, null)
            : (instrument ?? "").Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(execKey)) execKey = instrument.Trim().ToUpperInvariant();
        if (InstrumentExecutionAuthorityRegistry.TryGet(_ieaAccountName ?? "", execKey, out var iea))
            iea.EnqueueExecutionCommand(command);
        else if (_iea != null)
            _iea.EnqueueExecutionCommand(command);
    }

    /// <summary>
    /// Get IEA identity for CRITICAL event payloads (when IEA enabled).
    /// </summary>
    private object? GetIeaIdentityForCriticalEvents()
    {
        if (!_useInstrumentExecutionAuthority || _iea == null) return null;
        return new { iea_instance_id = _iea.InstanceId, execution_instrument_key = _iea.ExecutionInstrumentKey, account_name = _iea.AccountName };
    }

    /// <summary>
    /// Gap 6: Ensure CRITICAL events include iea_instance_id when IEA enabled. Invariant: no CRITICAL without IEA context.
    /// </summary>
    internal void LogCriticalWithIeaContext(DateTimeOffset utcNow, string intentId, string instrument, string eventType, object data)
    {
        var payload = _iea != null ? MergeIeaContext(data, _iea) : data;
        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, eventType, payload));
    }

    /// <summary>
    /// Gap 6: EngineBase variant for CRITICAL events.
    /// </summary>
    internal void LogCriticalEngineWithIeaContext(DateTimeOffset utcNow, string tradingDate, string eventType, string state, object data)
    {
        var payload = _iea != null ? MergeIeaContext(data, _iea) : data;
        _log.Write(RobotEvents.EngineBase(utcNow, tradingDate, eventType, state, payload));
    }

    private static System.Collections.Generic.Dictionary<string, object> MergeIeaContext(object data, InstrumentExecutionAuthority iea)
    {
        var dict = new System.Collections.Generic.Dictionary<string, object>();
        if (data != null)
        {
            foreach (var p in data.GetType().GetProperties())
                dict[p.Name] = p.GetValue(data);
        }
        dict["iea_instance_id"] = iea.InstanceId;
        dict["execution_instrument_key"] = iea.ExecutionInstrumentKey;
        return dict;
    }

    /// <summary>
    /// Canonical fill: Get next monotonic execution_sequence.
    /// Scope: Per execution_instrument_key (NOT per strategy instance, NOT per stream, NOT global across instruments).
    /// Multi-account: key = account|execution_instrument_key to avoid cross-account collision.
    /// Must be reproducible under replay.
    /// </summary>
    internal static int GetNextExecutionSequence(string executionInstrumentKey, string? accountName = null)
    {
        var key = string.IsNullOrEmpty(executionInstrumentKey) ? "_default" : executionInstrumentKey;
        if (!string.IsNullOrEmpty(accountName))
            key = accountName + "|" + key;
        lock (_executionSequenceLock)
        {
            if (!_executionSequenceByKey.TryGetValue(key, out var seq))
                seq = 0;
            seq++;
            _executionSequenceByKey[key] = seq;
            return seq;
        }
    }

    /// <summary>
    /// Canonical fill: Deterministic fill_group_id. Use broker execution id when available; else hash.
    /// Must be reproducible under replay (no random UUID).
    /// </summary>
    internal static string ComputeFillGroupId(string? brokerExecutionId, string orderId, string brokerOrderId, string timestampUtc, decimal fillPrice, int fillQty)
    {
        if (!string.IsNullOrWhiteSpace(brokerExecutionId))
            return brokerExecutionId;
        var input = $"{orderId}|{brokerOrderId}|{timestampUtc}|{fillPrice}|{fillQty}";
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
        var hex = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        return hex.Length >= 16 ? hex.Substring(0, 16) : hex;
    }

    /// <summary>
    /// Set whether to use Instrument Execution Authority (IEA).
    /// Must be called before SetNTContext. Called by RobotEngine when execution policy has use_instrument_execution_authority.
    /// </summary>
    public void SetUseInstrumentExecutionAuthority(bool use)
    {
        _useInstrumentExecutionAuthority = use;
    }

    /// <summary>
    /// Phase 2: Set aggregation/bracket policy for IEA. Must be called before SetNTContext when IEA is enabled.
    /// </summary>
    public void SetAggregationPolicy(AggregationPolicy? policy)
    {
        _aggregationPolicy = policy;
    }

}
