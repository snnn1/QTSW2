#!/usr/bin/env python3
"""Insert replay logic into Program.cs"""

file_path = "modules/robot/harness/Program.cs"

with open(file_path, 'r', encoding='utf-8') as f:
    lines = f.readlines()

replay_lines = '''// Load spec and time service early (needed for replay)
var specLoaded = ParitySpec.LoadFromFile(Path.Combine(root, "configs", "analyzer_robot_parity.json"));
var timeSvc = new TimeService(specLoaded.Timezone);

// Handle replay mode
if (argsList.Contains("--replay"))
{
    // Parse start and end dates
    var startIndex = argsList.IndexOf("--start");
    var endIndex = argsList.IndexOf("--end");
    
    if (startIndex < 0 || startIndex + 1 >= argsList.Count || endIndex < 0 || endIndex + 1 >= argsList.Count)
    {
        Console.Error.WriteLine("Error: --replay requires --start YYYY-MM-DD and --end YYYY-MM-DD");
        return;
    }
    
    if (!DateOnly.TryParse(argsList[startIndex + 1], out var startDate))
    {
        Console.Error.WriteLine($"Error: Invalid start date: {argsList[startIndex + 1]}");
        return;
    }
    
    if (!DateOnly.TryParse(argsList[endIndex + 1], out var endDate))
    {
        Console.Error.WriteLine($"Error: Invalid end date: {argsList[endIndex + 1]}");
        return;
    }
    
    // Parse optional log directory
    string? logDir = null;
    var logDirIndex = argsList.IndexOf("--log-dir");
    if (logDirIndex >= 0 && logDirIndex + 1 < argsList.Count)
    {
        logDir = argsList[logDirIndex + 1];
    }
    
    Console.WriteLine($"Replay mode: {startDate:yyyy-MM-dd} to {endDate:yyyy-MM-dd}");
    if (logDir != null)
        Console.WriteLine($"Custom log directory: {logDir}");
    
    // Initialize engine with custom log directory
    var engine = new RobotEngine(root, TimeSpan.FromSeconds(1), mode, logDir);
    engine.Start();
    
    // Get snapshot root
    var snapshotRoot = Path.Combine(root, "data", "translated_test");
    
    // Run replay
    HistoricalReplay.Replay(engine, specLoaded, timeSvc, snapshotRoot, root, startDate, endDate);
    
    engine.Stop();
    Console.WriteLine($"Done. Inspect logs in: {logDir ?? "logs/robot/"}");
    return;
}

'''.splitlines(keepends=True)

new_lines = []
i = 0
inserted = False
skip_duplicate_spec = False

while i < len(lines):
    line = lines[i]
    
    # Insert after "Test scenario:" line, before engine creation
    if not inserted and 'Console.WriteLine($"Test scenario: {testScenario}");' in line:
        new_lines.append(line)
        i += 1
        # Insert replay code
        new_lines.extend(replay_lines)
        inserted = True
        skip_duplicate_spec = True
        continue
    
    # Skip duplicate specLoaded/timeSvc if we just inserted
    if skip_duplicate_spec:
        if 'var specLoaded = ParitySpec.LoadFromFile' in line or 'var timeSvc = new TimeService' in line:
            i += 1
            continue
        if '// Simulate time progression' in line:
            skip_duplicate_spec = False
    
    new_lines.append(line)
    i += 1

with open(file_path, 'w', encoding='utf-8') as f:
    f.writelines(new_lines)

print("[OK] Replay code inserted")
