using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Coordinator for tracking intent exposure and validating exit operations.
/// This is a "truth keeper" - it tracks exposure per intent (not per instrument)
/// and ensures proper handling of multiple streams on the same instrument.
/// 
/// Key principle: All operations are per-intent, never cross-intent.
/// </summary>
public sealed class InstrumentIntentCoordinator
{
    private readonly RobotLogger _log;
    private readonly ConcurrentDictionary<string, IntentExposure> _exposures = new();
    private readonly Func<AccountSnapshot> _getAccountSnapshot;
    private readonly Action<string, DateTimeOffset, string>? _standDownStreamCallback;
    private readonly Func<string, string, DateTimeOffset, FlattenResult>? _flattenIntentCallback;
    private readonly Func<string, DateTimeOffset, bool>? _cancelIntentOrdersCallback;
    
    /// <summary>
    /// Constructor with callbacks for stream stand-down, flatten, and cancellation.
    /// </summary>
    public InstrumentIntentCoordinator(
        RobotLogger log,
        Func<AccountSnapshot> getAccountSnapshot,
        Action<string, DateTimeOffset, string>? standDownStreamCallback,
        Func<string, string, DateTimeOffset, FlattenResult>? flattenIntentCallback,
        Func<string, DateTimeOffset, bool>? cancelIntentOrdersCallback)
    {
        _log = log;
        _getAccountSnapshot = getAccountSnapshot;
        _standDownStreamCallback = standDownStreamCallback;
        _flattenIntentCallback = flattenIntentCallback;
        _cancelIntentOrdersCallback = cancelIntentOrdersCallback;
    }
    
