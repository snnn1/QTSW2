using System;
using System.IO;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Global kill switch: non-negotiable safety control.
/// If enabled, blocks ALL order execution (SIM and LIVE).
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
            // Kill switch file doesn't exist = disabled (fail open for safety)
            _cachedState = new KillSwitchState { Enabled = false, Message = null };
            return false;
        }

        try
        {
            var json = File.ReadAllText(_killSwitchPath);
            var state = JsonUtil.Deserialize<KillSwitchState>(json);
            
            if (state == null)
            {
                _cachedState = new KillSwitchState { Enabled = false, Message = null };
                return false;
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
            // If kill switch file is corrupted, treat as disabled (fail open)
            _log.Write(RobotEvents.EngineBase(now, "", "KILL_SWITCH_ERROR", "ENGINE",
                new
                {
                    error = ex.Message,
                    note = "Treating kill switch as disabled due to error"
                }));
            
            _cachedState = new KillSwitchState { Enabled = false, Message = null };
            return false;
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
