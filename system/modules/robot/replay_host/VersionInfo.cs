using System;

namespace QTSW2.Robot.ReplayHost;

/// <summary>
/// Host version for traceability. Set REPLAY_HOST_VERSION env var (e.g. git commit) in CI.
/// </summary>
public static class VersionInfo
{
    public static string Version => Environment.GetEnvironmentVariable("REPLAY_HOST_VERSION") ?? "dev";
}
