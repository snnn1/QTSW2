using System;
using System.IO;

namespace QTSW2.Robot.Core;

public static class ProjectRootResolver
{
    public static string ResolveProjectRoot()
    {
        // Check for explicit environment variable override (for NinjaTrader execution)
        var envRoot = Environment.GetEnvironmentVariable("QTSW2_PROJECT_ROOT");
        if (!string.IsNullOrEmpty(envRoot))
        {
            // Verify the directory exists
            if (!Directory.Exists(envRoot))
            {
                throw new DirectoryNotFoundException($"QTSW2_PROJECT_ROOT environment variable points to non-existent directory: {envRoot}");
            }

            // Verify configs/analyzer_robot_parity.json exists
            var configPath = Path.Combine(envRoot, "configs", "analyzer_robot_parity.json");
            if (!File.Exists(configPath))
            {
                throw new DirectoryNotFoundException($"QTSW2_PROJECT_ROOT environment variable is set but configs/analyzer_robot_parity.json not found at: {configPath}");
            }

            return envRoot;
        }

        // Fallback: Hard requirement: paths are relative to project root.
        // We resolve by walking up from cwd until we find configs/analyzer_robot_parity.json.
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "configs", "analyzer_robot_parity.json");
            if (File.Exists(candidate))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate project root (configs/analyzer_robot_parity.json not found in any parent directory).");
    }

    /// <summary>
    /// Active run artifact root (same as engine <c>_persistenceBase</c> when set by <see cref="RobotEngine"/> at startup).
    /// Falls back to <see cref="ResolveProjectRoot"/> when unset (e.g. tests, tools without engine).
    /// </summary>
    public static string ResolveRunArtifactRoot()
    {
        var p = Environment.GetEnvironmentVariable("QTSW2_ROBOT_PERSISTENCE_BASE");
        if (!string.IsNullOrWhiteSpace(p))
        {
            try
            {
                var full = Path.GetFullPath(p.Trim());
                if (Directory.Exists(full)) return full;
            }
            catch { /* fall through */ }
        }

        return ResolveProjectRoot();
    }
}

