namespace QTSW2.Robot.Core;

public static class ProjectRootResolver
{
    public static string ResolveProjectRoot()
    {
        // Hard requirement: paths are relative to project root.
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
}

