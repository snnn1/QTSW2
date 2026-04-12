using System;
using System.IO;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Global kill switch: non-negotiable safety control.
/// If enabled, blocks ALL order execution (SIM and LIVE).
/// 
/// FAIL-CLOSED BEHAVIOR: If the kill switch file cannot be read or parsed,
/// the kill switch defaults to ENABLED (blocking execution) for safety.
/// </summary>
public sealed class KillSwitch
{
    private readonly string _killSwitchPath;
    private readonly RobotLogger _log;
    private DateTimeOffset _lastCheckUtc = DateTimeOffset.MinValue;
    private KillSwitchState? _cachedState;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(5);

    public KillSwitch(string projectRoot, RobotLogger log)
    {
        _killSwitchPath = Path.Combine(projectRoot, "configs", "robot", "kill_switch.json");
        _log = log;
        
        // Log kill switch initialization at startup
        var now = DateTimeOffset.UtcNow;
        if (File.Exists(_killSwitchPath))
        {
            try
            {
                var json = File.ReadAllText(_killSwitchPath);
                var state = JsonUtil.Deserialize<KillSwitchState>(json);
                if (state != null)
                {
                    _log.Write(RobotEvents.EngineBase(now, "", "KILL_SWITCH_INITIALIZED", "ENGINE",
                        new
                        {
                            enabled = state.Enabled,
                            message = state.Message ?? "No message",
                            kill_switch_path = _killSwitchPath,
                            file_found = true,
                            note = state.Enabled 
                                ? "Kill switch is ENABLED - all execution is blocked" 
                                : "Kill switch is DISABLED - execution allowed"
                        }));
                }
                else
                {
                    _log.Write(RobotEvents.EngineBase(now, "", "KILL_SWITCH_INITIALIZED", "ENGINE",
                        new
                        {
                            enabled = true,
                            reason = "DESERIALIZATION_NULL",
                            kill_switch_path = _killSwitchPath,
                            file_found = true,
                            warning = "Kill switch file exists but deserialization returned null - defaulting to ENABLED (fail-closed)",
                            note = "Execution blocked by default - fail-closed behavior activated for safety"
                        }));
                }
            }
            catch (Exception ex)
            {
                _log.Write(RobotEvents.EngineBase(now, "", "KILL_SWITCH_INITIALIZED", "ENGINE",
                    new
                    {
                        enabled = true,
                        reason = "READ_ERROR",
                        error = ex.Message,
                        error_type = ex.GetType().Name,
                        kill_switch_path = _killSwitchPath,
                        file_found = true,
                        warning = "Kill switch file exists but could not be read - defaulting to ENABLED (fail-closed)",
                        note = "Execution blocked by default - fail-closed behavior activated for safety"
                    }));
            }
        }
        else
        {
            _log.Write(RobotEvents.EngineBase(now, "", "KILL_SWITCH_INITIALIZED", "ENGINE",
                new
                {
                    enabled = true,
                    reason = "FILE_NOT_FOUND",
                    kill_switch_path = _killSwitchPath,
                    file_found = false,
                    warning = "Kill switch file not found - defaulting to ENABLED (fail-closed)",
                    note = "Execution blocked by default - fail-closed behavior activated for safety"
                }));
        }
    }

    /// <summary>
    /// Check if kill switch is enabled.
    /// </summary>
    public bool IsEnabled()
    {
        var now = DateTimeOffset.UtcNow;
        
        // Cache check result for 5 seconds to avoid excessive file I/O
        if (_cachedState != null && (now - _lastCheckUtc) < CacheTtl)
        {
            return _cachedState.Enabled;
        }

        _lastCheckUtc = now;

        if (!File.Exists(_killSwitchPath))
        {
            // Kill switch file doesn't exist = enabled (fail closed for safety)
            _cachedState = new KillSwitchState { Enabled = true, Message = "Kill switch file not found - execution blocked by default" };
            _log.Write(RobotEvents.EngineBase(now, "", "KILL_SWITCH_ERROR_FAIL_CLOSED", "ENGINE",
                new
                {
                    enabled = true,
                    reason = "FILE_NOT_FOUND",
                    message = "Kill switch file does not exist",
                    kill_switch_path = _killSwitchPath,
                    note = "Execution blocked by default - fail-closed behavior activated for safety"
                }));
            return true;
        }

        try
        {
            var json = File.ReadAllText(_killSwitchPath);
            var state = JsonUtil.Deserialize<KillSwitchState>(json);
            
            if (state == null)
            {
                // Deserialization returned null = enabled (fail closed for safety)
                _cachedState = new KillSwitchState { Enabled = true, Message = "Kill switch file deserialization failed - execution blocked by default" };
                _log.Write(RobotEvents.EngineBase(now, "", "KILL_SWITCH_ERROR_FAIL_CLOSED", "ENGINE",
                    new
                    {
                        enabled = true,
                        reason = "DESERIALIZATION_NULL",
                        message = "Kill switch file deserialization returned null",
                        kill_switch_path = _killSwitchPath,
                        note = "Execution blocked by default - fail-closed behavior activated for safety"
                    }));
                return true;
            }

            _cachedState = state;

            if (state.Enabled)
            {
                // Log kill switch active (once per check period)
                _log.Write(RobotEvents.EngineBase(now, "", "KILL_SWITCH_ACTIVE", "ENGINE",
                    new
                    {
                        enabled = true,
                        message = state.Message ?? "Kill switch is active",
                        note = "All order execution is blocked"
                    }));
            }

            return state.Enabled;
        }
        catch (Exception ex)
        {
            // If kill switch file is corrupted or unreadable, treat as enabled (fail closed for safety)
            _cachedState = new KillSwitchState { Enabled = true, Message = $"Kill switch read error: {ex.Message} - execution blocked by default" };
            _log.Write(RobotEvents.EngineBase(now, "", "KILL_SWITCH_ERROR_FAIL_CLOSED", "ENGINE",
                new
                {
                    enabled = true,
                    reason = "READ_ERROR",
                    error = ex.Message,
                    error_type = ex.GetType().Name,
                    kill_switch_path = _killSwitchPath,
                    message = "Kill switch state could not be determined",
                    note = "Execution blocked by default - fail-closed behavior activated for safety"
                }));
            return true;
        }
    }
}

/// <summary>
/// Kill switch state (persisted in config file).
/// </summary>
public class KillSwitchState
{
    public bool Enabled { get; set; }

    public string? Message { get; set; }
}
