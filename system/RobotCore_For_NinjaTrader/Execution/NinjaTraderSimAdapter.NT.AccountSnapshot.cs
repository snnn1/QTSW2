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
    /// Get account snapshot using real NT API.
    /// </summary>
    private AccountSnapshot GetAccountSnapshotReal(DateTimeOffset utcNow)
    {
        var swSnapshot = Stopwatch.StartNew();
        if (_ntAccount == null)
        {
            return new AccountSnapshot
            {
                Positions = new List<PositionSnapshot>(),
                WorkingOrders = new List<WorkingOrderSnapshot>(),
                CapturedAtUtc = utcNow,
                IsAuthoritative = false,
                NonAuthoritativeReason = "NT_ACCOUNT_NOT_SET"
            };
        }
        
        var account = _ntAccount as Account;
        if (account == null)
        {
            return new AccountSnapshot
            {
                Positions = new List<PositionSnapshot>(),
                WorkingOrders = new List<WorkingOrderSnapshot>(),
                CapturedAtUtc = utcNow,
                IsAuthoritative = false,
                NonAuthoritativeReason = "NT_ACCOUNT_CAST_FAILED"
            };
        }
        
        var positions = new List<PositionSnapshot>();
        var workingOrders = new List<WorkingOrderSnapshot>();
        var isAuthoritative = true;
        string? nonAuthoritativeReason = null;
        
        try
        {
            // Get positions
            foreach (var position in SnapshotAccountPositions(account))
            {
                if (position.Quantity != 0)
                {
                    var brokerMarketPosition = position.MarketPosition.ToString();
                    positions.Add(new PositionSnapshot
                    {
                        Instrument = position.Instrument.MasterInstrument.Name,
                        Quantity = BrokerPositionResolver.ApplyMarketPositionSign(position.Quantity, brokerMarketPosition),
                        AveragePrice = (decimal)position.AveragePrice,
                        ContractLabel = position.Instrument.FullName,
                        MarketPosition = brokerMarketPosition
                    });
                }
            }
            
            // Get working orders
            foreach (var order in SnapshotAccountOrders(account))
            {
                if (order.OrderState == OrderState.Working || order.OrderState == OrderState.Accepted)
                {
                    workingOrders.Add(new WorkingOrderSnapshot
                    {
                        OrderId = order.OrderId,
                        Instrument = order.Instrument.MasterInstrument.Name,
                        Tag = GetOrderTag(order),
                        OcoGroup = order.Oco,
                        OrderType = order.OrderType.ToString(),
                        Price = order.OrderType == OrderType.Limit ? (decimal?)order.LimitPrice : null,
                        StopPrice = order.OrderType == OrderType.StopMarket || order.OrderType == OrderType.StopLimit ? (decimal?)order.StopPrice : null,
                        Quantity = order.Quantity
                    });
                }
            }
        }
        catch (Exception ex)
        {
            isAuthoritative = false;
            nonAuthoritativeReason = "ACCOUNT_SNAPSHOT_EXCEPTION:" + ex.GetType().Name;
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "ACCOUNT_SNAPSHOT_ERROR", state: "ENGINE",
                new
                {
                    error = ex.Message,
                    exception_type = ex.GetType().Name,
                    note = "Failed to snapshot account - returning partial snapshot"
                }));
        }

        swSnapshot.Stop();
        if (swSnapshot.ElapsedMilliseconds > 500)
        {
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "ACCOUNT_SNAPSHOT_SLOW", state: "ENGINE",
                new
                {
                    elapsed_ms = swSnapshot.ElapsedMilliseconds,
                    position_count = positions.Count,
                    working_order_count = workingOrders.Count,
                    note = "NT account snapshot enumeration exceeded 500ms"
                }));
        }

        return new AccountSnapshot
        {
            Positions = positions,
            WorkingOrders = workingOrders,
            CapturedAtUtc = utcNow,
            IsAuthoritative = isAuthoritative,
            NonAuthoritativeReason = nonAuthoritativeReason
        };
    }

}

#endif
