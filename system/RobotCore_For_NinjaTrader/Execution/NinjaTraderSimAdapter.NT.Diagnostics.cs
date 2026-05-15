// NinjaTrader-specific implementation using real NT APIs
// This file is compiled only when NINJATRADER is defined (inside NT Strategy context)

// CRITICAL: Define NINJATRADER for NinjaTrader's compiler
// NinjaTrader compiles to tmp folder and may not respect .csproj DefineConstants
#define NINJATRADER

#if NINJATRADER
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.CSharp.RuntimeBinder;
using NinjaTrader.Cbi;
using NinjaTrader.NinjaScript;
using QTSW2.Robot.Contracts;
using QTSW2.Robot.Core;
using QTSW2.Robot.Core.Diagnostics;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Real NinjaTrader API implementation for SIM adapter.
/// This partial class provides real NT API calls when running inside NinjaTrader.
/// </summary>
public sealed partial class NinjaTraderSimAdapter
{
    /// <summary>
    /// Tagged broker exposure without open journal row: upsert recovery journal when ownership evidence is strong.
    /// </summary>
    private bool TryRepairTaggedBrokerWithoutJournalCore(
        string instrument,
        int accountQtyAbs,
        int journalOpenQtySum,
        DateTimeOffset utcNow,
        out string resultCode,
        out string? detail)
    {
        resultCode = "";
        detail = null;
        if (journalOpenQtySum > 0)
        {
            resultCode = "SKIP_JOURNAL_ALREADY_OPEN";
            return false;
        }
        if (accountQtyAbs <= 0)
        {
            resultCode = "SKIP_NO_ACCOUNT_EXPOSURE";
            return false;
        }
        if (!_ntContextSet)
        {
            resultCode = "NT_CONTEXT";
            return false;
        }

        var instTrim = (instrument ?? "").Trim();
        if (string.IsNullOrEmpty(instTrim))
        {
            resultCode = "INSTRUMENT_EMPTY";
            return false;
        }

        if (TrySuppressTaggedRepairRepeatedFailure(instTrim, accountQtyAbs, journalOpenQtySum, utcNow, out resultCode))
        {
            detail = null;
            return false;
        }

        AccountSnapshot snap;
        try
        {
            snap = GetAccountSnapshotReal(utcNow);
        }
        catch (Exception ex)
        {
            resultCode = "SNAPSHOT_ERROR";
            detail = ex.Message;
            return false;
        }

        PrepareOrderRegistryForMismatchAssembly(instTrim, snap, utcNow);

        var positions = snap.Positions ?? new List<PositionSnapshot>();
        var exposure = BrokerPositionResolver.ResolveFromSnapshots(positions, instTrim);
        if (exposure.ReconciliationAbsQuantityTotal <= 0)
        {
            resultCode = "SNAPSHOT_ZERO_POSITION";
            RecordTaggedRepairFailureForCooldown(instTrim, accountQtyAbs, journalOpenQtySum, utcNow, resultCode);
            return false;
        }

        var repairQty = Math.Min(accountQtyAbs, exposure.ReconciliationAbsQuantityTotal);
        if (repairQty <= 0)
            repairQty = accountQtyAbs;

        var signedSum = 0;
        foreach (var leg in exposure.Legs)
            signedSum += leg.SignedQuantity;
        var direction = signedSum > 0 ? "Long" : "Short";

        var reopenedCarryover = _executionJournal.ReopenBrokerFlatCompletedJournalRowsForCarryoverFromStreamState(
            instTrim,
            DeriveCanonicalFromExecutionInstrument(instTrim),
            repairQty,
            direction,
            utcNow,
            "TaggedBrokerWithoutJournalStreamCarryover");
        if (reopenedCarryover > 0)
        {
            HydrateIntentsFromOpenJournals();
            resultCode = "REOPEN_CARRYOVER_JOURNAL_OK";
            detail = reopenedCarryover.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _log.Write(RobotEvents.EngineBase(utcNow, "", "ADOPTION_CARRYOVER_STREAM_JOURNAL_REOPENED", "ENGINE",
                new
                {
                    instrument = instTrim,
                    account_qty = accountQtyAbs,
                    reopened_journal_count = reopenedCarryover,
                    direction,
                    repair_result = resultCode,
                    note = "Broker exposure plus nonterminal stream journal restored a broker-flat-completed carried lifecycle before tagged-broker repair."
                }));
            ClearTaggedRepairFailureCooldown(instTrim);
            return true;
        }

        decimal? avgFill = null;
        foreach (var p in positions)
        {
            if (p.Quantity == 0 || string.IsNullOrWhiteSpace(p.Instrument)) continue;
            if (!string.Equals(p.Instrument.Trim(), instTrim, StringComparison.OrdinalIgnoreCase)) continue;
            avgFill = p.AveragePrice;
            break;
        }

        var robotWorkingOnInst = 0;
        var brokerOrderIds = new List<string>();
        foreach (var w in snap.WorkingOrders ?? new List<WorkingOrderSnapshot>())
        {
            if (string.IsNullOrEmpty(w.Tag) || !w.Tag.StartsWith(RobotOrderIds.Prefix, StringComparison.Ordinal)) continue;
            var wInst = (w.Instrument ?? "").Trim();
            if (!string.Equals(wInst, instTrim, StringComparison.OrdinalIgnoreCase) &&
                !ExecutionInstrumentResolver.IsSameInstrument(wInst, instTrim))
                continue;
            robotWorkingOnInst++;
            if (!string.IsNullOrEmpty(w.OrderId))
                brokerOrderIds.Add(w.OrderId);
        }

        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in GetActiveIntentIdsForProtectiveAudit(instTrim))
            candidates.Add(id);
        foreach (var w in snap.WorkingOrders ?? new List<WorkingOrderSnapshot>())
        {
            if (string.IsNullOrEmpty(w.Tag) || !w.Tag.StartsWith(RobotOrderIds.Prefix, StringComparison.Ordinal)) continue;
            var wInst = (w.Instrument ?? "").Trim();
            if (!string.Equals(wInst, instTrim, StringComparison.OrdinalIgnoreCase) &&
                !ExecutionInstrumentResolver.IsSameInstrument(wInst, instTrim))
                continue;
            var dec = RobotOrderIds.DecodeIntentId(w.Tag);
            if (!string.IsNullOrEmpty(dec))
                candidates.Add(dec);
        }
        foreach (var kv in OrderMap)
        {
            var oi = kv.Value;
            if (!oi.IsEntryOrder) continue;
            var oInst = (oi.Instrument ?? "").Trim();
            if (!string.Equals(oInst, instTrim, StringComparison.OrdinalIgnoreCase) &&
                !ExecutionInstrumentResolver.IsSameInstrument(oInst, instTrim))
                continue;
            candidates.Add(kv.Key);
        }

