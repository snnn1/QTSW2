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
    /// Phase 3: Evaluate break-even. When IEA enabled, delegates to IEA. When not, runs BE logic (legacy path).
    /// Timeout check runs first (same tick path) — post-read, retry, or STOP_MODIFY_FAILED.
    /// </summary>
    public void EvaluateBreakEven(decimal tickPrice, DateTimeOffset? tickTimeFromEvent, string executionInstrument)
    {
#if NINJATRADER
        CheckPendingBETimeouts(tickTimeFromEvent ?? DateTimeOffset.UtcNow);
        if (_useInstrumentExecutionAuthority && _iea != null)
        {
            // NT THREADING FIX: order.Change() must run on strategy thread (OnMarketData context).
            // IEA worker caused stop ping-pong (49604↔49305) and BE not sticking. Same pattern as entry submission.
            lock (_iea.EntrySubmissionLock)
            {
                var eventTime = tickTimeFromEvent ?? DateTimeOffset.UtcNow;
                var hasEventTime = tickTimeFromEvent.HasValue;
                _iea.EvaluateBreakEvenDirect(tickPrice, eventTime, hasEventTime, executionInstrument);
            }
        }
        else
        {
            EvaluateBreakEvenCoreImpl(tickPrice, tickTimeFromEvent ?? DateTimeOffset.UtcNow, executionInstrument);
        }
#endif
    }

    /// <summary>
    /// STEP 5: Break-Even Modification.
    /// </summary>
    public OrderModificationResult ModifyStopToBreakEven(
        string intentId,
        string instrument,
        decimal beStopPrice,
        DateTimeOffset utcNow) =>
        ModifyStopToBreakEven(intentId, instrument, beStopPrice, utcNow, retryCount: 0);

    /// <summary>
    /// Internal overload for retry path (retryCount > 0).
    /// </summary>
    internal OrderModificationResult ModifyStopToBreakEven(
        string intentId,
        string instrument,
        decimal beStopPrice,
        DateTimeOffset utcNow,
        int retryCount)
    {
        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "STOP_MODIFY_ATTEMPT", new
        {
            be_stop_price = beStopPrice,
            account = "SIM",
            retry_count = retryCount
        }));

        if (!_simAccountVerified)
        {
            var error = "SIM account not verified";
            return OrderModificationResult.FailureResult(error, utcNow);
        }

        try
        {
            // STEP 5: Check journal to prevent duplicate BE modifications
            // CRITICAL: Use tradingDate and stream from intent - empty strings never match journal keys
            var tradingDate = "";
            var stream = "";
            if (IntentMap.TryGetValue(intentId, out var intentForBe))
            {
                tradingDate = intentForBe.TradingDate ?? "";
                stream = intentForBe.Stream ?? "";
            }
            if (_executionJournal.IsBEModified(intentId, tradingDate, stream))
            {
                var error = "BE modification already attempted for this intent";
                _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "STOP_MODIFY_SKIPPED", new
                {
                    reason = "DUPLICATE_BE_MODIFICATION",
                    account = "SIM"
                }));
                return OrderModificationResult.FailureResult(error, utcNow);
            }
            
#if !NINJATRADER
            var error = "CRITICAL: NINJATRADER preprocessor directive is NOT defined. " +
                       "Add <DefineConstants>NINJATRADER</DefineConstants> to your .csproj file and rebuild. " +
                       "Mock mode has been removed - only real NT API execution is supported.";
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "STOP_MODIFY_FAIL", new
            {
                error,
                reason = "NINJATRADER_NOT_DEFINED"
            }));
            return OrderModificationResult.FailureResult(error, utcNow);
#endif

            if (!_ntContextSet)
            {
                var error = "CRITICAL: NT context is not set. " +
                           "SetNTContext() must be called by RobotSimStrategy before orders can be placed. " +
                           "Mock mode has been removed - only real NT API execution is supported.";
                _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "STOP_MODIFY_FAIL", new
                {
                    error,
                    reason = "NT_CONTEXT_NOT_SET"
                }));
                return OrderModificationResult.FailureResult(error, utcNow);
            }

            return ModifyStopToBreakEvenReal(intentId, instrument, beStopPrice, utcNow, retryCount);
        }
        catch (Exception ex)
        {
            // Journal: BE_MODIFY_FAILED
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "STOP_MODIFY_FAIL", new
            {
                error = ex.Message,
                account = "SIM"
            }));
            
            return OrderModificationResult.FailureResult($"BE modification failed: {ex.Message}", utcNow);
        }
    }

    /// <summary>Quantize price to tick using deterministic rounding (MidpointRounding.AwayFromZero).</summary>
    private static decimal QuantizeToTick(decimal price, decimal tickSize)
    {
        if (tickSize <= 0) return price;
        return Math.Round(price / tickSize, 0, MidpointRounding.AwayFromZero) * tickSize;
    }

    /// <summary>Purge pending BE for intent on exit (target/stop fill, flatten, journal close). Emit STOP_MODIFY_PENDING_CLEARED_ON_EXIT.</summary>
    internal void PurgePendingBEForIntent(string intentId, DateTimeOffset utcNow, string instrument = "", string reason = "exit")
    {
        var purged = new List<string>();
        string? lastInstrument = null;
        foreach (var kvp in _pendingBERequests.ToArray())
        {
            if (kvp.Value.IntentId == intentId)
            {
                lastInstrument = kvp.Value.Instrument;
                if (_pendingBERequests.TryRemove(kvp.Key, out _))
                    purged.Add(kvp.Key);
            }
        }
        if (purged.Count > 0)
        {
            _pendingBECancelUtcByIntent.TryRemove(intentId, out _);
        _pendingBECancelReplaceByIntent.TryRemove(intentId, out _);
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, lastInstrument ?? instrument, "STOP_MODIFY_PENDING_CLEARED_ON_EXIT", new
            {
                intent_id = intentId,
                purged_stop_order_ids = purged,
                reason
            }));
        }
    }

    /// <summary>Get tick size for instrument. YM: 1, CL: 0.01, etc.</summary>
    private static decimal GetTickSizeForInstrument(string instrument)
    {
        var u = (instrument ?? "").ToUpperInvariant();
        if (u.Contains("YM") || u.Contains("MYM")) return 1m;
        if (u.Contains("CL") || u.Contains("MCL")) return 0.01m;
        if (u.Contains("NQ") || u.Contains("MNQ") || u.Contains("M2K")) return 0.25m;
        if (u.Contains("ES") || u.Contains("MES")) return 0.25m;
        if (u.Contains("GC") || u.Contains("MGC")) return 0.1m;
        if (u.Contains("NG") || u.Contains("MNG")) return 0.001m;
        return 0.01m; // default
    }

}
