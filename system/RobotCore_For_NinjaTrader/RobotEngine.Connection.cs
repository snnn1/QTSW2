using System;
using System.Collections.Generic;
using System.Linq;

namespace QTSW2.Robot.Core;

public sealed partial class RobotEngine
{
    /// <summary>
    /// Forward mid-session restart evidence to the health monitor.
    /// </summary>
    public void OnMidSessionRestartDetected(string streamId, string tradingDate, string previousState, DateTimeOffset previousUpdateUtc, DateTimeOffset restartUtc)
    {
        _healthMonitor?.OnMidSessionRestartDetected(streamId, tradingDate, previousState, previousUpdateUtc, restartUtc);
    }

    /// <summary>
    /// Handle connection status update from NinjaTrader.
    ///
    /// State transitions:
    /// - CONNECTED_OK -> DISCONNECT_FAIL_CLOSED: On first disconnect
    /// - DISCONNECT_FAIL_CLOSED -> RECONNECTED_RECOVERY_PENDING: On reconnect
    /// - RECONNECTED_RECOVERY_PENDING -> RECOVERY_COMPLETE: After broker sync completes
    /// - RECOVERY_COMPLETE -> CONNECTED_OK: Normal operation restored
    ///
    /// Execution behavior during disconnect states:
    /// During DISCONNECT_FAIL_CLOSED and RECONNECTED_RECOVERY_PENDING, stream-attributed risk-increasing
    /// submits that call RiskGate.CheckGates are blocked. Risk-reducing paths (flatten, cancel, BE)
    /// do not use that stream gate; other EPA / adapter rules still apply.
    /// </summary>
    public void OnConnectionStatusUpdate(ConnectionStatus status, string connectionName)
    {
        lock (_engineLock)
        {
            var utcNow = DateTimeOffset.UtcNow;
            var wasConnected = _lastConnectionStatus == ConnectionStatus.Connected;
            var isConnected = status == ConnectionStatus.Connected;

            // Update trading date in health monitor on every call (prevents regression to empty string, handles day rollover)
            _healthMonitor?.SetTradingDate(TradingDateString);

            // Forward to health monitor
            _healthMonitor?.OnConnectionStatusUpdate(status, connectionName, utcNow);

            // Handle recovery state transitions
            if (wasConnected && !isConnected)
            {
                // First disconnect: transition to DISCONNECT_FAIL_CLOSED
                if (_recoveryState == ConnectionRecoveryState.CONNECTED_OK || _recoveryState == ConnectionRecoveryState.RECOVERY_COMPLETE)
                {
                    _recoveryState = ConnectionRecoveryState.DISCONNECT_FAIL_CLOSED;
                    _secondReconciliationRunUtc = null; // Reset for new recovery cycle
                    if (!_disconnectFirstUtc.HasValue)
                    {
                        _disconnectFirstUtc = utcNow;
                    }

                    var payload = new Dictionary<string, object>
                    {
                        ["recovery_state"] = _recoveryState.ToString(),
                        ["disconnect_first_utc"] = _disconnectFirstUtc.Value.ToString("o"),
                        ["connection_status"] = status.ToString(),
                        ["connection_name"] = connectionName,
                        ["execution_mode"] = _executionMode.ToString(),
                        ["active_stream_count"] = _streams.Count(s => !s.Value.Committed)
                    };

                    // Audit clarity: include run_id and trading_date when known
                    if (!string.IsNullOrWhiteSpace(_runId))
                        payload["run_id"] = _runId;
                    if (!string.IsNullOrWhiteSpace(TradingDateString))
                        payload["trading_date"] = TradingDateString;

                    LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "DISCONNECT_FAIL_CLOSED_ENTERED", state: "ENGINE",
                        payload));

                    // Report critical event to HealthMonitor for notification
                    _healthMonitor?.ReportCritical("DISCONNECT_FAIL_CLOSED_ENTERED", payload);
                }
            }
            else if (!wasConnected && isConnected)
            {
                // Reconnect: transition to RECONNECTED_RECOVERY_PENDING
                if (_recoveryState == ConnectionRecoveryState.DISCONNECT_FAIL_CLOSED)
                {
                    _recoveryState = ConnectionRecoveryState.RECONNECTED_RECOVERY_PENDING;
                    _reconnectUtc = utcNow; // Set reconnect timestamp (makes "after reconnect" comparisons unambiguous)

                    LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "DISCONNECT_RECOVERY_STARTED", state: "ENGINE",
                        new
                        {
                            recovery_state = _recoveryState.ToString(),
                            reconnect_utc = _reconnectUtc.Value.ToString("o"),
                            disconnect_first_utc = _disconnectFirstUtc?.ToString("o"),
                            connection_status = status.ToString(),
                            connection_name = connectionName,
                            note = "Recovery started - waiting for broker synchronization before proceeding"
                        }));

                    // Reset broker sync timestamps to ensure we only count updates after reconnect
                    _lastOrderUpdateUtc = null;
                    _lastExecutionUpdateUtc = null;
                }
            }

            _lastConnectionStatus = status;
        }
    }
}