    /// <summary>
    /// Responsibility 1: Register intent exposure when entry fills.
    /// Called when an entry order fills.
    /// </summary>
    /// <param name="intentId">Intent identifier</param>
    /// <param name="qty">Quantity filled</param>
    /// <param name="streamId">Stream ID (e.g., "NQ1")</param>
    /// <param name="instrument">Instrument symbol (e.g., "NQ")</param>
    /// <param name="direction">Direction ("Long" or "Short")</param>
    /// <param name="utcNow">Current UTC timestamp</param>
    public void OnEntryFill(string intentId, int qty, string streamId, string instrument, string direction, DateTimeOffset utcNow)
    {
        var exposure = _exposures.GetOrAdd(intentId, _ => new IntentExposure
        {
            IntentId = intentId,
            StreamId = streamId,
            Instrument = instrument,
            Direction = direction,
            Quantity = qty,  // Initial quantity set from first fill
            State = IntentExposureState.ACTIVE
        });
        
        // Update exposure
        if (exposure.State != IntentExposureState.CLOSED)
        {
            exposure.State = IntentExposureState.ACTIVE;
        }
        
        exposure.EntryFilledQty += qty;
        exposure.Quantity = Math.Max(exposure.Quantity, exposure.EntryFilledQty);  // Track max intended quantity
        
        _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "INTENT_EXPOSURE_REGISTERED", state: "ENGINE",
            new
            {
                intent_id = intentId,
                stream_id = streamId,
                instrument = instrument,
                direction = direction,
                entry_filled_qty = exposure.EntryFilledQty,
                remaining_exposure = exposure.RemainingExposure,
                state = exposure.State.ToString()
            }));
    }
    
    /// <summary>
    /// Responsibility 2: Handle exit fills (stop or target).
    /// Called whenever any exit order fills.
    /// </summary>
    /// <param name="intentId">Intent identifier</param>
    /// <param name="qty">Quantity filled</param>
    /// <param name="utcNow">Current UTC timestamp</param>
    public void OnExitFill(string intentId, int qty, DateTimeOffset utcNow)
    {
        if (!_exposures.TryGetValue(intentId, out var exposure))
        {
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "INTENT_EXIT_FILL_NO_EXPOSURE", state: "ENGINE",
                new
                {
                    intent_id = intentId,
                    exit_qty = qty,
                    error = "Exit fill received but no exposure found for intent"
                }));
            return;
        }
        
        // Increment exit filled quantity
        exposure.ExitFilledQty += qty;
        var remaining = exposure.RemainingExposure;
        
        _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "INTENT_EXIT_FILL", state: "ENGINE",
            new
            {
                intent_id = intentId,
                stream_id = exposure.StreamId,
                instrument = exposure.Instrument,
                exit_qty = qty,
                exit_filled_qty = exposure.ExitFilledQty,
                entry_filled_qty = exposure.EntryFilledQty,
                remaining_exposure = remaining
            }));
        
        // If exposure fully closed, mark as CLOSED and cancel remaining orders
        if (remaining <= 0)
        {
            exposure.State = IntentExposureState.CLOSED;
            
            // Cancel remaining orders for this intent only
            var cancelled = _cancelIntentOrdersCallback?.Invoke(intentId, utcNow) ?? false;
            
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "INTENT_EXPOSURE_CLOSED", state: "ENGINE",
                new
                {
                    intent_id = intentId,
                    stream_id = exposure.StreamId,
                    instrument = exposure.Instrument,
                    entry_filled_qty = exposure.EntryFilledQty,
                    exit_filled_qty = exposure.ExitFilledQty,
                    remaining_exposure = remaining,
                    orders_cancelled = cancelled
                }));
        }
        
        // Recalculate broker exposure for awareness (not action)
        RecalculateBrokerExposure(utcNow);
    }
    
    /// <summary>
    /// Responsibility 3: Validate exit orders before submission.
    /// Prevents over-closing and flipping due to duplicated orders.
    /// </summary>
    /// <param name="intentId">Intent identifier</param>
    /// <param name="qty">Quantity to submit</param>
    /// <returns>True if exit can be submitted, false otherwise</returns>
    public bool CanSubmitExit(string intentId, int qty)
    {
        if (!_exposures.TryGetValue(intentId, out var exposure))
        {
            _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", eventType: "INTENT_EXIT_VALIDATION_FAILED", state: "ENGINE",
                new
                {
                    intent_id = intentId,
                    exit_qty = qty,
                    reason = "NO_EXPOSURE_FOUND"
                }));
            return false;
        }
        
        if (exposure.State != IntentExposureState.ACTIVE)
        {
            _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", eventType: "INTENT_EXIT_VALIDATION_FAILED", state: "ENGINE",
                new
                {
                    intent_id = intentId,
                    exit_qty = qty,
                    reason = $"INTENT_NOT_ACTIVE",
                    state = exposure.State.ToString()
                }));
            return false;
        }
        
        if (qty > exposure.RemainingExposure)
        {
            _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", eventType: "INTENT_EXIT_VALIDATION_FAILED", state: "ENGINE",
                new
                {
                    intent_id = intentId,
                    exit_qty = qty,
                    remaining_exposure = exposure.RemainingExposure,
                    reason = "WOULD_OVER_CLOSE"
                }));
            return false;
        }
        
        return true;
    }
    
    /// <summary>
    /// Responsibility 4: Emergency fallback on protective failure.
    /// Only triggers on internal failure, not crashes.
    /// </summary>
    /// <param name="intentId">Intent identifier</param>
    /// <param name="streamId">Stream ID</param>
    /// <param name="utcNow">Current UTC timestamp</param>
    public void OnProtectiveFailure(string intentId, string streamId, DateTimeOffset utcNow)
    {
        if (!_exposures.TryGetValue(intentId, out var exposure))
        {
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "INTENT_PROTECTIVE_FAILURE", state: "ENGINE",
                new
                {
                    intent_id = intentId,
                    stream_id = streamId,
                    error = "No exposure found for intent"
                }));
            return;
        }
        
        exposure.State = IntentExposureState.STANDING_DOWN;
        
        // Try to flatten only this intent's exposure
        var flattenResult = _flattenIntentCallback?.Invoke(intentId, exposure.Instrument, utcNow);
        
        if (flattenResult == null || !flattenResult.Success)
        {
            // Fallback: instrument flatten + re-enter remaining intents (rare path)
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "INTENT_PROTECTIVE_FAILURE_FALLBACK", state: "ENGINE",
                new
                {
                    intent_id = intentId,
                    stream_id = streamId,
                    instrument = exposure.Instrument,
                    remaining_exposure = exposure.RemainingExposure,
                    note = "Per-intent flatten failed, using instrument flatten fallback (rare path)"
                }));
            
            // Note: Re-entering remaining intents would require additional logic
            // For now, log loudly and stand down stream
        }
        
        // Stand down stream
        _standDownStreamCallback?.Invoke(streamId, utcNow, $"PROTECTIVE_FAILURE:{intentId}");
        
        _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "INTENT_PROTECTIVE_FAILURE", state: "ENGINE",
            new
            {
                intent_id = intentId,
                stream_id = streamId,
                instrument = exposure.Instrument,
                remaining_exposure = exposure.RemainingExposure,
                flatten_success = flattenResult?.Success ?? false,
                note = "Intent standing down due to protective failure"
            }));
    }
    
    /// <summary>
    /// Get intent exposure for a specific intent.
    /// </summary>
    public IntentExposure? GetExposure(string intentId)
    {
        return _exposures.TryGetValue(intentId, out var exposure) ? exposure : null;
    }
    
    /// <summary>
    /// Get all active exposures for an instrument (for awareness only).
    /// </summary>
    public List<IntentExposure> GetActiveExposuresForInstrument(string instrument)
    {
        return _exposures.Values
            .Where(e => e.Instrument == instrument && e.State == IntentExposureState.ACTIVE)
            .ToList();
    }
    
    /// <summary>
    /// Recalculate broker exposure from account snapshot (for awareness only, not action).
    /// </summary>
    private void RecalculateBrokerExposure(DateTimeOffset utcNow)
    {
        try
        {
            var snapshot = _getAccountSnapshot();
            if (snapshot?.Positions != null)
            {
                // Calculate net exposure per instrument (for logging/awareness)
                var instrumentExposures = snapshot.Positions
                    .GroupBy(p => p.Instrument)
                    .Select(g => new BrokerExposure
                    {
                        Instrument = g.Key,
                        NetQuantity = g.Sum(p => p.Quantity)
                    })
                    .ToList();
                
                // Log for awareness (not action)
                foreach (var brokerExp in instrumentExposures)
                {
                    var intentExposures = GetActiveExposuresForInstrument(brokerExp.Instrument);
                    var intentNetQty = intentExposures.Sum(e => 
                        e.Direction == "Long" ? e.RemainingExposure : -e.RemainingExposure);
                    
                    if (brokerExp.NetQuantity != intentNetQty)
                    {
                        _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "BROKER_EXPOSURE_MISMATCH", state: "ENGINE",
                            new
                            {
                                instrument = brokerExp.Instrument,
                                broker_net_qty = brokerExp.NetQuantity,
                                intent_net_qty = intentNetQty,
                                active_intent_count = intentExposures.Count,
                                note = "Broker exposure differs from intent exposure (for awareness only)"
                            }));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Don't fail on exposure recalculation - it's for awareness only
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "BROKER_EXPOSURE_RECALC_ERROR", state: "ENGINE",
                new
                {
                    error = ex.Message,
                    exception_type = ex.GetType().Name
                }));
        }
    }
}
