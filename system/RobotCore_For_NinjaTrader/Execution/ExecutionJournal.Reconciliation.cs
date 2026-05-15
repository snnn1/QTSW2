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
    public int ForceReconcileOrphanJournalsForInstrument(string instrument, DateTimeOffset utcNow)
    {
        var execVariant = instrument.StartsWith("M") && instrument.Length > 1 ? instrument : "M" + instrument;
        var byInst = GetOpenJournalEntriesByInstrument();
        var count = 0;

        foreach (var kvp in byInst)
        {
            var key = kvp.Key?.Trim() ?? "";
            if (string.IsNullOrEmpty(key)) continue;
            if (!string.Equals(key, instrument, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(key, execVariant, StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var (tradingDate, stream, intentId, _) in kvp.Value)
            {
                if (ForceReconcileJournal(tradingDate, stream, intentId, utcNow))
                    count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Force-close a single journal (manual override). Use when operator confirms position was closed externally.
    /// </summary>
    private bool ForceReconcileJournal(string tradingDate, string stream, string intentId, DateTimeOffset utcNow)
    {
        if (string.IsNullOrWhiteSpace(tradingDate) || string.IsNullOrWhiteSpace(stream) || string.IsNullOrWhiteSpace(intentId))
            return false;

        lock (_lock)
        {
            var key = $"{tradingDate}_{stream}_{intentId}";
            var journalPath = GetJournalPath(tradingDate, stream, intentId);

            ExecutionJournalEntry? entry;
            if (_cache.TryGetValue(key, out var existing))
            {
                entry = existing;
            }
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
            {
                return false;
            }

            if (entry == null || !entry.EntryFilled || entry.TradeCompleted)
                return false;

            if (entry.EntryFilledQuantityTotal <= 0)
                return false;

            var journalOpenQtyBeforeClose = GetEntryRemainingOpenQuantity(entry);
            var brokerFlatAuthority = EvaluateBrokerFlatJournalCompletionAuthority(
                "ExecutionJournal.ForceReconcileManualOverride",
                entry.Instrument,
                BrokerPositionResolver.NormalizeCanonicalKey(entry.Instrument),
                tradingDate,
                stream,
                intentId,
                utcNow,
                brokerPositionQtyAbsAtDecision: 0,
                brokerWorkingOrderCount: 0,
                journalOpenQtyBeforeClose: journalOpenQtyBeforeClose,
                snapshotSufficient: false,
                snapshotError: "manual_force_reconcile_requires_broker_snapshot");
            if (!IsAllowedBrokerFlatCompletionAuthority(brokerFlatAuthority))
            {
                _log.Write(RobotEvents.EngineBase(utcNow, tradingDate, "JOURNAL_COMPLETION_AUTHORITY_DENIED", "ENGINE",
                    new
                    {
                        intent_id = intentId,
                        stream,
                        trading_date = tradingDate,
                        instrument = entry.Instrument,
                        completion_reason = CompletionReasons.RECONCILIATION_MANUAL_OVERRIDE,
                        journal_open_qty_before_close = journalOpenQtyBeforeClose,
                        authority_gate = brokerFlatAuthority.GateName,
                        authority_deny_reason = brokerFlatAuthority.DenyReason ?? "UNKNOWN",
                        authority_frame_id = brokerFlatAuthority.AuthorityFrame?.FrameId ?? "",
                        trigger_source = "ExecutionJournal.ForceReconcileManualOverride",
                        note = "Manual force-reconcile journal completion requires canonical authority with sufficient broker/account evidence."
                    }));
                return false;
            }

            entry.ExitFilledQuantityTotal = entry.EntryFilledQuantityTotal;
            entry.ExitAvgFillPrice = null;
            entry.ExitFillNotional = null;
            entry.ExitFilledAtUtc = utcNow.ToString("o");
            entry.ExitOrderType = CompletionReasons.RECONCILIATION_MANUAL_OVERRIDE;
            entry.TradeCompleted = true;
            entry.CompletedAtUtc = utcNow.ToString("o");
            entry.CompletionReason = CompletionReasons.RECONCILIATION_MANUAL_OVERRIDE;
            entry.RealizedPnLPoints = null;
            entry.RealizedPnLGross = null;
            entry.RealizedPnLNet = null;

            _cache[key] = entry;
            SaveJournal(journalPath, entry);
            BumpReleaseSuppressionActivity();
            return true;
        }
    }

    /// <summary>
    /// Record trade completion via reconciliation (broker flat, no exit fill received).
    /// Sets TradeCompleted, CompletionReason=RECONCILIATION_BROKER_FLAT.
    /// Exit price and P&L left null so reporting layer can treat reconciled trades specially.
    /// </summary>
    /// <param name="brokerPositionQtyAbsAtDecision">When &gt;= 0 and <paramref name="triggerSource"/> set, logged on <c>JOURNAL_COMPLETION_DECISION</c>.</param>
    /// <param name="journalOpenQtyBeforeClose">Remaining open qty before close; use -1 if unknown.</param>
    /// <param name="triggerSource">Non-null to emit <c>JOURNAL_COMPLETION_DECISION</c> after persist (e.g. ReconciliationRunner).</param>
    /// <param name="brokerFlatAuthority">Allowed UEA decision for <see cref="ExecutionAuthorityAction.JournalCompleteBrokerFlat"/>.</param>
    public bool RecordReconciliationComplete(string tradingDate, string stream, string intentId, DateTimeOffset utcNow,
        ExecutionAuthorityActionDecision brokerFlatAuthority,
        int brokerPositionQtyAbsAtDecision = -1,
        int journalOpenQtyBeforeClose = -1,
        string? triggerSource = null)
    {
        if (string.IsNullOrWhiteSpace(tradingDate) || string.IsNullOrWhiteSpace(stream) || string.IsNullOrWhiteSpace(intentId))
            return false;
        if (!IsAllowedBrokerFlatCompletionAuthority(brokerFlatAuthority))
        {
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate, "JOURNAL_COMPLETION_AUTHORITY_DENIED", "ENGINE",
                new
                {
                    intent_id = intentId,
                    stream,
                    trading_date = tradingDate,
                    completion_reason = CompletionReasons.RECONCILIATION_BROKER_FLAT,
                    authority_gate = brokerFlatAuthority?.GateName ?? "NULL",
                    authority_deny_reason = brokerFlatAuthority?.DenyReason ?? "AUTHORITY_NOT_ALLOWED",
                    authority_frame_id = brokerFlatAuthority?.AuthorityFrame?.FrameId ?? "",
                    trigger_source = triggerSource ?? "",
                    note = "RECONCILIATION_BROKER_FLAT journal completion requires an allowed UEA JournalCompleteBrokerFlat decision."
                }));
            return false;
        }

        lock (_lock)
        {
            var key = $"{tradingDate}_{stream}_{intentId}";
            var journalPath = GetJournalPath(tradingDate, stream, intentId);

            ExecutionJournalEntry? entry;
            if (_cache.TryGetValue(key, out var existing))
            {
                entry = existing;
            }
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
            {
                return false;
            }

            if (entry == null || !entry.EntryFilled || entry.TradeCompleted)
                return false;

            if (entry.EntryFilledQuantityTotal <= 0)
                return false;

            // Mark closed; exit price and P&L unknown (broker closed externally)
            entry.ExitFilledQuantityTotal = entry.EntryFilledQuantityTotal;
            entry.ExitAvgFillPrice = null;
            entry.ExitFillNotional = null;
            entry.ExitFilledAtUtc = utcNow.ToString("o");
            entry.ExitOrderType = CompletionReasons.RECONCILIATION_BROKER_FLAT;
            entry.TradeCompleted = true;
            entry.CompletedAtUtc = utcNow.ToString("o");
            entry.CompletionReason = CompletionReasons.RECONCILIATION_BROKER_FLAT;
            entry.RealizedPnLPoints = null;
            entry.RealizedPnLGross = null;
            entry.RealizedPnLNet = null;

            _cache[key] = entry;
            SaveJournal(journalPath, entry);
            BumpReleaseSuppressionActivity();

            if (!string.IsNullOrEmpty(triggerSource))
            {
                _log.Write(RobotEvents.EngineBase(utcNow, tradingDate, "JOURNAL_COMPLETION_DECISION", "ENGINE",
                    new
                    {
                        intent_id = intentId,
                        stream,
                        trading_date = tradingDate,
                        completion_reason = CompletionReasons.RECONCILIATION_BROKER_FLAT,
                        broker_position_qty = brokerPositionQtyAbsAtDecision < 0 ? null : (int?)brokerPositionQtyAbsAtDecision,
                        journal_open_qty_before_close = journalOpenQtyBeforeClose < 0 ? null : (int?)journalOpenQtyBeforeClose,
                        authority_gate = brokerFlatAuthority.GateName,
                        authority_frame_id = brokerFlatAuthority.AuthorityFrame?.FrameId ?? "",
                        reason = "TradeCompleted via reconciliation — broker flat confirmed by caller",
                        trigger_source = triggerSource
                    }));
            }

            return true;
        }
    }

    public static ExecutionAuthorityActionDecision EvaluateBrokerFlatJournalCompletionAuthority(
        string source,
        string instrument,
        string? canonicalInstrument,
        string tradingDate,
        string stream,
        string intentId,
        DateTimeOffset utcNow,
        int brokerPositionQtyAbsAtDecision,
        int brokerWorkingOrderCount,
        int journalOpenQtyBeforeClose,
        int ownershipOpenQty = 0,
        int ownershipActiveSlotCount = 0,
        int ownershipOrphanSlotCount = 0,
        string account = "",
        bool snapshotSufficient = true,
        string? snapshotError = null)
    {
        var brokerAbs = Math.Max(0, Math.Abs(brokerPositionQtyAbsAtDecision));
        var brokerWorking = Math.Max(0, brokerWorkingOrderCount);
        var journalOpen = Math.Max(0, journalOpenQtyBeforeClose);
        var frame = ExecutionAuthorityFrameBuilder.Build(new ExecutionAuthorityFrameBuilderInput
        {
            Source = string.IsNullOrWhiteSpace(source) ? "ExecutionJournal.RecordReconciliationComplete" : source,
            Account = account ?? "",
            Instrument = instrument?.Trim() ?? "",
            CanonicalInstrument = canonicalInstrument,
            TradingDate = tradingDate?.Trim() ?? "",
            StreamId = stream?.Trim() ?? "",
            IntentId = intentId?.Trim() ?? "",
            SubmitPath = "JOURNAL_COMPLETE_BROKER_FLAT",
            DecisionUtc = utcNow,
            FrameCreatedUtc = utcNow,
            SnapshotError = snapshotError,
            BrokerPositionQty = brokerAbs,
            BrokerWorkingOrdersCount = brokerWorking,
            JournalOpenQty = journalOpen,
            OwnershipOpenQty = Math.Max(0, ownershipOpenQty),
            OwnershipActiveSlots = Math.Max(0, ownershipActiveSlotCount),
            OwnershipOrphanSlots = Math.Max(0, ownershipOrphanSlotCount),
            AuthorityState = "JOURNAL_COMPLETE_BROKER_FLAT"
        });
        return UnifiedExecutionAuthority.EvaluateAction(new ExecutionAuthorityActionEvaluationRequest
        {
            Action = ExecutionAuthorityAction.JournalCompleteBrokerFlat,
            Source = string.IsNullOrWhiteSpace(source) ? "ExecutionJournal.RecordReconciliationComplete" : source,
            Instrument = instrument?.Trim() ?? "",
            IntentId = intentId?.Trim() ?? "",
            Stream = stream?.Trim() ?? "",
            UtcNow = utcNow,
            BrokerAbsQty = brokerAbs,
            BrokerWorkingOrderCount = brokerWorking,
            JournalOpenQty = journalOpen,
            OwnershipOpenQty = Math.Max(0, ownershipOpenQty),
            OwnershipActiveSlotCount = Math.Max(0, ownershipActiveSlotCount),
            OwnershipOrphanSlotCount = Math.Max(0, ownershipOrphanSlotCount),
            SnapshotSufficient = snapshotSufficient,
            AuthorityFrame = frame
        });
    }

    private static bool IsAllowedBrokerFlatCompletionAuthority(ExecutionAuthorityActionDecision? authority) =>
        authority != null &&
        authority.Allowed &&
        string.Equals(authority.GateName, "AuthorityJournalCompleteBrokerFlat", StringComparison.Ordinal);

    /// <summary>
    /// Pre-load all journal entries for a trading date into cache.
    /// Call on Realtime transition so BE monitoring never hits disk on first lookup.
    /// </summary>
    public void WarmCacheForTradingDate(string tradingDate)
    {
        if (string.IsNullOrWhiteSpace(tradingDate)) return;

        var prefix = $"{tradingDate.Trim()}_";
        string[] files;
        try
        {
            files = Directory.GetFiles(_journalDir, $"{prefix}*.json");
        }
        catch
        {
            return; // Directory doesn't exist or inaccessible - no-op
        }

        lock (_lock)
        {
            foreach (var path in files)
            {
                try
                {
                    var key = Path.GetFileNameWithoutExtension(path);
                    if (_cache.ContainsKey(key)) continue; // Already cached

                    var json = File.ReadAllText(path);
                    var entry = JsonUtil.Deserialize<ExecutionJournalEntry>(json);
                    if (entry != null)
                    {
                        _cache[key] = entry;
                        if (TryParseIntentIdFromCacheKey(key, out var wid))
                            SyncAdoptionCandidateIndexForIntentLocked(wid, entry);
                        // Populate entry-fill cache for O(1) HasEntryFillForStream
                        var parts = key.Split('_');
                        if (parts.Length >= 3)
                        {
                            var date = parts[0];
                            var stream = string.Join("_", parts.Skip(1).Take(parts.Length - 2));
                            var hasFill = entry.EntryFilled || entry.EntryFilledQuantityTotal > 0;
                            var fillKey = $"{date}_{stream}";
                            if (!_entryFillByStream.TryGetValue(fillKey, out var existing) || !existing)
                                _entryFillByStream[fillKey] = hasFill;
                            else if (hasFill)
                                _entryFillByStream[fillKey] = true; // OR: any intent with fill => true
                        }
                    }
                }
                catch
                {
                    // Skip individual file errors - best-effort warm
                }
            }
        }
    }

}
