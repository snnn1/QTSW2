using System;
using System.IO;

namespace QTSW2.Robot.Core;

/// <summary>
/// Opt-in wall-clock attribution for high CPU investigations in NinjaTrader.
/// Create empty file <c>{projectRoot}/data/engine_cpu_profile.enabled</c> then restart strategies;
/// remove file to disable. Flag is re-checked every few seconds.
/// </summary>
public static class EngineCpuProfile
{
    private static string? _root;
    private static DateTimeOffset _lastCheckUtc = DateTimeOffset.MinValue;
    private static bool _cached;

    private const string RelativeFlagPath = "data/engine_cpu_profile.enabled";
    private const double RecheckSeconds = 5.0;

    public static void SetRoot(string projectRoot)
    {
        _root = string.IsNullOrWhiteSpace(projectRoot) ? null : projectRoot;
    }

    public static bool IsEnabled()
    {
        if (string.IsNullOrEmpty(_root))
            return false;

        var now = DateTimeOffset.UtcNow;
        if ((now - _lastCheckUtc).TotalSeconds < RecheckSeconds)
            return _cached;

        _lastCheckUtc = now;
        try
        {
            _cached = File.Exists(Path.Combine(_root, RelativeFlagPath));
        }
        catch
        {
            _cached = false;
        }

        return _cached;
    }
}
