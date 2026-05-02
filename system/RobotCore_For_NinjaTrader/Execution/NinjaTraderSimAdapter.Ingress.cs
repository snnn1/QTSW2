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
    /// STEP 3: Handle NT OrderUpdate event (called by Strategy host).
    /// Public method for Strategy to forward NT events.
    /// </summary>
    public void HandleOrderUpdate(object order, object orderUpdate)
    {
#if !NINJATRADER
        var error = "CRITICAL: NINJATRADER preprocessor directive is NOT defined. " +
                   "Add <DefineConstants>NINJATRADER</DefineConstants> to your .csproj file and rebuild. " +
                   "Mock mode has been removed - only real NT API execution is supported.";
        _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", eventType: "EXECUTION_BLOCKED", state: "ENGINE",
            new { reason = "NINJATRADER_NOT_DEFINED", error }));
        throw new InvalidOperationException(error);
#endif
        // Gap 2: When IEA enabled, route through queue so OrderMap mutations run on worker (single mutation lane).
        if (_useInstrumentExecutionAuthority && _iea != null)
        {
            _iea.EnqueueOrderUpdate(order, orderUpdate);
        }
        else
        {
            HandleOrderIngressFromNt(order, orderUpdate);
        }
    }

    /// <summary>
    /// STEP 3: Handle NT ExecutionUpdate event (called by Strategy host).
    /// Public method for Strategy to forward NT events.
    /// </summary>
    public void HandleExecutionUpdate(object execution, object order)
    {
#if !NINJATRADER
        var error = "CRITICAL: NINJATRADER preprocessor directive is NOT defined. " +
                   "Add <DefineConstants>NINJATRADER</DefineConstants> to your .csproj file and rebuild. " +
                   "Mock mode has been removed - only real NT API execution is supported.";
        _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", eventType: "EXECUTION_BLOCKED", state: "ENGINE",
            new { reason = "NINJATRADER_NOT_DEFINED", error }));
        throw new InvalidOperationException(error);
#endif
        if (_useInstrumentExecutionAuthority && _iea != null)
        {
            var utcNow = DateTimeOffset.UtcNow;
            var key = $"{_iea.AccountName}:{_iea.ExecutionInstrumentKey}";
            if (!_lastIeaExecUpdateRoutedUtc.TryGetValue(key, out var last) || (utcNow - last).TotalSeconds >= 1)
            {
                _lastIeaExecUpdateRoutedUtc[key] = utcNow;
                _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "IEA_EXEC_UPDATE_ROUTED", state: "ENGINE",
                    new { account_name = _iea.AccountName, execution_instrument_key = _iea.ExecutionInstrumentKey, iea_instance_id = _iea.InstanceId }));
            }
            _iea.EnqueueExecutionUpdate(execution, order);
        }
        else
        {
            HandleExecutionIngressFromNt(execution, order);
        }
    }

    /// <summary>
    /// Phase 1: Process pending unresolved executions (non-IEA path). Called from strategy thread on OnBarUpdate/OnMarketData.
    /// </summary>
    public void ProcessPendingUnresolvedExecutions()
    {
        if (_useInstrumentExecutionAuthority) return;
        List<UnresolvedExecutionRecord> snapshot;
        lock (_pendingUnresolvedLock)
        {
            if (_pendingUnresolvedExecutions.Count == 0) return;
            snapshot = new List<UnresolvedExecutionRecord>(_pendingUnresolvedExecutions);
            _pendingUnresolvedExecutions.Clear();
        }
        foreach (var record in snapshot)
            ProcessUnresolvedRetry(record);
    }

}