        var candidateList = candidates.Count > 0 ? candidates.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList() : new List<string>();
        if (candidateList.Count == 0)
        {
            resultCode = "NO_OWNERSHIP_EVIDENCE";
            _log.Write(RobotEvents.EngineBase(utcNow, "", "TAGGED_BROKER_WITHOUT_JOURNAL_DETECTED", "ENGINE",
                new
                {
                    instrument = instTrim,
                    account_qty = accountQtyAbs,
                    journal_qty = journalOpenQtySum,
                    intent_ids = Array.Empty<string>(),
                    registry_owned_working_count = robotWorkingOnInst,
                    broker_order_ids_sample = brokerOrderIds.Take(8).ToList(),
                    repair_action = "NONE",
                    repair_result = resultCode,
                    reason = "TAGGED_ORPHAN_POSITION_RECOVERY"
                }));
            RecordTaggedRepairFailureForCooldown(instTrim, accountQtyAbs, journalOpenQtySum, utcNow, resultCode);
            return false;
        }

        string? bestId = null;
        Intent? bestIntent = null;
        foreach (var cid in candidateList)
        {
            if (!IntentMap.TryGetValue(cid, out var intn) || intn == null) continue;
            if (string.IsNullOrWhiteSpace(intn.TradingDate) || string.IsNullOrWhiteSpace(intn.Stream)) continue;
            var idir = intn.Direction ?? "";
            if (!string.IsNullOrEmpty(idir) && !idir.Equals(direction, StringComparison.OrdinalIgnoreCase))
                continue;
            bestId = cid;
            bestIntent = intn;
            break;
        }
        if (bestId == null)
        {
            foreach (var cid in candidateList)
            {
                if (!IntentMap.TryGetValue(cid, out var intn) || intn == null) continue;
                if (string.IsNullOrWhiteSpace(intn.TradingDate) || string.IsNullOrWhiteSpace(intn.Stream)) continue;
                bestId = cid;
                bestIntent = intn;
                break;
            }
        }

        if (bestId == null || bestIntent == null)
        {
            resultCode = "NO_QUALIFYING_INTENT_CONTEXT";
            _log.Write(RobotEvents.EngineBase(utcNow, "", "TAGGED_BROKER_WITHOUT_JOURNAL_DETECTED", "ENGINE",
                new
                {
                    instrument = instTrim,
                    account_qty = accountQtyAbs,
                    journal_qty = journalOpenQtySum,
                    intent_ids = candidateList,
                    registry_owned_working_count = robotWorkingOnInst,
                    broker_order_ids_sample = brokerOrderIds.Take(8).ToList(),
                    repair_action = "NONE",
                    repair_result = resultCode,
                    reason = "TAGGED_ORPHAN_POSITION_RECOVERY"
                }));
            RecordTaggedRepairFailureForCooldown(instTrim, accountQtyAbs, journalOpenQtySum, utcNow, resultCode);
            return false;
        }

        var (_, _, _, intentStop, intentTarget, intentDir, _) = GetIntentInfo(bestId);
        var useDir = string.IsNullOrEmpty(intentDir) ? direction : intentDir!;
        var brokerOid = OrderMap.TryGetValue(bestId, out var oMap) ? oMap.OrderId : null;

