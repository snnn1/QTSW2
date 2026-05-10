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
    private static string NormalizeJournalInstrumentSymbol(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var s = raw.Trim();
        var sp = s.IndexOf(' ');
        if (sp > 0) s = s.Substring(0, sp);
        return s;
    }

    /// <summary>
    /// Deterministic match for journal buckets: normalized entry key equals execution root OR canonical root (e.g. ES vs MES).
    /// Does not invent cross-product relationships — only the two roots supplied by the caller (from policy/spec).
    /// </summary>
    public static bool OpenJournalMapBucketMatches(string? journalInstrumentKey, string executionInstrument,
        string? canonicalInstrument)
    {
        var inst = NormalizeJournalInstrumentSymbol(string.IsNullOrWhiteSpace(journalInstrumentKey) ? "UNKNOWN" : journalInstrumentKey);
        var exec = NormalizeJournalInstrumentSymbol(executionInstrument);
        var canon = string.IsNullOrEmpty(canonicalInstrument) ? null : NormalizeJournalInstrumentSymbol(canonicalInstrument);
        if (string.Equals(inst, exec, StringComparison.OrdinalIgnoreCase)) return true;
        if (canon != null && string.Equals(inst, canon, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>True if journal entry instrument matches IEA execution key or canonical (ES vs MES, etc.).</summary>
    private static bool JournalInstrumentMatchesExecutionKey(string? entryInstrument, string executionInstrument, string? canonicalInstrument) =>
        OpenJournalMapBucketMatches(entryInstrument, executionInstrument, canonicalInstrument);

    private static bool IsUntrackedFillRecoveryMarker(ExecutionJournalEntry? entry) =>
        entry != null &&
        string.Equals(entry.Stream, UntrackedFillRecoveryStream, StringComparison.OrdinalIgnoreCase);

    /// <summary>Integrity-layer recovered rows: must not participate in adoption candidate index.</summary>
    public static bool IsRecoveredIntegrityJournalEntry(ExecutionJournalEntry? entry) =>
        entry != null && (entry.IsRecovered ||
                          string.Equals(entry.Stream, RecoveredIntentStream, StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(entry.IntentType, IntentTypeRecovered, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Position-authority taxonomy (<c>POSITION_AUTHORITY_*</c> logs): <see cref="IntentTypeRecovered"/> or <c>RECOVERED-</c> intent id prefix.
    /// </summary>
    private static bool IsRecoveredRowForPositionAuthority(ExecutionJournalEntry entry, string intentId) =>
        (!string.IsNullOrEmpty(intentId) && intentId.StartsWith("RECOVERED-", StringComparison.OrdinalIgnoreCase)) ||
        string.Equals(entry.IntentType, IntentTypeRecovered, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Sums open quantities from journal rows for one execution instrument (split real vs recovered for authority logging).
    /// Open qty = max(0, entry filled − exit), same as <see cref="GetEntryRemainingOpenQuantity"/>.
    /// </summary>
    public (int RealOpenQty, int RecoveredOpenQty, int OpenJournalRowCount) GetPositionAuthorityOpenQuantitiesForInstrument(
        string executionInstrumentPrimary,
        string? canonicalInstrument)
    {
        var exec = executionInstrumentPrimary.Trim();
        var canon = string.IsNullOrWhiteSpace(canonicalInstrument) ? exec : canonicalInstrument.Trim();
        lock (_lock)
        {
            var openMap = GetOpenJournalEntriesByInstrument();
            var real = 0;
            var recovered = 0;
            var rows = 0;
            foreach (var kvp in openMap)
            {
                if (!OpenJournalMapBucketMatches(kvp.Key, exec, canon)) continue;
                foreach (var (_, _, iid, entry) in kvp.Value)
                {
                    var rem = GetEntryRemainingOpenQuantity(entry);
                    if (rem <= 0) continue;
                    rows++;
                    if (IsRecoveredRowForPositionAuthority(entry, iid))
                        recovered += rem;
                    else
                        real += rem;
                }
            }

            return (real, recovered, rows);
        }
    }

    private static bool IsAdoptionCandidateJournalEntry(ExecutionJournalEntry? entry)
        => entry != null && entry.EntrySubmitted && !entry.TradeCompleted && !IsUntrackedFillRecoveryMarker(entry) &&
           !IsRecoveredIntegrityJournalEntry(entry);

    /// <summary>Parse intent id from journal file basename (tradingDate_stream..._intentId).</summary>
    private static bool TryParseIntentIdFromJournalFileName(string fileNameWithoutExtension, out string intentId)
    {
        intentId = "";
        var parts = fileNameWithoutExtension.Split('_');
        if (parts.Length < 3) return false;
        intentId = parts[parts.Length - 1];
        return !string.IsNullOrEmpty(intentId);
    }

    /// <summary>Parse intent id from cache key tradingDate_stream_intentId.</summary>
    private static bool TryParseIntentIdFromCacheKey(string cacheKey, out string intentId)
        => TryParseIntentIdFromJournalFileName(cacheKey, out intentId);

    /// <summary>Caller must hold <see cref="_lock"/>.</summary>
    private void SyncAdoptionCandidateIndexForIntentLocked(string intentId, ExecutionJournalEntry entry)
    {
        if (string.IsNullOrWhiteSpace(intentId)) return;

        if (_adoptionCandidateIntentToNormInst.TryGetValue(intentId, out var prevNorm))
        {
            if (_normInstToAdoptionCandidateIntentIds.TryGetValue(prevNorm, out var prevSet))
            {
                prevSet.Remove(intentId);
                if (prevSet.Count == 0)
                    _normInstToAdoptionCandidateIntentIds.Remove(prevNorm);
            }
            _adoptionCandidateIntentToNormInst.Remove(intentId);
        }

        if (!IsAdoptionCandidateJournalEntry(entry)) return;

        var norm = NormalizeJournalInstrumentSymbol(string.IsNullOrWhiteSpace(entry.Instrument) ? "UNKNOWN" : entry.Instrument);
        if (string.IsNullOrEmpty(norm)) norm = "UNKNOWN";

        if (!_normInstToAdoptionCandidateIntentIds.TryGetValue(norm, out var set))
        {
            set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _normInstToAdoptionCandidateIntentIds[norm] = set;
        }
        set.Add(intentId);
        _adoptionCandidateIntentToNormInst[intentId] = norm;
    }

    /// <summary>Caller must hold <see cref="_lock"/>.</summary>
    private void RebuildAdoptionCandidateIndexFromDiskLocked()
    {
        _normInstToAdoptionCandidateIntentIds.Clear();
        _adoptionCandidateIntentToNormInst.Clear();

        string[] files;
        try
        {
            files = Directory.GetFiles(_journalDir, "*.json");
        }
        catch
        {
            _adoptionCandidateIndexWarmed = false;
            return;
        }

        foreach (var path in files)
        {
            try
            {
                var fileName = Path.GetFileNameWithoutExtension(path);
                if (!TryParseIntentIdFromJournalFileName(fileName, out var intentId)) continue;

                var json = ReadJournalFileWithRetry(path);
                if (json == null) continue;
                var entry = JsonUtil.Deserialize<ExecutionJournalEntry>(json);
                if (entry == null) continue;
                SyncAdoptionCandidateIndexForIntentLocked(intentId, entry);
            }
            catch { /* skip corrupt files */ }
        }

        _adoptionCandidateIndexWarmed = true;
        // RULE: No logging here — RebuildAdoptionCandidateIndexFromDiskLocked runs from ctor before RobotEngine.Start() / RebindLogging.
    }

    /// <summary>Full disk scan — same semantics as pre-index implementation. Caller must NOT hold <see cref="_lock"/> (method acquires per-file lock).</summary>
    private HashSet<string> GetAdoptionCandidateIntentIdsForInstrumentFullScan(string executionInstrument, string? canonicalInstrument)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string[] files;
        try
        {
            files = Directory.GetFiles(_journalDir, "*.json");
        }
        catch
        {
            return result;
        }

        foreach (var path in files)
        {
            try
            {
                var fileName = Path.GetFileNameWithoutExtension(path);
                var parts = fileName.Split('_');
                if (parts.Length < 3) continue;

                var intentId = parts[parts.Length - 1];

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

                result.Add(intentId);
            }
            catch { /* skip corrupt files */ }
        }

        return result;
    }

    /// <summary>
    /// Get intent IDs that are adoption candidates for restart recovery.
    /// Includes: EntrySubmitted (unfilled entry stops, filled entries, protectives) and !TradeCompleted.
    /// Separate from GetActiveIntentsForBEMonitoring which requires EntryFilled — adoption must support unfilled entry stops.
    /// </summary>
    public HashSet<string> GetAdoptionCandidateIntentIdsForInstrument(string executionInstrument, string? canonicalInstrument = null)
    {
        lock (_lock)
        {
            if (!_adoptionCandidateIndexWarmed)
            {
                _adoptionCandidateIndexLookupFallbacks++;
                if (_adoptionCandidateIndexLookupFallbacks == 1 || _adoptionCandidateIndexLookupFallbacks % 25 == 0)
                {
                    _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, "", "EXECUTION_JOURNAL_ADOPTION_INDEX_FALLBACK", "ENGINE",
                        new
                        {
                            lookup_source = "full_scan",
                            fallback_total = _adoptionCandidateIndexLookupFallbacks,
                            warmed = false,
                            note = "Adoption candidate index cold or failed — using full journal directory scan"
                        }));
                }
            }
            else
            {
                _adoptionCandidateIndexLookupIndexHits++;
                var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var kvp in _normInstToAdoptionCandidateIntentIds)
                {
                    if (!JournalInstrumentMatchesExecutionKey(kvp.Key, executionInstrument, canonicalInstrument))
                        continue;
                    foreach (var id in kvp.Value)
                        result.Add(id);
                }
                return result;
            }
        }

        return GetAdoptionCandidateIntentIdsForInstrumentFullScan(executionInstrument, canonicalInstrument);
    }

    /// <summary>
    /// Union of adoption candidates for execution key and micro/root variant (MES/MES + MES, etc.), same as adapter recovery scope.
    /// </summary>
    public HashSet<string> GetAdoptionCandidateIntentIdsUnionForExecutionKeys(string executionInstrumentPrimary, string? canonicalInstrument = null)
    {
        var u = executionInstrumentPrimary?.Trim() ?? "";
        var execVariant = u.StartsWith("M", StringComparison.OrdinalIgnoreCase) && u.Length > 1 ? u : "M" + u;
        var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in GetAdoptionCandidateIntentIdsForInstrument(u, canonicalInstrument))
            all.Add(id);
        if (!string.Equals(execVariant, u, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var id in GetAdoptionCandidateIntentIdsForInstrument(execVariant, canonicalInstrument))
                all.Add(id);
        }
        return all;
    }

    /// <summary>
    /// Restart recovery repair: if live QTSW2 working orders still reference an intent that was completed as
    /// RECONCILIATION_BROKER_FLAT, restore that row to open so carryover exposure remains owned and adoptable.
    /// This is intentionally intent-specific; it never treats broker net flat as stream lifecycle proof.
    /// </summary>
    public int ReopenBrokerFlatCompletedJournalRowsForCarryover(
        string executionInstrumentPrimary,
        string? canonicalInstrument,
        IReadOnlyDictionary<string, int> workingIntentOpenQtyByIntent,
        DateTimeOffset utcNow,
        string? triggerSource = null)
    {
        var executionKey = executionInstrumentPrimary?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(executionKey) ||
            workingIntentOpenQtyByIntent == null ||
            workingIntentOpenQtyByIntent.Count == 0)
            return 0;

        var evidence = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in workingIntentOpenQtyByIntent)
        {
            var intentId = kvp.Key?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(intentId) || kvp.Value <= 0)
                continue;
            evidence[intentId] = Math.Max(evidence.TryGetValue(intentId, out var existing) ? existing : 0, kvp.Value);
        }

        if (evidence.Count == 0)
            return 0;

        string[] files;
        try
        {
            files = Directory.GetFiles(_journalDir, "*.json");
        }
        catch
        {
            return 0;
        }

        var reopened = 0;
        foreach (var path in files)
        {
            try
            {
                var fileName = Path.GetFileNameWithoutExtension(path);
                var parts = fileName.Split('_');
                if (parts.Length < 3) continue;
                if (!TryParseIntentIdFromJournalFileName(fileName, out var intentId)) continue;
                if (!evidence.TryGetValue(intentId, out var liveWorkingQty) || liveWorkingQty <= 0) continue;

                lock (_lock)
                {
                    var json = ReadJournalFileWithRetry(path);
                    if (json == null) continue;
                    var entry = JsonUtil.Deserialize<ExecutionJournalEntry>(json);
                    if (entry == null) continue;
                    if (!entry.EntrySubmitted || !entry.EntryFilled || !entry.TradeCompleted) continue;
                    if (entry.EntryFilledQuantityTotal <= 0) continue;
                    if (IsUntrackedFillRecoveryMarker(entry) || IsRecoveredIntegrityJournalEntry(entry)) continue;
                    if (!JournalInstrumentMatchesExecutionKey(entry.Instrument, executionKey, canonicalInstrument)) continue;

                    var priorCompletionReason = entry.CompletionReason;
                    var priorExitOrderType = entry.ExitOrderType;
                    var wasBrokerFlat =
                        string.Equals(priorCompletionReason, CompletionReasons.RECONCILIATION_BROKER_FLAT, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(priorExitOrderType, CompletionReasons.RECONCILIATION_BROKER_FLAT, StringComparison.OrdinalIgnoreCase);
                    if (!wasBrokerFlat) continue;

                    var tradingDate = !string.IsNullOrWhiteSpace(entry.TradingDate) ? entry.TradingDate! : parts[0];
                    var stream = !string.IsNullOrWhiteSpace(entry.Stream)
                        ? entry.Stream
                        : string.Join("_", parts.Skip(1).Take(parts.Length - 2));
                    if (string.IsNullOrWhiteSpace(tradingDate) || string.IsNullOrWhiteSpace(stream)) continue;

                    var priorCompletedAtUtc = entry.CompletedAtUtc;
                    var priorExitQty = entry.ExitFilledQuantityTotal;
                    var restoredOpenQty = Math.Min(entry.EntryFilledQuantityTotal, liveWorkingQty);
                    if (restoredOpenQty <= 0) continue;

                    entry.TradingDate = tradingDate;
                    entry.Stream = stream;
                    entry.ExitFilledQuantityTotal = Math.Max(0, entry.EntryFilledQuantityTotal - restoredOpenQty);
                    if (entry.ExitFilledQuantityTotal == 0)
                    {
                        entry.ExitAvgFillPrice = null;
                        entry.ExitFillNotional = null;
                        entry.ExitFilledAtUtc = null;
                        entry.ExitFilledObservedAtUtc = null;
                    }
                    entry.ExitOrderType = null;
                    entry.TradeCompleted = false;
                    entry.CompletedAtUtc = null;
                    entry.CompletionReason = null;
                    entry.RealizedPnLPoints = null;
                    entry.RealizedPnLGross = null;
                    entry.RealizedPnLNet = null;

                    var key = $"{tradingDate}_{stream}_{intentId}";
                    _cache[key] = entry;
                    SaveJournal(path, entry);
                    BumpReleaseSuppressionActivity();
                    reopened++;

                    _log.Write(RobotEvents.EngineBase(utcNow, tradingDate, "EXECUTION_JOURNAL_BROKER_FLAT_REOPENED_FOR_CARRYOVER", "ENGINE",
                        new
                        {
                            intent_id = intentId,
                            stream,
                            instrument = entry.Instrument,
                            execution_instrument = executionKey,
                            canonical_instrument = canonicalInstrument,
                            live_working_qty = liveWorkingQty,
                            restored_open_qty = restoredOpenQty,
                            prior_exit_qty = priorExitQty,
                            new_exit_qty = entry.ExitFilledQuantityTotal,
                            prior_completion_reason = priorCompletionReason,
                            prior_completed_at_utc = priorCompletedAtUtc,
                            trigger_source = triggerSource ?? "RestartAdoptionCarryoverRepair",
                            note = "Reopened broker-flat-completed journal because current QTSW2 working orders prove carried robot-owned exposure"
                        }));
                }
            }
            catch { /* skip corrupt or concurrently-mutated files */ }
        }

        return reopened;
    }

    /// <summary>
    /// <para><b>Pending adoption candidate</b> (recovery): journal row with EntrySubmitted &amp;&amp; !TradeCompleted for this instrument
    /// scope — see <see cref="GetAdoptionCandidateIntentIdsForInstrument"/>.</para>
    /// <para><b>Stale journal intent (release)</b>: pending row with no material open quantity, broker flat for the instrument,
    /// and no robot-tagged working order on the instrument references the intent id. Safe to ignore for release and close
    /// under forced convergence.</para>
    /// <para><b>Non-flat broker</b>: stale-for-release is not used; see <see cref="IsExposureRelevantAdoptionCandidateForRelease"/>.</para>
    /// </summary>
    public static bool IsStaleAdoptionJournalEntryForRelease(
        ExecutionJournalEntry entry,
        int brokerPositionQtyAbs,
        IReadOnlyCollection<string> robotTaggedIntentIdsOnInstrument,
        string intentId)
    {
        if (entry == null) return false;
        if (brokerPositionQtyAbs > 0) return false;
        var tagSet = robotTaggedIntentIdsOnInstrument as HashSet<string> ??
                     new HashSet<string>(robotTaggedIntentIdsOnInstrument ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        if (tagSet.Contains(intentId)) return false;
        var remaining = entry.EntryFilled && entry.EntryFilledQuantityTotal > 0
            ? Math.Max(0, entry.EntryFilledQuantityTotal - entry.ExitFilledQuantityTotal)
            : 0;
        return remaining <= 0;
    }

    /// <summary>
    /// Broker is flat, but the row is still a live robot-tagged unfilled entry submission. This is normal working-entry state,
    /// not stale journal residue, and should not by itself hold release mismatch open.
    /// </summary>
    private static bool IsLiveTaggedUnfilledEntryWhileBrokerFlat(
        ExecutionJournalEntry entry,
        IReadOnlyCollection<string> robotTaggedIntentIdsOnInstrument,
        string intentId)
    {
        if (entry == null || string.IsNullOrWhiteSpace(intentId)) return false;
        if (!entry.EntrySubmitted || entry.TradeCompleted || entry.EntryFilled) return false;
        var tagSet = robotTaggedIntentIdsOnInstrument as HashSet<string> ??
                     new HashSet<string>(robotTaggedIntentIdsOnInstrument ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        return tagSet.Contains(intentId);
    }

    /// <summary>Remaining open quantity for a journal entry (filled legs only).</summary>
    public static int GetEntryRemainingOpenQuantity(ExecutionJournalEntry entry)
    {
        if (entry == null || !entry.EntryFilled || entry.EntryFilledQuantityTotal <= 0) return 0;
        return Math.Max(0, entry.EntryFilledQuantityTotal - entry.ExitFilledQuantityTotal);
    }

    private static int DirectionSignFromJournalDirection(string? direction)
    {
        if (string.IsNullOrWhiteSpace(direction)) return 0;
        var u = direction.Trim().ToUpperInvariant();
        if (u.IndexOf("LONG", StringComparison.OrdinalIgnoreCase) >= 0 || u == "BUY" || u == "L") return 1;
        if (u.IndexOf("SHORT", StringComparison.OrdinalIgnoreCase) >= 0 || u == "SELL" || u == "S") return -1;
        return 0;
    }

    private static int BrokerPositionSign(int signedQty) => signedQty > 0 ? 1 : (signedQty < 0 ? -1 : 0);

    /// <summary>
    /// When the broker has non-flat exposure, an adoption candidate blocks release only if it is tied to <b>live</b>
    /// exposure: remaining open journal quantity, robot-tagged working orders on the instrument, or IEA mismatch-trusted
    /// working intents. Historical rows that merely share the same direction as the broker are <b>not</b> treated as
    /// release-blocking once they have no open qty and are absent from live tag/registry evidence (see
    /// <see cref="ReleaseBlockingExclusionReasons.NON_LIVE_STALE_ADOPTION"/>).
    /// </summary>
    public static bool IsExposureRelevantAdoptionCandidateForRelease(
        ExecutionJournalEntry entry,
        string intentId,
        int brokerPositionQtySigned,
        IReadOnlyCollection<string> robotTaggedIntentIdsOnInstrument,
        IReadOnlyCollection<string>? registryMismatchTrustedIntentIds)
    {
        if (entry == null || string.IsNullOrWhiteSpace(intentId)) return false;
        var tagSet = robotTaggedIntentIdsOnInstrument as HashSet<string> ??
                     new HashSet<string>(robotTaggedIntentIdsOnInstrument ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var regSet = registryMismatchTrustedIntentIds as HashSet<string> ??
                     new HashSet<string>(registryMismatchTrustedIntentIds ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);

        if (GetEntryRemainingOpenQuantity(entry) > 0) return true;
        if (tagSet.Contains(intentId)) return true;
        if (regSet.Count > 0 && regSet.Contains(intentId)) return true;
        return false;
    }

    private List<(string IntentId, bool Blocking, string? ExclusionReason)> BuildReleaseBlockingDecisionList(
        string executionInstrumentPrimary,
        string? canonicalInstrument,
        int brokerPositionQtyAbs,
        int brokerPositionQtySigned,
        IReadOnlyCollection<string> robotTaggedIntentIdsOnInstrument,
        IReadOnlyCollection<string>? registryMismatchTrustedIntentIds)
    {
        var decisions = new List<(string, bool, string?)>();
        var candidates = GetAdoptionCandidateIntentIdsUnionForExecutionKeys(executionInstrumentPrimary, canonicalInstrument);
        var tagSet = robotTaggedIntentIdsOnInstrument as HashSet<string> ??
                     new HashSet<string>(robotTaggedIntentIdsOnInstrument ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var regSet = registryMismatchTrustedIntentIds as HashSet<string> ??
                     new HashSet<string>(registryMismatchTrustedIntentIds ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);

        foreach (var intentId in candidates)
        {
            var located = TryGetAdoptionCandidateEntry(intentId, executionInstrumentPrimary, canonicalInstrument);
            if (located == null)
            {
                decisions.Add((intentId, true, null));
                continue;
            }

            var entry = located.Value.Entry;

            if (brokerPositionQtyAbs == 0)
            {
                if (IsLiveTaggedUnfilledEntryWhileBrokerFlat(entry, tagSet, intentId))
                {
                    decisions.Add((intentId, false, ReleaseBlockingExclusionReasons.LIVE_TAGGED_UNFILLED_ENTRY));
                    continue;
                }

                if (IsStaleAdoptionJournalEntryForRelease(entry, brokerPositionQtyAbs, tagSet, intentId))
                    decisions.Add((intentId, false, ReleaseBlockingExclusionReasons.NO_TAG));
                else
                    decisions.Add((intentId, true, null));
                continue;
            }

            var remainingOpen = GetEntryRemainingOpenQuantity(entry);
            if (remainingOpen > 0 && tagSet.Contains(intentId) && regSet.Contains(intentId))
            {
                decisions.Add((intentId, false, ReleaseBlockingExclusionReasons.REGISTRY_TRUSTED_ACTIVE_EXPOSURE));
                continue;
            }

            if (IsExposureRelevantAdoptionCandidateForRelease(
                    entry,
                    intentId,
                    brokerPositionQtySigned,
                    tagSet,
                    registryMismatchTrustedIntentIds))
            {
                decisions.Add((intentId, true, null));
                continue;
            }

            var rem = remainingOpen;
            var bSign = BrokerPositionSign(brokerPositionQtySigned);
            var jSign = DirectionSignFromJournalDirection(entry.Direction);
            string exReason;
            if (rem > 0)
                exReason = ReleaseBlockingExclusionReasons.OTHER;
            else if (jSign != 0 && bSign != 0 && jSign != bSign)
                exReason = ReleaseBlockingExclusionReasons.DIRECTION_MISMATCH;
            else if (jSign != 0 && bSign != 0 && jSign == bSign &&
                     !tagSet.Contains(intentId) &&
                     !(regSet.Count > 0 && regSet.Contains(intentId)))
                exReason = ReleaseBlockingExclusionReasons.NON_LIVE_STALE_ADOPTION;
            else if (regSet.Count > 0 && !regSet.Contains(intentId))
                exReason = ReleaseBlockingExclusionReasons.NOT_IN_REGISTRY;
            else if (!tagSet.Contains(intentId))
                exReason = ReleaseBlockingExclusionReasons.NO_TAG;
            else
                exReason = ReleaseBlockingExclusionReasons.NO_OPEN_QTY;

            decisions.Add((intentId, false, exReason));
        }

        return decisions;
    }

    /// <summary>
    /// Per blocking candidate: category, disposition, and explicit non-adoption rationale (PR2/PR3).
    /// </summary>
    public IReadOnlyList<ReleaseBlockingCandidateDiagnostic> BuildReleaseBlockingCandidateDiagnostics(
        string executionInstrumentPrimary,
        string? canonicalInstrument,
        int brokerPositionQtyAbs,
        int brokerPositionQtySigned,
        IReadOnlyCollection<string> robotTaggedIntentIdsOnInstrument,
        IReadOnlyCollection<string>? registryMismatchTrustedIntentIds)
    {
        var decisions = BuildReleaseBlockingDecisionList(
            executionInstrumentPrimary,
            canonicalInstrument,
            brokerPositionQtyAbs,
            brokerPositionQtySigned,
            robotTaggedIntentIdsOnInstrument,
            registryMismatchTrustedIntentIds);
        var tagSet = robotTaggedIntentIdsOnInstrument as HashSet<string> ??
                     new HashSet<string>(robotTaggedIntentIdsOnInstrument ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var regSet = registryMismatchTrustedIntentIds as HashSet<string> ??
                     new HashSet<string>(registryMismatchTrustedIntentIds ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var list = new List<ReleaseBlockingCandidateDiagnostic>();
        foreach (var (intentId, blocking, _) in decisions)
        {
            if (!blocking) continue;
            list.Add(BuildReleaseBlockingCandidateDiagnostic(
                intentId,
                executionInstrumentPrimary,
                canonicalInstrument,
                brokerPositionQtyAbs,
                brokerPositionQtySigned,
                tagSet,
                regSet));
        }

        return list;
    }

    /// <summary>
    /// Canonical blocker list for reconciliation contract (classification layer).
    /// Replaces ad-hoc pending counts — every blocking row is classified.
    /// </summary>
    public IReadOnlyList<ReconciliationBlocker> BuildReconciliationBlockers(
        string executionInstrumentPrimary,
        string? canonicalInstrument,
        int brokerPositionQtyAbs,
        int brokerPositionQtySigned,
        IReadOnlyCollection<string> robotTaggedIntentIdsOnInstrument,
        IReadOnlyCollection<string>? registryMismatchTrustedIntentIds,
        DateTimeOffset createdAtUtc)
    {
        var diagnostics = BuildReleaseBlockingCandidateDiagnostics(
            executionInstrumentPrimary,
            canonicalInstrument,
            brokerPositionQtyAbs,
            brokerPositionQtySigned,
            robotTaggedIntentIdsOnInstrument,
            registryMismatchTrustedIntentIds);
        if (diagnostics.Count == 0)
            return Array.Empty<ReconciliationBlocker>();
        var list = new List<ReconciliationBlocker>(diagnostics.Count);
        foreach (var d in diagnostics)
            list.Add(ReconciliationBlockerFactory.FromDiagnostic(d, createdAtUtc));
        return list;
    }

    private ReleaseBlockingCandidateDiagnostic BuildReleaseBlockingCandidateDiagnostic(
        string intentId,
        string executionInstrumentPrimary,
        string? canonicalInstrument,
        int brokerPositionQtyAbs,
        int brokerPositionQtySigned,
        HashSet<string> tagSet,
        HashSet<string> regSet)
    {
        var located = TryGetAdoptionCandidateEntry(intentId, executionInstrumentPrimary, canonicalInstrument);
        if (located == null)
        {
            var d0 = ReleaseAdoptionDispositionMapper.Map(BlockingCandidateCategory.Unknown);
            return new ReleaseBlockingCandidateDiagnostic
            {
                IntentId = intentId,
                BlocksRelease = true,
                JournalExclusionReason = null,
                Category = BlockingCandidateCategory.Unknown,
                Disposition = d0,
                RecoveryAdoptionShouldConsume = false,
                NonAdoptionReason = "journal_candidate_entry_not_located_for_intent"
            };
        }

        var entry = located.Value.Entry;

        if (brokerPositionQtyAbs == 0)
        {
            // Blocking + flat => non-stale journal row still exposure-relevant for release (BuildReleaseBlockingDecisionList).
            var dJ = ReleaseAdoptionDispositionMapper.Map(BlockingCandidateCategory.JournalOnly);
            return new ReleaseBlockingCandidateDiagnostic
            {
                IntentId = intentId,
                BlocksRelease = true,
                JournalExclusionReason = null,
                Category = BlockingCandidateCategory.JournalOnly,
                Disposition = dJ,
                RecoveryAdoptionShouldConsume = false,
                NonAdoptionReason = "pending_journal_adoption_row_broker_flat_requires_journal_or_stale_lane_not_tag_adoption"
            };
        }

        if (IsExposureRelevantAdoptionCandidateForRelease(
                entry,
                intentId,
                brokerPositionQtySigned,
                tagSet,
                regSet.Count > 0 ? regSet : null))
        {
            if (tagSet.Contains(intentId))
            {
                var cat = BlockingCandidateCategory.BrokerVisibleAdoptable;
                return new ReleaseBlockingCandidateDiagnostic
                {
                    IntentId = intentId,
                    BlocksRelease = true,
                    JournalExclusionReason = null,
                    Category = cat,
                    Disposition = ReleaseAdoptionDispositionMapper.Map(cat),
                    RecoveryAdoptionShouldConsume = true,
                    NonAdoptionReason = ""
                };
            }

            if (regSet.Count > 0 && regSet.Contains(intentId))
            {
                var catA = BlockingCandidateCategory.AlreadyOwnedElsewhere;
                return new ReleaseBlockingCandidateDiagnostic
                {
                    IntentId = intentId,
                    BlocksRelease = true,
                    JournalExclusionReason = null,
                    Category = catA,
                    Disposition = ReleaseAdoptionDispositionMapper.Map(catA),
                    RecoveryAdoptionShouldConsume = false,
                    NonAdoptionReason = "registry_trusted_but_order_tag_not_visible_for_intent_adoption_may_not_apply"
                };
            }

            var catT = BlockingCandidateCategory.TagMismatch;
            return new ReleaseBlockingCandidateDiagnostic
            {
                IntentId = intentId,
                BlocksRelease = true,
                JournalExclusionReason = null,
                Category = catT,
                Disposition = ReleaseAdoptionDispositionMapper.Map(catT),
                RecoveryAdoptionShouldConsume = false,
                NonAdoptionReason = "exposure_relevant_journal_intent_not_in_robot_tagged_working_set"
            };
        }

        var catU = BlockingCandidateCategory.UnsupportedCandidateShape;
        return new ReleaseBlockingCandidateDiagnostic
        {
            IntentId = intentId,
            BlocksRelease = true,
            JournalExclusionReason = null,
            Category = catU,
            Disposition = ReleaseAdoptionDispositionMapper.Map(catU),
            RecoveryAdoptionShouldConsume = false,
            NonAdoptionReason = "blocking_row_not_classified_as_exposure_relevant_or_flat_journal"
        };
    }

    /// <summary>
    /// Structured audit for release-blocking vs excluded adoption candidates (logging). Does not mutate the journal.
    /// </summary>
    public ReleaseBlockingCandidateAuditData BuildReleaseBlockingCandidateAudit(
        string executionInstrumentPrimary,
        string? canonicalInstrument,
        int brokerPositionQtyAbs,
        int brokerPositionQtySigned,
        IReadOnlyCollection<string> robotTaggedIntentIdsOnInstrument,
        IReadOnlyCollection<string>? registryMismatchTrustedIntentIds,
        int sampleLimit = 15)
    {
        var decisions = BuildReleaseBlockingDecisionList(
            executionInstrumentPrimary,
            canonicalInstrument,
            brokerPositionQtyAbs,
            brokerPositionQtySigned,
            robotTaggedIntentIdsOnInstrument,
            registryMismatchTrustedIntentIds);

        foreach (var (intentId, blocking, exReason) in decisions)
        {
            if (blocking ||
                !string.Equals(exReason, ReleaseBlockingExclusionReasons.NON_LIVE_STALE_ADOPTION, StringComparison.Ordinal))
                continue;
            var located = TryGetAdoptionCandidateEntry(intentId, executionInstrumentPrimary, canonicalInstrument);
            if (located == null) continue;
            var e = located.Value.Entry;
            var inTag = robotTaggedIntentIdsOnInstrument != null &&
                        robotTaggedIntentIdsOnInstrument.Any(x =>
                            string.Equals(x, intentId, StringComparison.OrdinalIgnoreCase));
            var inReg = registryMismatchTrustedIntentIds != null &&
                        registryMismatchTrustedIntentIds.Any(x =>
                            string.Equals(x, intentId, StringComparison.OrdinalIgnoreCase));
            _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", eventType: "RELEASE_BLOCKING_CANDIDATE_EXCLUDED", state: "ENGINE",
                new Dictionary<string, object?>
                {
                    ["intent_id"] = intentId,
                    ["reason"] = exReason,
                    ["broker_position_qty"] = brokerPositionQtyAbs,
                    ["broker_position_signed"] = brokerPositionQtySigned,
                    ["journal_remaining_open_qty"] = GetEntryRemainingOpenQuantity(e),
                    ["in_robot_tag_set"] = inTag,
                    ["in_registry_set"] = inReg,
                    ["direction"] = e.Direction ?? "",
                    ["execution_instrument"] = executionInstrumentPrimary,
                    ["canonical_instrument"] = canonicalInstrument ?? "",
                }));
        }

        var raw = decisions.Count;
        var blockingIds = new List<string>();
        var excludedIds = new List<string>();
        var excludedReasons = new List<string>();
        foreach (var (intentId, blocking, exReason) in decisions)
        {
            if (blocking)
                blockingIds.Add(intentId);
            else if (exReason != null)
            {
                excludedIds.Add(intentId);
                excludedReasons.Add(exReason);
            }
        }

        var blockingCount = blockingIds.Count;
        var excludedCount = excludedIds.Count;
        blockingIds.Sort(StringComparer.OrdinalIgnoreCase);
        var blockingHash = ReleaseReconciliationRedundancySuppression.ComputeStableOrderedStringSetHashFromSortedList(blockingIds);
        return new ReleaseBlockingCandidateAuditData
        {
            RawCandidateCount = raw,
            BlockingCandidateCount = blockingCount,
            ExcludedCandidateCount = excludedCount,
            BlockingIntentSetHash = blockingHash,
            BlockingIntentIdsSample = blockingIds.Take(sampleLimit).ToList(),
            ExcludedIntentIdsSample = excludedIds.Take(sampleLimit).ToList(),
            ExclusionReasonsSample = excludedReasons.Take(sampleLimit).ToList()
        };
    }

    /// <summary>
    /// Adoption candidate intent ids that are exposure-relevant for release (subset of
    /// <see cref="GetAdoptionCandidateIntentIdsUnionForExecutionKeys"/>). Does not mutate the journal.
    /// </summary>
    public HashSet<string> FilterAdoptionCandidatesForRelease(
        string executionInstrumentPrimary,
        string? canonicalInstrument,
        int brokerPositionQtyAbs,
        int brokerPositionQtySigned,
        IReadOnlyCollection<string> robotTaggedIntentIdsOnInstrument,
        IReadOnlyCollection<string>? registryMismatchTrustedIntentIds)
    {
        var blocking = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (intentId, isBlocking, _) in BuildReleaseBlockingDecisionList(
                     executionInstrumentPrimary,
                     canonicalInstrument,
                     brokerPositionQtyAbs,
                     brokerPositionQtySigned,
                     robotTaggedIntentIdsOnInstrument,
                     registryMismatchTrustedIntentIds))
        {
            if (isBlocking)
                blocking.Add(intentId);
        }

        return blocking;
    }

    /// <summary>
    /// Count of adoption candidates that must block <see cref="StateConsistencyReleaseEvaluator"/>'s pending check.
    /// Does not mutate the journal.
    /// </summary>
    public int CountReleaseBlockingAdoptionCandidates(
        string executionInstrumentPrimary,
        string? canonicalInstrument,
        int brokerPositionQtyAbs,
        int brokerPositionQtySigned,
        IReadOnlyCollection<string> robotTaggedIntentIdsOnInstrument,
        IReadOnlyCollection<string>? registryMismatchTrustedIntentIds)
    {
        var n = 0;
        foreach (var (_, isBlocking, _) in BuildReleaseBlockingDecisionList(
                     executionInstrumentPrimary,
                     canonicalInstrument,
                     brokerPositionQtyAbs,
                     brokerPositionQtySigned,
                     robotTaggedIntentIdsOnInstrument,
                     registryMismatchTrustedIntentIds))
        {
            if (isBlocking)
                n++;
        }

        return n;
    }

    /// <summary>
    /// Single pass over blocking adoption decisions for release suppression / fingerprinting (sorted intent-set hash).
    /// </summary>
    public (int BlockingCount, long BlockingIntentSetHash) GetReleaseBlockingAdoptionStructuralFingerprint(
        string executionInstrumentPrimary,
        string? canonicalInstrument,
        int brokerPositionQtyAbs,
        int brokerPositionQtySigned,
        IReadOnlyCollection<string> robotTaggedIntentIdsOnInstrument,
        IReadOnlyCollection<string>? registryMismatchTrustedIntentIds)
    {
        var blockingIds = new List<string>();
        foreach (var (intentId, isBlocking, _) in BuildReleaseBlockingDecisionList(
                     executionInstrumentPrimary,
                     canonicalInstrument,
                     brokerPositionQtyAbs,
                     brokerPositionQtySigned,
                     robotTaggedIntentIdsOnInstrument,
                     registryMismatchTrustedIntentIds))
        {
            if (isBlocking)
                blockingIds.Add(intentId);
        }

        blockingIds.Sort(StringComparer.OrdinalIgnoreCase);
        return (blockingIds.Count,
            ReleaseReconciliationRedundancySuppression.ComputeStableOrderedStringSetHashFromSortedList(blockingIds));
    }

    /// <summary>
    /// Mark stale adoption journal rows complete so recovery index and release gate converge. Only when broker is flat,
    /// no QTSW2-tagged working references the intent on this instrument snapshot, and journal has no open position quantity
    /// for the row.
    /// </summary>
    /// <returns>Number of journal files updated.</returns>
    public int ReconcileStaleAdoptionJournalCandidatesForRelease(
        string executionInstrumentPrimary,
        string? canonicalInstrument,
        int brokerPositionQtyAbs,
        IReadOnlyCollection<string> robotTaggedIntentIdsOnInstrument,
        DateTimeOffset utcNow)
    {
        var candidates = GetAdoptionCandidateIntentIdsUnionForExecutionKeys(executionInstrumentPrimary, canonicalInstrument);
        if (candidates.Count == 0) return 0;
        var tagSet = robotTaggedIntentIdsOnInstrument as HashSet<string> ??
                     new HashSet<string>(robotTaggedIntentIdsOnInstrument ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var closed = 0;
        foreach (var intentId in candidates)
        {
            var located = TryGetAdoptionCandidateEntry(intentId, executionInstrumentPrimary, canonicalInstrument);
            if (located == null) continue;
            var (tradingDate, stream, entry) = located.Value;
            if (!IsStaleAdoptionJournalEntryForRelease(entry, brokerPositionQtyAbs, tagSet, intentId))
                continue;
            if (TryCloseStaleAdoptionJournalEntry(tradingDate, stream, intentId, utcNow))
                closed++;
        }
        if (closed > 0)
            BumpReleaseSuppressionActivity();
        return closed;
    }

    /// <summary>
    /// When broker-flat proof is already present for the instrument and no working orders remain, retire lingering
    /// journal-only exposure rows that still report open quantity but have no live tag/registry ownership evidence.
    /// This keeps broker-flat recovery from waiting on stale journal residue that no longer maps to real exposure.
    /// </summary>
    public int ReconcileBrokerFlatJournalRowsForRelease(
        string executionInstrumentPrimary,
        string? canonicalInstrument,
        int brokerPositionQtyAbs,
        int brokerWorkingCount,
        IReadOnlyCollection<string> robotTaggedIntentIdsOnInstrument,
        IReadOnlyCollection<string>? registryMismatchTrustedIntentIds,
        DateTimeOffset utcNow,
        string? triggerSource = null,
        bool suppressWhileFlattenPending = false,
        bool allowTaggedResidualRetirement = false,
        Action<string, string, string, int>? onReconciled = null)
    {
        if (brokerPositionQtyAbs != 0 || brokerWorkingCount != 0 || suppressWhileFlattenPending)
            return 0;

        var openByInstrument = GetOpenJournalEntriesByInstrument();
        if (openByInstrument.Count == 0)
            return 0;

        var tagSet = robotTaggedIntentIdsOnInstrument as HashSet<string> ??
                     new HashSet<string>(robotTaggedIntentIdsOnInstrument ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var closed = 0;

        foreach (var kv in openByInstrument)
        {
            if (!OpenJournalInstrumentKeyMatches(kv.Key, executionInstrumentPrimary, canonicalInstrument))
                continue;

            foreach (var (tradingDate, stream, intentId, entry) in kv.Value)
            {
                // Broker-flat + no working is stronger proof than stale registry trust. Keep actively tagged intents
                // protected unless the caller is running the final residual-cleanup pulse.
                if (!allowTaggedResidualRetirement && tagSet.Contains(intentId))
                    continue;

                var rowKey = $"{tradingDate}_{stream}_{intentId}";
                if (!processed.Add(rowKey))
                    continue;
                if (entry == null || !entry.EntryFilled || entry.TradeCompleted || entry.EntryFilledQuantityTotal <= 0)
                    continue;

                var remaining = GetEntryRemainingOpenQuantity(entry);
                if (remaining <= 0)
                    continue;

                if (RecordReconciliationComplete(
                        tradingDate,
                        stream,
                        intentId,
                        utcNow,
                        brokerPositionQtyAbsAtDecision: 0,
                        journalOpenQtyBeforeClose: remaining,
                        triggerSource: triggerSource ?? "BrokerFlatReleaseReadiness"))
                {
                    closed++;
                    onReconciled?.Invoke(tradingDate, stream, intentId, remaining);
                }
            }
        }

        return closed;
    }

    /// <summary>
    /// When broker-flat proof arrives after a timed-out session-close flatten, close any still-open interrupted
    /// journal rows for the same execution/canonical instrument set so sibling streams can reenter together.
    /// </summary>
    public int ReconcileInterruptedSessionCloseBrokerFlat(
        string executionInstrumentPrimary,
        string? canonicalInstrument,
        IReadOnlyCollection<string> interruptedIntentIds,
        DateTimeOffset utcNow,
        string? triggerSource = null,
        Action<string, string, string, int>? onReconciled = null)
    {
        var executionKey = executionInstrumentPrimary?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(executionKey) || interruptedIntentIds == null || interruptedIntentIds.Count == 0)
            return 0;

        var interruptedSet = interruptedIntentIds as HashSet<string> ??
                             new HashSet<string>(interruptedIntentIds, StringComparer.OrdinalIgnoreCase);
        if (interruptedSet.Count == 0)
            return 0;

        var openByInstrument = GetOpenJournalEntriesByInstrument();
        var processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var closed = 0;

        foreach (var kv in openByInstrument)
        {
            if (!OpenJournalInstrumentKeyMatches(kv.Key, executionKey, canonicalInstrument))
                continue;

            foreach (var (tradingDate, stream, intentId, entry) in kv.Value)
            {
                if (!interruptedSet.Contains(intentId))
                    continue;

                var rowKey = $"{tradingDate}_{stream}_{intentId}";
                if (!processed.Add(rowKey))
                    continue;

                var openQty = GetEntryRemainingOpenQuantity(entry);
                if (openQty <= 0)
                    continue;

                if (RecordReconciliationComplete(
                        tradingDate,
                        stream,
                        intentId,
                        utcNow,
                        brokerPositionQtyAbsAtDecision: 0,
                        journalOpenQtyBeforeClose: openQty,
                        triggerSource: triggerSource ?? "LateSessionCloseFlattenBrokerFlat"))
                {
                    closed++;
                    onReconciled?.Invoke(tradingDate, stream, intentId, openQty);
                }
            }
        }

        return closed;
    }

    private bool TryCloseStaleAdoptionJournalEntry(string tradingDate, string stream, string intentId, DateTimeOffset utcNow)
    {
        if (string.IsNullOrWhiteSpace(tradingDate) || string.IsNullOrWhiteSpace(stream) || string.IsNullOrWhiteSpace(intentId))
            return false;

        lock (_lock)
        {
            var key = $"{tradingDate}_{stream}_{intentId}";
            var journalPath = GetJournalPath(tradingDate, stream, intentId);

            ExecutionJournalEntry? entry;
            if (_cache.TryGetValue(key, out var existing))
                entry = existing;
            else if (File.Exists(journalPath))
            {
                try
                {
                    var json = File.ReadAllText(journalPath);
                    entry = JsonUtil.Deserialize<ExecutionJournalEntry>(json);
                    if (entry != null)
                        _cache[key] = entry;
                    else
                        return false;
                }
                catch
                {
                    return false;
                }
            }
            else
                return false;

            if (entry == null || !entry.EntrySubmitted || entry.TradeCompleted)
                return false;

            if (entry.EntryFilled && entry.EntryFilledQuantityTotal > 0)
            {
                var remaining = Math.Max(0, entry.EntryFilledQuantityTotal - entry.ExitFilledQuantityTotal);
                if (remaining > 0)
                    return false;

                entry.ExitFilledQuantityTotal = entry.EntryFilledQuantityTotal;
                entry.ExitAvgFillPrice = null;
                entry.ExitFillNotional = null;
                entry.ExitFilledAtUtc = utcNow.ToString("o");
                entry.ExitOrderType = CompletionReasons.RECONCILIATION_STALE_JOURNAL_RELEASE;
            }

            entry.TradeCompleted = true;
            entry.CompletedAtUtc = utcNow.ToString("o");
            entry.CompletionReason = CompletionReasons.RECONCILIATION_STALE_JOURNAL_RELEASE;
            entry.RealizedPnLPoints = null;
            entry.RealizedPnLGross = null;
            entry.RealizedPnLNet = null;

            _cache[key] = entry;
            SaveJournal(journalPath, entry);

            SyncAdoptionCandidateIndexForIntentLocked(intentId, entry);

            _log.Write(RobotEvents.EngineBase(utcNow, "", "EXECUTION_JOURNAL_STALE_RELEASE_CLOSED", "ENGINE",
                new
                {
                    intent_id = intentId,
                    trading_date = tradingDate,
                    stream,
                    note = "Stale adoption journal closed for state-consistency release (broker flat, no tagged working ref, no open qty)"
                }));
            return true;
        }
    }

    private static bool OpenJournalInstrumentKeyMatches(string? key, string executionInstrumentPrimary, string? alternateInstrumentKey) =>
        OpenJournalMapBucketMatches(key, executionInstrumentPrimary, alternateInstrumentKey);

    /// <summary>
    /// When sum of open journal quantities for the instrument exceeds broker position, trim phantom exposure by applying
    /// virtual exits (or full completion) to unprotected rows first (no QTSW2 tag / mismatch-trusted registry intent),
    /// then protected rows if needed. Fail-closed if excess cannot be removed without touching protected attribution.
    /// </summary>
    /// <returns>Number of journal write operations applied.</returns>
    public int ReconcileJournalOpenQuantityWithBroker(
        string executionInstrumentPrimary,
        string? alternateInstrumentKey,
        int brokerPositionQtyAbs,
        IReadOnlyCollection<string> robotTaggedIntentIdsOnInstrument,
        IReadOnlyCollection<string>? registryMismatchTrustedIntentIds,
        DateTimeOffset utcNow)
    {
        var byInst = GetOpenJournalEntriesByInstrument();
        var rows = new List<(string TradingDate, string Stream, string IntentId, int Remaining, ExecutionJournalEntry Entry)>();
        foreach (var kvp in byInst)
        {
            if (!OpenJournalInstrumentKeyMatches(kvp.Key, executionInstrumentPrimary, alternateInstrumentKey))
                continue;
            foreach (var (tradingDate, stream, intentId, entry) in kvp.Value)
            {
                var rem = GetEntryRemainingOpenQuantity(entry);
                if (rem > 0)
                    rows.Add((tradingDate, stream, intentId, rem, entry));
            }
        }

        var sum = 0;
        foreach (var r in rows)
            sum += r.Remaining;

        if (sum <= brokerPositionQtyAbs)
            return 0;

        var excess = sum - brokerPositionQtyAbs;
        var tagSet = robotTaggedIntentIdsOnInstrument as HashSet<string> ??
                     new HashSet<string>(robotTaggedIntentIdsOnInstrument ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var regSet = registryMismatchTrustedIntentIds as HashSet<string> ??
                     new HashSet<string>(registryMismatchTrustedIntentIds ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);

        bool Protected(string intentId) =>
            tagSet.Contains(intentId) || (regSet.Count > 0 && regSet.Contains(intentId));

        static string RowRemKey((string TradingDate, string Stream, string IntentId, int Remaining, ExecutionJournalEntry Entry) row) =>
            $"{row.TradingDate}\x1f{row.Stream}\x1f{row.IntentId}";

        var ordered = rows
            .OrderBy(r => Protected(r.IntentId) ? 1 : 0)
            .ThenBy(r => r.TradingDate, StringComparer.Ordinal)
            .ThenBy(r => r.Stream, StringComparer.Ordinal)
            .ThenBy(r => r.IntentId, StringComparer.Ordinal)
            .ToList();

        var writes = 0;
        var remLeftByRow = rows.ToDictionary(RowRemKey, r => r.Remaining, StringComparer.Ordinal);
        var trimmedSamples = new List<(string td, string st, string id, int qty)>();
        var totalQtyTrimmed = 0;

        foreach (var r in ordered)
        {
            if (excess <= 0) break;
            if (Protected(r.IntentId)) continue;
            var rk = RowRemKey(r);
            if (!remLeftByRow.TryGetValue(rk, out var remLeft) || remLeft <= 0)
                continue;

            var take = Math.Min(remLeft, excess);
            if (take <= 0) continue;
            if (!TryApplyJournalPositionAlignmentExitQty(r.TradingDate, r.Stream, r.IntentId, take, utcNow,
                    brokerPositionQtyAbs, robotTaggedIntentIdsOnInstrument, registryMismatchTrustedIntentIds))
                continue;

            remLeftByRow[rk] = remLeft - take;
            excess -= take;
            writes++;
            totalQtyTrimmed += take;
            if (trimmedSamples.Count < 15)
                trimmedSamples.Add((r.TradingDate, r.Stream, r.IntentId, take));
        }

        var journalAfter = sum - totalQtyTrimmed;
        var protectedPreserved = rows.Count(r => Protected(r.IntentId) && r.Remaining > 0);
        if (writes > 0)
        {
            _log.Write(RobotEvents.EngineBase(utcNow, "", "EXECUTION_JOURNAL_POSITION_ALIGNMENT", "ENGINE",
                new
                {
                    instrument = executionInstrumentPrimary,
                    broker_position_qty_before = brokerPositionQtyAbs,
                    journal_open_qty_before = sum,
                    journal_open_qty_after = journalAfter,
                    trimmed_row_count = writes,
                    trimmed_rows_sample = trimmedSamples.Select(s => new
                    {
                        trading_date = s.td,
                        stream = s.st,
                        intent_id = s.id,
                        qty_trimmed = s.qty
                    }).ToList(),
                    protected_rows_preserved_count = protectedPreserved,
                    completion_reason = CompletionReasons.RECONCILIATION_POSITION_ALIGNMENT,
                    note = "Batch summary for journal open qty alignment toward broker position"
                }));
            BumpReleaseSuppressionActivity();
        }

        return writes;
    }

    private static bool TryParseJournalIsoUtc(string? s, out DateTimeOffset dto)
    {
        dto = default;
        if (string.IsNullOrWhiteSpace(s)) return false;
        return DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out dto);
    }

    /// <summary>
    /// Migrates persisted legacy timestamp fields into canonical UTC fields without rewriting chronology.
    /// </summary>
    private static void HydrateCanonicalTimestampsFromLegacy(ExecutionJournalEntry entry)
    {
        if (string.IsNullOrEmpty(entry.EntrySubmittedAtUtc) && !string.IsNullOrEmpty(entry.EntrySubmittedAt) &&
            TryParseJournalIsoUtc(entry.EntrySubmittedAt, out _))
            entry.EntrySubmittedAtUtc = entry.EntrySubmittedAt;

        if (string.IsNullOrEmpty(entry.EntryFilledAtUtc) && !string.IsNullOrEmpty(entry.EntryFilledAt) &&
            TryParseJournalIsoUtc(entry.EntryFilledAt, out _))
            entry.EntryFilledAtUtc = entry.EntryFilledAt;

        if (string.IsNullOrEmpty(entry.EntrySubmittedObservedAtUtc) && !string.IsNullOrEmpty(entry.EntrySubmittedAtUtc))
            entry.EntrySubmittedObservedAtUtc = entry.EntrySubmittedAtUtc;
        if (string.IsNullOrEmpty(entry.EntryFilledObservedAtUtc) && !string.IsNullOrEmpty(entry.EntryFilledAtUtc))
            entry.EntryFilledObservedAtUtc = entry.EntryFilledAtUtc;
    }

    private static DateTimeOffset AlignmentExitTimestampUtc(ExecutionJournalEntry entry, DateTimeOffset utcNow)
    {
        if (TryParseJournalIsoUtc(entry.EntryFilledAtUtc, out var entryAt) && utcNow < entryAt)
            return entryAt;
        return utcNow;
    }

    /// <summary>
    /// Caps virtual alignment exit so we do not mark <see cref="ExecutionJournalEntry.TradeCompleted"/> while the broker
    /// still shows exposure, while intent is tied to working/tag state, or while exit timestamps would precede entry.
    /// </summary>
    private static int MaxVirtualExitFilledTotalForAlignment(
        ExecutionJournalEntry entry,
        DateTimeOffset utcNow,
        int brokerPositionQtyAbs,
        string intentId,
        IReadOnlyCollection<string> robotTaggedIntentIdsOnInstrument,
        IReadOnlyCollection<string>? registryMismatchTrustedIntentIds,
        out string reason)
    {
        reason = "";
        var entryTotal = entry.EntryFilledQuantityTotal;
        if (entryTotal <= 0)
        {
            reason = "no_entry_qty";
            return 0;
        }

        var tagSet = robotTaggedIntentIdsOnInstrument as HashSet<string> ??
                     new HashSet<string>(robotTaggedIntentIdsOnInstrument ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var regSet = registryMismatchTrustedIntentIds as HashSet<string> ??
                     new HashSet<string>(registryMismatchTrustedIntentIds ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);

        if (brokerPositionQtyAbs > 0)
        {
            reason = "broker_not_flat";
            return Math.Max(0, entryTotal - 1);
        }

        if (tagSet.Contains(intentId) || (regSet.Count > 0 && regSet.Contains(intentId)))
        {
            reason = "intent_tagged_or_registry_trusted_working";
            return Math.Max(0, entryTotal - 1);
        }

        if (!TryParseJournalIsoUtc(entry.EntryFilledAtUtc, out var entryAt))
        {
            reason = "entry_timestamp_missing_or_unparseable";
            return Math.Max(0, entryTotal - 1);
        }

        if (utcNow < entryAt)
        {
            reason = "alignment_clock_before_entry_fill";
            return Math.Max(0, entryTotal - 1);
        }

        return entryTotal;
    }

    private bool TryApplyJournalPositionAlignmentExitQty(
        string tradingDate,
        string stream,
        string intentId,
        int exitQty,
        DateTimeOffset utcNow,
        int brokerPositionQtyAbs,
        IReadOnlyCollection<string> robotTaggedIntentIdsOnInstrument,
        IReadOnlyCollection<string>? registryMismatchTrustedIntentIds)
    {
        if (exitQty <= 0 || string.IsNullOrWhiteSpace(tradingDate) || string.IsNullOrWhiteSpace(stream) ||
            string.IsNullOrWhiteSpace(intentId))
            return false;

        lock (_lock)
        {
            var key = $"{tradingDate}_{stream}_{intentId}";
            var journalPath = GetJournalPath(tradingDate, stream, intentId);

            ExecutionJournalEntry? entry;
            if (_cache.TryGetValue(key, out var existing))
                entry = existing;
            else if (File.Exists(journalPath))
            {
                try
                {
                    var json = File.ReadAllText(journalPath);
                    entry = JsonUtil.Deserialize<ExecutionJournalEntry>(json);
                    if (entry != null)
                        _cache[key] = entry;
                }
                catch
                {
                    return false;
                }
            }
            else
                return false;

            if (entry == null || !entry.EntryFilled || entry.EntryFilledQuantityTotal <= 0 || entry.TradeCompleted)
                return false;

            var remaining = Math.Max(0, entry.EntryFilledQuantityTotal - entry.ExitFilledQuantityTotal);
            if (remaining <= 0)
                return false;

            var requestedApply = Math.Min(exitQty, remaining);
            if (requestedApply <= 0)
                return false;

            var exitBefore = entry.ExitFilledQuantityTotal;
            var maxExitTotal = MaxVirtualExitFilledTotalForAlignment(
                entry, utcNow, brokerPositionQtyAbs, intentId,
                robotTaggedIntentIdsOnInstrument, registryMismatchTrustedIntentIds,
                out var guardReason);
            var cappedApply = Math.Min(requestedApply, Math.Max(0, maxExitTotal - exitBefore));
            if (cappedApply <= 0)
            {
                _log.Write(RobotEvents.EngineBase(utcNow, "", "JOURNAL_COMPLETION_BLOCKED_ALIGNMENT_INVALID", "ENGINE",
                    new
                    {
                        intent_id = intentId,
                        entry_qty = entry.EntryFilledQuantityTotal,
                        exit_qty = exitBefore,
                        broker_position_qty = brokerPositionQtyAbs,
                        reason = guardReason.Length > 0 ? guardReason : "virtual_exit_would_exceed_allowed_cap"
                    }));
                return false;
            }

            if (cappedApply < requestedApply)
            {
                _log.Write(RobotEvents.EngineBase(utcNow, "", "JOURNAL_COMPLETION_BLOCKED_ALIGNMENT_INVALID", "ENGINE",
                    new
                    {
                        intent_id = intentId,
                        entry_qty = entry.EntryFilledQuantityTotal,
                        exit_qty = exitBefore,
                        broker_position_qty = brokerPositionQtyAbs,
                        reason = "apply_capped:" + (guardReason.Length > 0 ? guardReason : "avoid_false_trade_completed")
                    }));
            }

            entry.ExitFilledQuantityTotal += cappedApply;
            var exitStamp = AlignmentExitTimestampUtc(entry, utcNow);
            entry.ExitOrderType = CompletionReasons.RECONCILIATION_POSITION_ALIGNMENT;

            var fullyClosed = entry.ExitFilledQuantityTotal >= entry.EntryFilledQuantityTotal;
            if (fullyClosed)
                entry.ExitFilledAtUtc = exitStamp.ToString("o");
            else
                entry.ExitFilledAtUtc ??= exitStamp.ToString("o");

            if (fullyClosed)
            {
                entry.TradeCompleted = true;
                entry.CompletedAtUtc = exitStamp.ToString("o");
                entry.CompletionReason = CompletionReasons.RECONCILIATION_POSITION_ALIGNMENT;
                entry.RealizedPnLPoints = null;
                entry.RealizedPnLGross = null;
                entry.RealizedPnLNet = null;
            }
            else
            {
                entry.TradeCompleted = false;
                entry.CompletedAtUtc = null;
                entry.CompletionReason = CompletionReasons.RECONCILIATION_ALIGNMENT_PENDING;
                entry.RealizedPnLPoints = null;
                entry.RealizedPnLGross = null;
                entry.RealizedPnLNet = null;
            }

            _cache[key] = entry;
            SaveJournal(journalPath, entry);
            SyncAdoptionCandidateIndexForIntentLocked(intentId, entry);
            BumpReleaseSuppressionActivity();
            return true;
        }
    }

    /// <summary>
    /// Try get journal entry for adoption candidate by intentId.
    /// Used for cross-instance fill resolution when IntentMap may not have the intent yet.
    /// Returns (tradingDate, stream, entry) if found; null otherwise.
    /// </summary>
}
