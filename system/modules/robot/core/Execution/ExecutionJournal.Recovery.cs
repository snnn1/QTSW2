using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using QTSW2.Robot.Core;

namespace QTSW2.Robot.Core.Execution;

public sealed partial class ExecutionJournal
{
    public (string TradingDate, string Stream, ExecutionJournalEntry Entry)? TryGetAdoptionCandidateEntry(string intentId, string executionInstrument, string? canonicalInstrument = null)
    {
        if (string.IsNullOrWhiteSpace(intentId)) return null;
        string[] files;
        try { files = Directory.GetFiles(_journalDir, "*.json"); }
        catch { return null; }
        foreach (var path in files)
        {
            try
            {
                var fileName = Path.GetFileNameWithoutExtension(path);
                var parts = fileName.Split('_');
                if (parts.Length < 3) continue;
                var fileIntentId = parts[parts.Length - 1];
                if (!string.Equals(fileIntentId, intentId, StringComparison.OrdinalIgnoreCase)) continue;
                var tradingDate = parts[0];
                var stream = string.Join("_", parts.Skip(1).Take(parts.Length - 2));
                ExecutionJournalEntry? entry;
                lock (_lock)
                {
                    var json = ReadJournalFileWithRetry(path);
                    if (json == null) continue;
                    entry = JsonUtil.Deserialize<ExecutionJournalEntry>(json);
                }
                if (entry == null || !entry.EntrySubmitted || entry.TradeCompleted) continue;
                if (IsUntrackedFillRecoveryMarker(entry)) continue;
                if (!JournalInstrumentMatchesExecutionKey(entry.Instrument, executionInstrument, canonicalInstrument))
                    continue;
                return (tradingDate, stream, entry);
            }
            catch { /* skip */ }
        }
        return null;
    }

