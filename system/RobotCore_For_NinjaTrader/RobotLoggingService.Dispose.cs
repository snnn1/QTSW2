// SINGLE SOURCE OF TRUTH
// This file is the authoritative implementation of RobotLoggingService.
// It is compiled into Robot.Core.dll and should be referenced from that DLL.
// Do not duplicate this file elsewhere - if source is needed, reference Robot.Core.dll instead.
//
// Linked into: Robot.Core.csproj (modules/robot/core/)
// Referenced by: RobotCore_For_NinjaTrader (via Robot.Core.dll)

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace QTSW2.Robot.Core;

public sealed partial class RobotLoggingService
{
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _cancellationTokenSource.Dispose();
    }

    /// <summary>
    /// Get the current singleton instance (for testing/debugging).
    /// Returns null if no instance exists.
    /// </summary>
    public static RobotLoggingService? GetInstance(string configRoot, string? robotLogDirectory = null)
    {
        var key = robotLogDirectory ?? RobotRunArtifactPaths.LogsRobot(configRoot);
        lock (_instancesLock)
        {
            _instances.TryGetValue(key, out var instance);
            return instance;
        }
    }
}