        _log.Write(RobotEvents.EngineBase(utcNow, bestIntent.TradingDate ?? "", "TAGGED_BROKER_WITHOUT_JOURNAL_DETECTED", "ENGINE",
            new
            {
                instrument = instTrim,
                account_qty = accountQtyAbs,
                journal_qty = journalOpenQtySum,
                intent_ids = candidateList,
                chosen_intent_id = bestId,
                registry_owned_working_count = robotWorkingOnInst,
                broker_order_ids_sample = brokerOrderIds.Take(8).ToList(),
                repair_action = "TAGGED_BROKER_EXPOSURE_RECOVERY_JOURNAL_UPSERT",
                repair_result = "ATTEMPTING",
                reason = "TAGGED_ORPHAN_POSITION_RECOVERY"
            }));

        var correl = $"TAGGED_RECOVERY:{instTrim}:{utcNow:yyyyMMddHHmmssfff}";
        _executionJournal.UpsertTaggedBrokerExposureRecoveryJournal(
            bestId,
            bestIntent.TradingDate!,
            bestIntent.Stream!,
            bestIntent.ExecutionInstrument ?? bestIntent.Instrument ?? instTrim,
            brokerOid,
            repairQty,
            useDir,
            avgFill,
            intentStop,
            intentTarget,
            utcNow,
            correl);

        ClearTaggedRepairFailureCooldown(instTrim);
        resultCode = "REPAIR_UPSERT_OK";
        detail = bestId;
        _log.Write(RobotEvents.EngineBase(utcNow, bestIntent.TradingDate ?? "", "TAGGED_BROKER_WITHOUT_JOURNAL_REPAIR_COMPLETE", "ENGINE",
            new
            {
                instrument = instTrim,
                intent_id = bestId,
                open_journal_qty = repairQty,
                correlation_id = correl,
                result = resultCode
            }));
        return true;
    }

    /// <summary>
    /// Get current position quantity for instrument using real NT API.
    /// </summary>
    private int GetCurrentPositionReal(string instrument)
    {
        if (_ntAccount == null || _ntInstrument == null)
        {
            return 0;
        }
        
        var account = _ntAccount as Account;
        var ntInstrument = _ntInstrument as Instrument;
        
        if (account == null || ntInstrument == null)
        {
            return 0;
        }
        
        try
        {
            // Get position - use dynamic to handle different API signatures
            dynamic dynAccountPos = account;
            Position? position = null;
            try
            {
                position = dynAccountPos.GetPosition(ntInstrument);
            }
            catch
            {
                // Try alternative signature - GetPosition might take instrument name string
                try
                {
                    position = dynAccountPos.GetPosition(ntInstrument.MasterInstrument.Name);
                }
                catch
                {
                    return 0;
                }
            }
            return position?.Quantity ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Suppress duplicate NT order callbacks: same instrument + order_id + order_state within 50ms.</summary>
    private bool TrySkipDuplicateOrderUpdate50ms(string instrument, string orderId, string orderState, DateTimeOffset utcNow, out long skippedCount)
    {
        skippedCount = 0;
        var inst = string.IsNullOrEmpty(instrument) ? "_" : instrument.Trim();
        var oid = orderId ?? "";
        var key = inst + "|" + oid + "|" + orderState;
        lock (_callbackDedupLock)
        {
            if (_orderCallbackDedup50ms.TryGetValue(key, out var ent))
            {
                if ((utcNow - ent.LastUtc).TotalMilliseconds < 50.0)
                {
                    ent.TotalSkips++;
                    skippedCount = ent.TotalSkips;
                    return true;
                }
                ent.LastUtc = utcNow;
                ent.TotalSkips = 0;
                return false;
            }
            _orderCallbackDedup50ms[key] = new OrderCallbackDedupEntry { LastUtc = utcNow, TotalSkips = 0 };
            return false;
        }
    }

    private static string BuildPermanentExecutionDedupKey(string instrument, string? executionId, string? brokerOrderId, int fillQty)
    {
        var inst = string.IsNullOrEmpty(instrument) ? "_" : instrument.Trim();
        var q = fillQty.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(executionId))
            return inst + "|" + executionId.Trim() + "|q:" + q;
        var oid = brokerOrderId ?? "";
        return inst + "|noid:" + oid + "|q:" + q;
    }

    /// <summary>First occurrence of <paramref name="dedupKey"/> returns true (process). Repeats return false (skip) and bump skip counter.</summary>
    private bool TryMarkFirstPermanentExecutionProcessing(string dedupKey, out long skippedCount)
    {
        skippedCount = 0;
        if (_permanentExecutionProcessed.TryAdd(dedupKey, 0))
            return true;
        skippedCount = _permanentExecutionDedupSkipCount.AddOrUpdate(dedupKey, 1L, static (_, v) => v + 1);
        return false;
    }

}

#endif