    private static string BuildUntrackedFillRecoveryIntentId(string instrumentKey, string brokerOrderId)
    {
        var inst = NormalizeJournalInstrumentSymbol(string.IsNullOrWhiteSpace(instrumentKey) ? "UNKNOWN" : instrumentKey);
        var bo = string.IsNullOrWhiteSpace(brokerOrderId) ? "UNKNOWN" : brokerOrderId.Trim();
        var payload = $"{inst}\u001f{bo}";
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(payload));
        var sb = new StringBuilder(20);
        sb.Append("UNTR");
        for (var i = 0; i < 8; i++)
            sb.Append(hash[i].ToString("x2"));
        return sb.ToString();
    }

    private static int ResolveBrokerAbsQtyForJournalInstrument(string journalInstrumentKey,
        IReadOnlyDictionary<string, int> accountQtyByInstrument)
    {
        if (accountQtyByInstrument == null || accountQtyByInstrument.Count == 0) return 0;
        var target = BrokerPositionResolver.NormalizeCanonicalKey(journalInstrumentKey);
        if (string.IsNullOrEmpty(target)) return 0;
        var sum = 0;
        foreach (var kv in accountQtyByInstrument)
        {
            if (string.Equals(BrokerPositionResolver.NormalizeCanonicalKey(kv.Key), target, StringComparison.OrdinalIgnoreCase))
                sum += kv.Value;
        }
        return sum;
    }

    /// <summary>
    /// Durable journal-backed marker when a fill cannot be attributed to an intent (fail-closed flatten still runs separately).
    /// Keeps reconciliation from settling into broker-open / journal-zero until the broker is flat or this row is completed.
    /// </summary>
    public void UpsertUntrackedFillRecoveryJournal(
        string instrumentKey,
        string brokerOrderId,
        int fillQuantitySigned,
        decimal fillPrice,
        DateTimeOffset utcNow,
        string? correlationId = null)
    {
        var absQty = Math.Max(1, Math.Abs(fillQuantitySigned));
        var intentId = BuildUntrackedFillRecoveryIntentId(instrumentKey, brokerOrderId);
        var tradingDate = UntrackedFillRecoveryTradingDateBucket;
        var stream = UntrackedFillRecoveryStream;
        var key = $"{tradingDate}_{stream}_{intentId}";
        var journalPath = GetJournalPath(tradingDate, stream, intentId);
        var inst = string.IsNullOrWhiteSpace(instrumentKey) ? "UNKNOWN" : instrumentKey.Trim();

        lock (_lock)
        {
            ExecutionJournalEntry? entry = null;
            if (_cache.TryGetValue(key, out var cached))
                entry = cached;
            else if (File.Exists(journalPath))
            {
                var json = ReadJournalFileWithRetry(journalPath);
                if (json != null)
                    entry = JsonUtil.Deserialize<ExecutionJournalEntry>(json);
            }

            entry ??= new ExecutionJournalEntry
            {
                IntentId = intentId,
                TradingDate = tradingDate,
                Stream = stream,
                Instrument = inst
            };

            entry.IntentId = intentId;
            entry.TradingDate = tradingDate;
            entry.Stream = stream;
            if (string.IsNullOrWhiteSpace(entry.Instrument)) entry.Instrument = inst;

            HydrateCanonicalTimestampsFromLegacy(entry);
            var uRec = utcNow.ToString("o");
            entry.EntrySubmitted = true;
            entry.EntrySubmittedObservedAtUtc = uRec;
            entry.EntryFilledObservedAtUtc = uRec;
            entry.IsReconstructedSubmission = true;
            if (string.IsNullOrEmpty(entry.EntrySubmittedAtUtc))
                entry.EntrySubmittedAtUtc = uRec;
            entry.EntrySubmittedAt = entry.EntrySubmittedAtUtc;
            entry.EntryFilled = true;
            if (string.IsNullOrEmpty(entry.EntryFilledAtUtc))
                entry.EntryFilledAtUtc = uRec;
            entry.EntryFilledAt = entry.EntryFilledAtUtc;
            entry.EntryOrderType = "UNTRACKED_FILL_RECOVERY";
            entry.BrokerOrderId = brokerOrderId;
            entry.EntryFilledQuantityTotal = Math.Max(entry.EntryFilledQuantityTotal, absQty);
            entry.FillPrice = fillPrice;
            entry.ActualFillPrice = fillPrice;
            entry.FillQuantity = entry.EntryFilledQuantityTotal;

            if (fillQuantitySigned != 0)
                entry.Direction = fillQuantitySigned > 0 ? "Long" : "Short";
            else if (string.IsNullOrWhiteSpace(entry.Direction))
                entry.Direction = "UNTRACKED";

            entry.TradeCompleted = false;

            _cache[key] = entry;
            SaveJournal(journalPath, entry);
            BumpReleaseSuppressionActivity();
        }

        _log.Write(RobotEvents.EngineBase(utcNow, tradingDate, "UNTRACKED_FILL_RECOVERY_JOURNAL_UPSERT", "ENGINE",
            new
            {
                instrument = inst,
                intent_id = intentId,
                broker_order_id = brokerOrderId,
                open_journal_qty = absQty,
                correlation_id = correlationId
            }));
    }

    /// <summary>
    /// Deterministic synthetic intent when broker shows open exposure not fully represented in the journal (integrity layer).
    /// Does not submit orders; adoption index excludes recovered rows.
    /// </summary>
    public RecoveredIntentUpsertResult UpsertRecoveredIntentForBrokerIntegrity(
        string executionInstrumentPrimary,
        string? canonicalInstrument,
        AccountSnapshot snapshot,
        string tradingDate,
        DateTimeOffset utcNow)
    {
        if (string.IsNullOrWhiteSpace(tradingDate) || snapshot == null)
            return new RecoveredIntentUpsertResult();

        var exec = executionInstrumentPrimary.Trim();
        var canon = string.IsNullOrWhiteSpace(canonicalInstrument) ? exec : canonicalInstrument.Trim();
        var instKey = string.IsNullOrWhiteSpace(exec) ? "UNKNOWN" : exec;

        var brokerPositionQtyAbs = SumAbsBrokerPositionForInstrument(snapshot, exec);
        var brokerPositionSignedNet = SumNetBrokerPositionSignedForInstrument(snapshot, exec);

        if (brokerPositionQtyAbs == 0)
            return CloseRecoveredIntegrityRowsWhenBrokerFlat(exec, canon, utcNow);

        if (brokerPositionQtyAbs > 0 && brokerPositionSignedNet == 0)
        {
            return new RecoveredIntentUpsertResult
            {
                Conflict = true,
                ConflictReason = "hedged_or_offsetting_positions_net_zero"
            };
        }

        var brokerSign = brokerPositionSignedNet > 0 ? 1 : -1;
        var dirStr = brokerSign > 0 ? "Long" : "Short";
        var avgPx = TryWeightedAveragePriceForInstrument(snapshot, exec);
        var defaultRecoveredIntentId = BuildRecoveredIntegrityIntentId(exec);
        var stream = RecoveredIntentStream;
        var uIso = utcNow.ToString("o");

        lock (_lock)
        {
            var openMap = GetOpenJournalEntriesByInstrument();
            var nonRecoveredSum = 0;
            var conflictingDirection = false;
            var recoveredTuples = new List<(string Td, string St, string Iid, ExecutionJournalEntry Entry)>();
            foreach (var kvp in openMap)
            {
                if (!OpenJournalMapBucketMatches(kvp.Key, exec, canon)) continue;
                foreach (var (td, st, iid, row) in kvp.Value)
                {
                    var rem = GetEntryRemainingOpenQuantity(row);
                    if (rem <= 0) continue;
                    if (IsRecoveredIntegrityJournalEntry(row))
                    {
                        recoveredTuples.Add((td, st, iid, row));
                        continue;
                    }

                    nonRecoveredSum += rem;
                    var js = DirectionSignFromJournalDirection(row.Direction);
                    if (js != 0 && js != brokerSign)
                        conflictingDirection = true;
                }
            }

            if (conflictingDirection)
                return new RecoveredIntentUpsertResult { Conflict = true, ConflictReason = "direction_mismatch_non_recovered" };

            if (nonRecoveredSum > brokerPositionQtyAbs)
                return new RecoveredIntentUpsertResult { Conflict = true, ConflictReason = "journal_open_exceeds_broker" };

            var targetRecoveredRemaining = brokerPositionQtyAbs - nonRecoveredSum;
            if (targetRecoveredRemaining < 0)
                targetRecoveredRemaining = 0;

            var writes = 0;
            if (recoveredTuples.Count > 1)
            {
                for (var i = 1; i < recoveredTuples.Count; i++)
                {
                    var (td, st, rid, ent) = recoveredTuples[i];
                    var pth = GetJournalPath(td, st, rid);
                    ent.TradeCompleted = true;
                    ent.CompletionReason = CompletionReasons.RECONCILIATION_BROKER_FLAT;
                    ent.CompletedAtUtc = uIso;
                    _cache[$"{td}_{st}_{rid}"] = ent;
                    SaveJournal(pth, ent);
                    writes++;
                }
            }

            if (targetRecoveredRemaining == 0)
            {
                foreach (var t in recoveredTuples.Take(1))
                {
                    var ent = t.Entry;
                    var pth = GetJournalPath(t.Td, t.St, t.Iid);
                    ent.ExitFilledQuantityTotal = ent.EntryFilledQuantityTotal;
                    ent.TradeCompleted = true;
                    ent.CompletionReason = CompletionReasons.RECONCILIATION_BROKER_FLAT;
                    ent.CompletedAtUtc = uIso;
                    _cache[$"{t.Td}_{t.St}_{t.Iid}"] = ent;
                    SaveJournal(pth, ent);
                    writes++;
                }

                return new RecoveredIntentUpsertResult { Writes = writes };
            }

            string useTd;
            string useSt;
            string useIid;
            ExecutionJournalEntry entry;
            string journalPath;
            string cacheKey;

            if (recoveredTuples.Count > 0)
            {
                var t0 = recoveredTuples[0];
                useTd = t0.Td;
                useSt = t0.St;
                useIid = t0.Iid;
                entry = t0.Entry;
                cacheKey = $"{useTd}_{useSt}_{useIid}";
                journalPath = GetJournalPath(useTd, useSt, useIid);
            }
            else
            {
                useTd = tradingDate;
                useSt = stream;
                useIid = defaultRecoveredIntentId;
                cacheKey = $"{useTd}_{useSt}_{useIid}";
                journalPath = GetJournalPath(useTd, useSt, useIid);
                if (_cache.TryGetValue(cacheKey, out var cached))
                    entry = cached;
                else if (File.Exists(journalPath))
                {
                    var json = ReadJournalFileWithRetry(journalPath);
                    entry = json != null ? JsonUtil.Deserialize<ExecutionJournalEntry>(json) : null;
                }
                else
                    entry = null;

                entry ??= new ExecutionJournalEntry
                {
                    IntentId = useIid,
                    TradingDate = useTd,
                    Stream = useSt,
                    Instrument = instKey
                };
            }

            var isUpdate = recoveredTuples.Count > 0;

            entry.IntentId = useIid;
            entry.TradingDate = useTd;
            entry.Stream = useSt;
            entry.Instrument = instKey;
            entry.IntentType = IntentTypeRecovered;
            entry.RecoverySource = RecoverySourceBrokerSnapshot;
            entry.RecoveryTimestampUtc = uIso;
            entry.IsRecovered = true;
            entry.OriginalIntentId = null;
            entry.IsReconstructedSubmission = true;
            entry.EntryOrderType = "INTEGRITY_RECOVERED";
            entry.BrokerOrderId = null;

            HydrateCanonicalTimestampsFromLegacy(entry);
            entry.EntrySubmitted = true;
            entry.EntrySubmittedObservedAtUtc = uIso;
            if (string.IsNullOrEmpty(entry.EntrySubmittedAtUtc))
                entry.EntrySubmittedAtUtc = uIso;
            entry.EntrySubmittedAt = entry.EntrySubmittedAtUtc;
            entry.EntryFilled = true;
            if (string.IsNullOrEmpty(entry.EntryFilledAtUtc))
                entry.EntryFilledAtUtc = uIso;
            entry.EntryFilledAt = entry.EntryFilledAtUtc;
            entry.EntryFilledObservedAtUtc = uIso;
            entry.Direction = dirStr;

            var exitPrev = entry.ExitFilledQuantityTotal;
            entry.EntryFilledQuantityTotal = targetRecoveredRemaining + exitPrev;
            entry.RecoveredQuantity = targetRecoveredRemaining;
            entry.RecoveredPrice = avgPx;
            entry.FillPrice = avgPx;
            entry.ActualFillPrice = avgPx;
            entry.EntryAvgFillPrice = avgPx;
            entry.FillQuantity = targetRecoveredRemaining;
            entry.EntryFillNotional = avgPx.HasValue ? avgPx.Value * targetRecoveredRemaining : null;
            entry.TradeCompleted = false;

            _cache[cacheKey] = entry;
            SaveJournal(journalPath, entry);
            SyncAdoptionCandidateIndexForIntentLocked(useIid, entry);
            writes++;
            BumpReleaseSuppressionActivity();

            _log.Write(RobotEvents.EngineBase(utcNow, useTd, "RECOVERED_INTENT_CREATED", "ENGINE",
                new
                {
                    instrument = instKey,
                    intent_id = useIid,
                    stream = useSt,
                    is_update = isUpdate,
                    recovered_remaining = targetRecoveredRemaining,
                    broker_abs = brokerPositionQtyAbs,
                    non_recovered_open_sum = nonRecoveredSum,
                    direction = dirStr,
                    recovered_price = avgPx,
                    note = "journal_integrity_synthetic_row"
                }));

            return new RecoveredIntentUpsertResult { Writes = writes };
        }
    }

    /// <summary>
    /// When non-recovered open quantity equals broker abs exposure and at least one integrity-recovered row is still open,
    /// fully close all recovered rows for the instrument (superseded by real strategy journal rows).
    /// Does not run when <paramref name="brokerPositionQtyAbs"/> is 0 (broker-flat paths use existing logic).
    /// </summary>
    /// <returns>Number of recovered journal rows written as completed.</returns>
    public int CloseRecoveredRowsSupersededByRealExposure(
        string executionInstrumentPrimary,
        string? canonicalInstrument,
        int brokerPositionQtyAbs,
        DateTimeOffset utcNow)
    {
        if (brokerPositionQtyAbs <= 0)
            return 0;

        var exec = executionInstrumentPrimary.Trim();
        var canon = string.IsNullOrWhiteSpace(canonicalInstrument) ? exec : canonicalInstrument.Trim();
        var uIso = utcNow.ToString("o");
        lock (_lock)
        {
            var openMap = GetOpenJournalEntriesByInstrument();
            var nonRecoveredOpenSum = 0;
            var recoveredOpen = new List<(string Td, string St, string Iid, ExecutionJournalEntry Entry)>();
            foreach (var kvp in openMap)
            {
                if (!OpenJournalMapBucketMatches(kvp.Key, exec, canon)) continue;
                foreach (var (td, st, iid, entry) in kvp.Value)
                {
                    var rem = GetEntryRemainingOpenQuantity(entry);
                    if (rem <= 0) continue;
                    if (IsRecoveredIntegrityJournalEntry(entry))
                        recoveredOpen.Add((td, st, iid, entry));
                    else
                        nonRecoveredOpenSum += rem;
                }
            }

            if (recoveredOpen.Count == 0)
                return 0;
            if (nonRecoveredOpenSum != brokerPositionQtyAbs)
                return 0;

            var writes = 0;
            string? logTd = null;
            foreach (var (td, st, iid, entry) in recoveredOpen)
            {
                logTd ??= td;
                var pth = GetJournalPath(td, st, iid);
                entry.ExitFilledQuantityTotal = entry.EntryFilledQuantityTotal;
                entry.ExitFilledAtUtc = uIso;
                entry.ExitOrderType = CompletionReasons.RECONCILIATION_RECOVERED_SUPERSEDED_BY_REAL;
                entry.TradeCompleted = true;
                entry.CompletionReason = CompletionReasons.RECONCILIATION_RECOVERED_SUPERSEDED_BY_REAL;
                entry.CompletedAtUtc = uIso;
                _cache[$"{td}_{st}_{iid}"] = entry;
                SaveJournal(pth, entry);
                SyncAdoptionCandidateIndexForIntentLocked(iid, entry);
                writes++;
                BumpReleaseSuppressionActivity();
            }

            _log.Write(RobotEvents.EngineBase(utcNow, logTd ?? "", "RECONCILIATION_RECOVERED_ROW_CLOSED_SUPERSEDED", "ENGINE",
                new
                {
                    instrument = exec,
                    broker_qty = brokerPositionQtyAbs,
                    non_recovered_open_qty = nonRecoveredOpenSum,
                    recovered_rows_closed_count = writes,
                    note = "Recovered rows closed because real strategy exposure matches broker"
                }));
            return writes;
        }
    }

    private RecoveredIntentUpsertResult CloseRecoveredIntegrityRowsWhenBrokerFlat(
        string executionInstrumentPrimary,
        string? canonicalInstrument,
        DateTimeOffset utcNow)
    {
        var exec = executionInstrumentPrimary.Trim();
        var canon = string.IsNullOrWhiteSpace(canonicalInstrument) ? exec : canonicalInstrument.Trim();
        var uIso = utcNow.ToString("o");
        var writes = 0;
        lock (_lock)
        {
            var openMap = GetOpenJournalEntriesByInstrument();
            foreach (var kvp in openMap)
            {
                if (!OpenJournalMapBucketMatches(kvp.Key, exec, canon)) continue;
                foreach (var (td, st, iid, entry) in kvp.Value)
                {
                    if (!IsRecoveredIntegrityJournalEntry(entry)) continue;
                    var rem = GetEntryRemainingOpenQuantity(entry);
                    if (rem <= 0) continue;
                    var pth = GetJournalPath(td, st, iid);
                    entry.ExitFilledQuantityTotal = entry.EntryFilledQuantityTotal;
                    entry.TradeCompleted = true;
                    entry.CompletionReason = CompletionReasons.RECONCILIATION_BROKER_FLAT;
                    entry.CompletedAtUtc = uIso;
                    _cache[$"{td}_{st}_{iid}"] = entry;
                    SaveJournal(pth, entry);
                    writes++;
                }
            }
        }

        return new RecoveredIntentUpsertResult { Writes = writes };
    }

    /// <summary>
    /// Robot-tagged exposure reconciliation: persist an open filled journal row when broker shows position but
    /// normal <see cref="RecordEntryFill"/> did not (lifecycle/fill path failure). Distinct from untracked-fill recovery.
    /// Uses the intent's real trading_date + stream + intent_id key so protectives and reconciliation align.
    /// </summary>
    public void UpsertTaggedBrokerExposureRecoveryJournal(
        string intentId,
        string tradingDate,
        string stream,
        string executionInstrument,
        string? brokerOrderId,
        int openQtyAbs,
        string direction,
        decimal? avgFillPrice,
        decimal? stopPrice,
        decimal? targetPrice,
        DateTimeOffset utcNow,
        string? correlationId = null)
    {
        if (string.IsNullOrWhiteSpace(intentId) || string.IsNullOrWhiteSpace(tradingDate) ||
            string.IsNullOrWhiteSpace(stream) || openQtyAbs <= 0)
            return;
        var inst = string.IsNullOrWhiteSpace(executionInstrument) ? "UNKNOWN" : executionInstrument.Trim();
        var key = $"{tradingDate}_{stream}_{intentId}";
        var journalPath = GetJournalPath(tradingDate, stream, intentId);
        var dRaw = direction?.Trim() ?? "";
        var normDir = dRaw.Length == 0 ? ""
            : (dRaw.Length == 1 ? dRaw.ToUpperInvariant()
                : char.ToUpperInvariant(dRaw[0]) + dRaw.Substring(1).ToLowerInvariant());

        var didNormalizeReopen = false;
        var logPriorEntry = 0;
        var logPriorExit = 0;
        var logNewExit = 0;
        string? logPriorCompletionReason = null;
        var finalEntryQty = 0;
        var finalExitQty = 0;
        var finalRemainingAfterUpsert = 0;
        var finalTradeCompletedAfterUpsert = false;
        string? finalDirection = null;
        var didHydrateMultiplier = false;
        decimal? hydratedMultiplier = null;

        lock (_lock)
        {
            ExecutionJournalEntry? entry = null;
            if (_cache.TryGetValue(key, out var cached))
                entry = cached;
            else if (File.Exists(journalPath))
            {
                var json = ReadJournalFileWithRetry(journalPath);
                if (json != null)
                    entry = JsonUtil.Deserialize<ExecutionJournalEntry>(json);
            }

            entry ??= new ExecutionJournalEntry
            {
                IntentId = intentId,
                TradingDate = tradingDate,
                Stream = stream,
                Instrument = inst
            };

            entry.IntentId = intentId;
            entry.TradingDate = tradingDate;
            entry.Stream = stream;
            if (string.IsNullOrWhiteSpace(entry.Instrument)) entry.Instrument = inst;

            HydrateCanonicalTimestampsFromLegacy(entry);
            var tRec = utcNow.ToString("o");
            entry.EntrySubmitted = true;
            entry.EntrySubmittedObservedAtUtc = tRec;
            entry.EntryFilledObservedAtUtc = tRec;
            entry.EntryFilled = true;

            if (TryParseJournalIsoUtc(entry.EntryFilledAtUtc, out var priorFillCanon) && utcNow > priorFillCanon)
            {
                entry.IsReconstructedSubmission = true;
                _log.Write(RobotEvents.EngineBase(utcNow, tradingDate, "JOURNAL_LATE_SUBMISSION_OBSERVATION", "ENGINE",
                    new
                    {
                        intent_id = intentId,
                        stream,
                        phase = "TAGGED_BROKER_RECOVERY_SUBMIT_OBSERVED_AFTER_KNOWN_ENTRY_FILL",
                        entry_filled_at_utc_canonical = entry.EntryFilledAtUtc,
                        recovery_observed_at_utc = tRec,
                        note = "Not stamping EntrySubmittedAtUtc from recovery observation when fill canonical precedes it."
                    }));
                if (!string.IsNullOrEmpty(entry.EntrySubmittedAtUtc))
                    entry.EntrySubmittedAt = entry.EntrySubmittedAtUtc;
            }
            else
            {
                if (string.IsNullOrEmpty(entry.EntrySubmittedAtUtc))
                    entry.EntrySubmittedAtUtc = tRec;
                entry.EntrySubmittedAt = entry.EntrySubmittedAtUtc;
            }

            if (string.IsNullOrEmpty(entry.EntryFilledAtUtc))
                entry.EntryFilledAtUtc = tRec;
            entry.EntryFilledAt = entry.EntryFilledAtUtc;
            entry.EntryOrderType = "TAGGED_BROKER_EXPOSURE_RECOVERY";
            if (!string.IsNullOrEmpty(brokerOrderId))
                entry.BrokerOrderId = brokerOrderId;

            entry.EntryFilledQuantityTotal = Math.Max(entry.EntryFilledQuantityTotal, openQtyAbs);
            if (avgFillPrice.HasValue)
            {
                entry.EntryAvgFillPrice = avgFillPrice;
                entry.FillPrice = avgFillPrice;
                entry.ActualFillPrice = avgFillPrice;
            }
            if (!entry.ContractMultiplier.HasValue)
            {
                hydratedMultiplier = ResolveContractMultiplierFromSiblingJournalLocked(tradingDate, stream, inst, intentId);
                if (hydratedMultiplier.HasValue)
                {
                    entry.ContractMultiplier = hydratedMultiplier;
                    didHydrateMultiplier = true;
                }
            }
            if (entry.EntryAvgFillPrice.HasValue && entry.EntryFilledQuantityTotal > 0 &&
                (!entry.EntryFillNotional.HasValue || entry.EntryFillNotional.Value <= 0m))
            {
                entry.EntryFillNotional = entry.EntryAvgFillPrice.Value * entry.EntryFilledQuantityTotal;
            }
            entry.FillQuantity = entry.EntryFilledQuantityTotal;
            if (!string.IsNullOrEmpty(normDir))
                entry.Direction = normDir;
            if (stopPrice.HasValue && !entry.StopPrice.HasValue)
                entry.StopPrice = stopPrice;
            if (targetPrice.HasValue && !entry.TargetPrice.HasValue)
                entry.TargetPrice = targetPrice;

            logPriorEntry = entry.EntryFilledQuantityTotal;
            logPriorExit = entry.ExitFilledQuantityTotal;
            logPriorCompletionReason = entry.CompletionReason;
            var priorTradeCompleted = entry.TradeCompleted;
            var numericallyFullyExited = logPriorEntry > 0 && logPriorExit >= logPriorEntry;

            // Reopening for live broker exposure must not leave terminal exit cumulatives from a prior cycle.
            if (priorTradeCompleted || numericallyFullyExited)
            {
                didNormalizeReopen = true;
                entry.ExitFilledQuantityTotal = 0;
                entry.ExitOrderType = null;
                entry.ExitAvgFillPrice = null;
                entry.ExitFillNotional = null;
                entry.ExitFilledAtUtc = null;
                entry.ExitFilledObservedAtUtc = null;
                entry.CompletedAtUtc = null;
                entry.CompletionReason = null;
                entry.RealizedPnLGross = null;
                entry.RealizedPnLNet = null;
                entry.RealizedPnLPoints = null;
                logNewExit = 0;
            }
            else
            {
                logNewExit = entry.ExitFilledQuantityTotal;
            }

            // Post-upsert open-state invariant: GetOpenJournalEntriesByInstrument requires
            // EntryFilled && !TradeCompleted && EntryFilledQuantityTotal > 0. Tagged-broker
            // recovery must never leave exit totals or TradeCompleted in a state that hides
            // a live broker-open row from aggregation/rehydration.
            entry.EntryFilled = true;
            entry.EntryFilledQuantityTotal = Math.Max(entry.EntryFilledQuantityTotal, openQtyAbs);
            if (entry.EntryFilledQuantityTotal > 0 &&
                entry.ExitFilledQuantityTotal > entry.EntryFilledQuantityTotal)
            {
                entry.ExitFilledQuantityTotal = entry.EntryFilledQuantityTotal;
            }

            var remainingQty = entry.EntryFilledQuantityTotal - entry.ExitFilledQuantityTotal;
            if (openQtyAbs > 0 && remainingQty <= 0 && entry.EntryFilledQuantityTotal > 0)
            {
                entry.ExitFilledQuantityTotal = Math.Max(0, entry.EntryFilledQuantityTotal - openQtyAbs);
                if (entry.EntryFilledQuantityTotal > entry.ExitFilledQuantityTotal)
                {
                    entry.ExitOrderType = null;
                    entry.ExitAvgFillPrice = null;
                    entry.ExitFillNotional = null;
                    entry.ExitFilledAtUtc = null;
                    entry.ExitFilledObservedAtUtc = null;
                    entry.CompletedAtUtc = null;
                    entry.CompletionReason = null;
                    entry.RealizedPnLGross = null;
                    entry.RealizedPnLNet = null;
                    entry.RealizedPnLPoints = null;
                }
            }

            if (entry.EntryFilledQuantityTotal > entry.ExitFilledQuantityTotal)
            {
                entry.TradeCompleted = false;
                entry.CompletedAtUtc = null;
                entry.CompletionReason = null;
            }

            entry.FillQuantity = entry.EntryFilledQuantityTotal;
            finalEntryQty = entry.EntryFilledQuantityTotal;
            finalExitQty = entry.ExitFilledQuantityTotal;
            finalDirection = entry.Direction;
            finalRemainingAfterUpsert = finalEntryQty - finalExitQty;
            finalTradeCompletedAfterUpsert = entry.TradeCompleted;

            _cache[key] = entry;
            SaveJournal(journalPath, entry);
            BumpReleaseSuppressionActivity();
        }

        if (didNormalizeReopen)
        {
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate, "JOURNAL_REOPEN_NORMALIZED", "ENGINE",
                new
                {
                    intent_id = intentId,
                    prior_entry_qty = logPriorEntry,
                    prior_exit_qty = logPriorExit,
                    new_exit_qty = logNewExit,
                    prior_completion_reason = logPriorCompletionReason,
                    broker_open_qty = openQtyAbs,
                    note = "Tagged broker recovery reopened row; cleared terminal exit-side state so journal open qty is not falsely exhausted"
                }));
        }

        if (didHydrateMultiplier)
        {
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate, "TAGGED_BROKER_RECOVERY_MULTIPLIER_HYDRATED", "ENGINE",
                new
                {
                    intent_id = intentId,
                    instrument = inst,
                    stream,
                    contract_multiplier = hydratedMultiplier,
                    correlation_id = correlationId,
                    note = "Tagged broker recovery journal hydrated contract multiplier from same-date sibling journal evidence"
                }));
        }

        _log.Write(RobotEvents.EngineBase(utcNow, tradingDate, "JOURNAL_ROW_NORMALIZED_OPEN_AFTER_UPSERT", "ENGINE",
            new
            {
                intent_id = intentId,
                instrument = inst,
                entry_qty = finalEntryQty,
                exit_qty = finalExitQty,
                remaining = finalRemainingAfterUpsert,
                trade_completed = finalTradeCompletedAfterUpsert,
                correlation_id = correlationId
            }));

        _log.Write(RobotEvents.EngineBase(utcNow, tradingDate, "TAGGED_BROKER_EXPOSURE_RECOVERY_JOURNAL_UPSERT", "ENGINE",
            new
            {
                instrument = inst,
                intent_id = intentId,
                broker_order_id = brokerOrderId,
                open_journal_qty = openQtyAbs,
                correlation_id = correlationId,
                note = "TAGGED_ORPHAN_POSITION_RECOVERY — sibling to UNTRACKED; uses real stream/date"
            }));

        if (finalEntryQty > finalExitQty)
        {
            var dirForRehydration = string.IsNullOrWhiteSpace(finalDirection)
                ? (direction?.Trim() ?? "Long")
                : finalDirection!;
            _onTaggedBrokerJournalRehydrationCallback?.Invoke(
                intentId, tradingDate, stream, inst, dirForRehydration, finalEntryQty, finalExitQty, utcNow);
        }
    }

    private decimal? ResolveContractMultiplierFromSiblingJournalLocked(
        string tradingDate,
        string stream,
        string instrument,
        string intentId)
    {
        var cachedEntries = _cache.Values.ToList();
        var fileEntries = ReadJournalEntriesForMultiplierLookupLocked();

        return TryResolveContractMultiplierFromEntries(cachedEntries, tradingDate, stream, instrument, intentId, requireSameStream: true) ??
               TryResolveContractMultiplierFromEntries(fileEntries, tradingDate, stream, instrument, intentId, requireSameStream: true) ??
               TryResolveContractMultiplierFromEntries(cachedEntries, tradingDate, stream, instrument, intentId, requireSameStream: false) ??
               TryResolveContractMultiplierFromEntries(fileEntries, tradingDate, stream, instrument, intentId, requireSameStream: false);
    }

    private List<ExecutionJournalEntry> ReadJournalEntriesForMultiplierLookupLocked()
    {
        var entries = new List<ExecutionJournalEntry>();
        string[] files;
        try { files = Directory.GetFiles(_journalDir, "*.json"); }
        catch { return entries; }

        foreach (var path in files)
        {
            try
            {
                var json = ReadJournalFileWithRetry(path);
                if (json == null) continue;
                var entry = JsonUtil.Deserialize<ExecutionJournalEntry>(json);
                if (entry != null)
                    entries.Add(entry);
            }
            catch { /* skip unreadable journals for audit hydration */ }
        }

        return entries;
    }

    private static decimal? TryResolveContractMultiplierFromEntries(
        IEnumerable<ExecutionJournalEntry> entries,
        string tradingDate,
        string stream,
        string instrument,
        string intentId,
        bool requireSameStream)
    {
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalizedInstrument = NormalizeJournalInstrumentSymbol(instrument);
        if (!string.IsNullOrWhiteSpace(normalizedInstrument))
            aliases.Add(normalizedInstrument);
        foreach (var alias in ExecutionInstrumentResolver.GetInstrumentMatchAliases(normalizedInstrument))
        {
            var normalizedAlias = NormalizeJournalInstrumentSymbol(alias);
            if (!string.IsNullOrWhiteSpace(normalizedAlias))
                aliases.Add(normalizedAlias);
        }

        foreach (var entry in entries)
        {
            if (entry == null || !entry.ContractMultiplier.HasValue)
                continue;
            if (string.Equals(entry.IntentId, intentId, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!string.Equals(entry.TradingDate, tradingDate, StringComparison.OrdinalIgnoreCase))
                continue;
            if (requireSameStream && !string.Equals(entry.Stream, stream, StringComparison.OrdinalIgnoreCase))
                continue;

            var candidateInstrument = NormalizeJournalInstrumentSymbol(entry.Instrument);
            if (aliases.Contains(candidateInstrument))
                return entry.ContractMultiplier.Value;
        }

        return null;
    }

    private bool TryCompleteUntrackedFillRecoveryEntry(string tradingDate, string stream, string intentId,
        DateTimeOffset utcNow, string completionReason)
    {
        if (string.IsNullOrWhiteSpace(tradingDate) || string.IsNullOrWhiteSpace(stream) || string.IsNullOrWhiteSpace(intentId))
            return false;
        if (!string.Equals(stream, UntrackedFillRecoveryStream, StringComparison.OrdinalIgnoreCase)) return false;

        lock (_lock)
        {
            var key = $"{tradingDate}_{stream}_{intentId}";
            var journalPath = GetJournalPath(tradingDate, stream, intentId);

            ExecutionJournalEntry? entry = null;
            if (_cache.TryGetValue(key, out var cached))
                entry = cached;
            else if (File.Exists(journalPath))
            {
                var json = ReadJournalFileWithRetry(journalPath);
                if (json != null)
                    entry = JsonUtil.Deserialize<ExecutionJournalEntry>(json);
            }

            if (entry == null || entry.TradeCompleted) return false;
            if (!string.Equals(entry.Stream, UntrackedFillRecoveryStream, StringComparison.OrdinalIgnoreCase)) return false;

            entry.ExitFilledQuantityTotal = entry.EntryFilledQuantityTotal;
            entry.ExitOrderType = completionReason;
            entry.ExitFilledAtUtc ??= utcNow.ToString("o");
            entry.TradeCompleted = true;
            entry.CompletedAtUtc = utcNow.ToString("o");
            entry.CompletionReason = completionReason;
            entry.RealizedPnLPoints = null;
            entry.RealizedPnLGross = null;
            entry.RealizedPnLNet = null;

            _cache[key] = entry;
            SaveJournal(journalPath, entry);
            BumpReleaseSuppressionActivity();
            return true;
        }
    }

    /// <summary>Completes open untracked-fill recovery markers for an instrument (e.g. verified-flat or broker-flat reconciliation).</summary>
    public int CompleteOpenUntrackedFillRecoveryForInstrument(
        string executionInstrument,
        string? canonicalInstrument,
        DateTimeOffset utcNow,
        string completionReason)
    {
        var byInst = GetOpenJournalEntriesByInstrument();
        var count = 0;
        foreach (var kvp in byInst)
        {
            var key = kvp.Key?.Trim() ?? "";
            if (string.IsNullOrEmpty(key)) continue;
            if (!string.Equals(key, executionInstrument, StringComparison.OrdinalIgnoreCase) &&
                (canonicalInstrument == null ||
                 !string.Equals(key, canonicalInstrument, StringComparison.OrdinalIgnoreCase)))
                continue;

            foreach (var (tradingDate, stream, intentId, _) in kvp.Value)
            {
                if (!string.Equals(stream, UntrackedFillRecoveryStream, StringComparison.OrdinalIgnoreCase)) continue;
                if (TryCompleteUntrackedFillRecoveryEntry(tradingDate, stream, intentId, utcNow, completionReason))
                    count++;
            }
        }

        return count;
    }

    /// <summary>
    /// When the account snapshot shows no exposure for an instrument, close durable untracked-fill recovery markers
    /// so restart cannot leave stale open rows; call before periodic reconciliation pass signature.
    /// </summary>
    public int CloseUntrackedFillRecoveryMarkersWhenBrokerFlat(
        IReadOnlyDictionary<string, int> accountQtyByInstrument,
        DateTimeOffset utcNow)
    {
        var open = GetOpenJournalEntriesByInstrument();
        var instrumentsToClose = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in open)
        {
            var inst = kvp.Key?.Trim() ?? "";
            if (string.IsNullOrEmpty(inst)) continue;
            var hasRecovery = false;
            foreach (var (_, stream, _, _) in kvp.Value)
            {
                if (!string.Equals(stream, UntrackedFillRecoveryStream, StringComparison.OrdinalIgnoreCase)) continue;
                hasRecovery = true;
                break;
            }
            if (!hasRecovery) continue;
            if (ResolveBrokerAbsQtyForJournalInstrument(inst, accountQtyByInstrument) != 0) continue;
            instrumentsToClose.Add(inst);
        }

        var total = 0;
        foreach (var inst in instrumentsToClose)
        {
            var canon = BrokerPositionResolver.NormalizeCanonicalKey(inst);
            total += CompleteOpenUntrackedFillRecoveryForInstrument(inst, canon, utcNow, CompletionReasons.UNTRACKED_FILL_RECOVERY_FLAT);
        }

        return total;
    }

    /// <summary>
    /// Get count of open journal entries for an instrument. Matches execution instrument and optionally canonical.
    /// </summary>
    public int GetOpenJournalCountForInstrument(string executionInstrument, string? canonicalInstrument = null)
    {
        var byInst = GetOpenJournalEntriesByInstrument();
        var count = 0;
        foreach (var kvp in byInst)
        {
            var key = kvp.Key?.Trim() ?? "";
            if (string.IsNullOrEmpty(key)) continue;
            if (string.Equals(key, executionInstrument, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(canonicalInstrument) && string.Equals(key, canonicalInstrument, StringComparison.OrdinalIgnoreCase)))
                count += kvp.Value.Count;
        }
        return count;
    }

    /// <summary>
    /// Get sum of remaining open quantity for journal entries matching an instrument. For quantity reconciliation.
    /// Uses EntryFilledQuantityTotal - ExitFilledQuantityTotal (not just EntryFilledQuantityTotal) so partial exits
    /// (e.g. 1 of 2 at target) reconcile correctly. See 2026-03-17_YM1_RECONCILIATION_QTY_MISMATCH_INVESTIGATION.
    /// </summary>
    public int GetOpenJournalQuantitySumForInstrument(string executionInstrument, string? canonicalInstrument = null) =>
        GetOpenJournalStructuralStateForInstrument(executionInstrument, canonicalInstrument).OpenQtySum;

    /// <summary>
    /// Open exposure sum plus stable hash of intent ids with remaining open quantity &gt; 0 (release suppression).
    /// </summary>
    public (int OpenQtySum, long OpenIntentSetHash) GetOpenJournalStructuralStateForInstrument(string executionInstrument,
        string? canonicalInstrument = null) =>
        GetOpenJournalStructuralStateForInstrumentFromMap(GetOpenJournalEntriesByInstrument(), executionInstrument,
            canonicalInstrument);

    /// <summary>
    /// Same as <see cref="GetOpenJournalStructuralStateForInstrument"/> but reuses a single
    /// <see cref="GetOpenJournalEntriesByInstrument"/> result for a whole reconciliation/mismatch pass (avoids O(instruments×files) disk replay).
    /// </summary>
    public (int OpenQtySum, long OpenIntentSetHash) GetOpenJournalStructuralStateForInstrumentFromMap(
        Dictionary<string, List<(string TradingDate, string Stream, string IntentId, ExecutionJournalEntry Entry)>> openByInstrument,
        string executionInstrument,
        string? canonicalInstrument = null)
    {
        var sum = 0;
        var openIntentIds = new List<string>();
        foreach (var kvp in openByInstrument)
        {
            var key = kvp.Key?.Trim() ?? "";
            if (string.IsNullOrEmpty(key)) continue;
            if (!OpenJournalMapBucketMatches(key, executionInstrument, canonicalInstrument)) continue;
            foreach (var (_, _, intentId, entry) in kvp.Value)
            {
                var remaining = GetEntryRemainingOpenQuantity(entry);
                if (remaining <= 0) continue;
                sum += remaining;
                if (!string.IsNullOrWhiteSpace(intentId))
                    openIntentIds.Add(intentId);
            }
        }

        openIntentIds.Sort(StringComparer.OrdinalIgnoreCase);
        return (sum, ReleaseReconciliationRedundancySuppression.ComputeStableOrderedStringSetHashFromSortedList(openIntentIds));
    }

    /// <summary>
    /// Signed net open quantity for mismatch-sweep: sum of (direction sign × remaining) per open journal row on this instrument.
    /// </summary>
    public int GetOpenJournalSignedNetForInstrumentFromMap(
        Dictionary<string, List<(string TradingDate, string Stream, string IntentId, ExecutionJournalEntry Entry)>> openByInstrument,
        string executionInstrument,
        string? canonicalInstrument = null)
    {
        var sum = 0;
        foreach (var kvp in openByInstrument)
        {
            var key = kvp.Key?.Trim() ?? "";
            if (string.IsNullOrEmpty(key)) continue;
            if (!OpenJournalMapBucketMatches(key, executionInstrument, canonicalInstrument)) continue;
            foreach (var (_, _, _, entry) in kvp.Value)
            {
                var remaining = GetEntryRemainingOpenQuantity(entry);
                if (remaining <= 0) continue;
                var sign = DirectionSignFromJournalDirection(entry.Direction);
                if (sign == 0) continue;
                sum += sign * remaining;
            }
        }

        return sum;
    }

    /// <summary>
    /// True when at least two open intents on this instrument have opposing directions (e.g. long and short).
    /// </summary>
    public bool HasOpposingDirectionOpenIntentsFromMap(
        Dictionary<string, List<(string TradingDate, string Stream, string IntentId, ExecutionJournalEntry Entry)>> openByInstrument,
        string executionInstrument,
        string? canonicalInstrument = null)
    {
        var hasLong = false;
        var hasShort = false;
        foreach (var kvp in openByInstrument)
        {
            var key = kvp.Key?.Trim() ?? "";
            if (string.IsNullOrEmpty(key)) continue;
            if (!OpenJournalMapBucketMatches(key, executionInstrument, canonicalInstrument)) continue;
            foreach (var (_, _, _, entry) in kvp.Value)
            {
                var remaining = GetEntryRemainingOpenQuantity(entry);
                if (remaining <= 0) continue;
                var sign = DirectionSignFromJournalDirection(entry.Direction);
                if (sign > 0) hasLong = true;
                else if (sign < 0) hasShort = true;
                if (hasLong && hasShort) return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Historical misuse: second aggregation parameter was <c>execVariant</c> (e.g. MES) instead of true canonical (ES),
    /// so only raw keys equal to execution and duplicate micro matched — not canonical-keyed open rows.
    /// For diagnostics vs <see cref="GetOpenJournalStructuralStateForInstrumentFromMap"/> with real canonical.
    /// </summary>
    public (int OpenQtySum, long OpenIntentSetHash) GetOpenJournalStructuralStateForInstrumentFromMapMisusedExecVariantAsCanonical(
        Dictionary<string, List<(string TradingDate, string Stream, string IntentId, ExecutionJournalEntry Entry)>> openByInstrument,
        string executionInstrument,
        string execVariantDuplicate)
    {
        var sum = 0;
        var openIntentIds = new List<string>();
        foreach (var kvp in openByInstrument)
        {
            var key = kvp.Key?.Trim() ?? "";
            if (string.IsNullOrEmpty(key)) continue;
            if (string.Equals(key, executionInstrument, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(execVariantDuplicate) &&
                 string.Equals(key, execVariantDuplicate, StringComparison.OrdinalIgnoreCase)))
            {
                foreach (var (_, _, intentId, entry) in kvp.Value)
                {
                    var remaining = GetEntryRemainingOpenQuantity(entry);
                    if (remaining <= 0) continue;
                    sum += remaining;
                    if (!string.IsNullOrWhiteSpace(intentId))
                        openIntentIds.Add(intentId);
                }
            }
        }

        openIntentIds.Sort(StringComparer.OrdinalIgnoreCase);
        return (sum, ReleaseReconciliationRedundancySuppression.ComputeStableOrderedStringSetHashFromSortedList(openIntentIds));
    }

    /// <summary>Count open journal rows (all entries in matching buckets) for convergence / diagnostics.</summary>
    public static int CountOpenJournalRowsMatchingInstrumentScope(
        Dictionary<string, List<(string TradingDate, string Stream, string IntentId, ExecutionJournalEntry Entry)>> openByInstrument,
        string executionInstrument,
        string? canonicalInstrument)
    {
        var n = 0;
        foreach (var kvp in openByInstrument)
        {
            if (!OpenJournalMapBucketMatches(kvp.Key, executionInstrument, canonicalInstrument)) continue;
            n += kvp.Value.Count;
        }

        return n;
    }

    /// <inheritdoc cref="GetOpenJournalQuantitySumForInstrument"/>
    public int GetOpenJournalQuantitySumForInstrumentFromMap(
        Dictionary<string, List<(string TradingDate, string Stream, string IntentId, ExecutionJournalEntry Entry)>> openByInstrument,
        string executionInstrument,
        string? canonicalInstrument = null) =>
        GetOpenJournalStructuralStateForInstrumentFromMap(openByInstrument, executionInstrument, canonicalInstrument).OpenQtySum;

    /// <summary>
    /// Force-close orphan journals for an instrument when operator has confirmed account is correct.
    /// Use only after verifying broker position matches expectation. Marks journals with RECONCILIATION_MANUAL_OVERRIDE.
    /// </summary>
    /// <returns>Number of journals force-closed.</returns>
}
