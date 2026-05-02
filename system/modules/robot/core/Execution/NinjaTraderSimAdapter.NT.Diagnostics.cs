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
