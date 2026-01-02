#!/usr/bin/env python3
import re

file_path = "modules/robot/harness/Program.cs"
with open(file_path, 'r', encoding='utf-8') as f:
    content = f.read()

replay_code = '''// Load spec and time service early (needed for replay)
var specLoaded = ParitySpec.LoadFromFile(Path.Combine(root, "configs", "analyzer_robot_parity.json"));
var timeSvc = new TimeService(specLoaded.Timezone);

// Handle replay mode
if (argsList.Contains("--replay"))
{
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
    string? logDir = null;
    var logDirIndex = argsList.IndexOf("--log-dir");
    if (logDirIndex >= 0 && logDirIndex + 1 < argsList.Count)
        logDir = argsList[logDirIndex + 1];
    Console.WriteLine($"Replay mode: {startDate:yyyy-MM-dd} to {endDate:yyyy-MM-dd}");
    if (logDir != null)
        Console.WriteLine($"Custom log directory: {logDir}");
    var engine = new RobotEngine(root, TimeSpan.FromSeconds(1), mode, logDir);
    engine.Start();
    var snapshotRoot = Path.Combine(root, "data", "translated_test");
    HistoricalReplay.Replay(engine, specLoaded, timeSvc, snapshotRoot, root, startDate, endDate);
    engine.Stop();
    Console.WriteLine($"Done. Inspect logs in: {logDir ?? "logs/robot/"}");
    return;
}

'''

# Find and replace
pattern = r'(if \(testScenario != null\)\s+Console\.WriteLine\(\$"Test scenario: \{testScenario\}"\);)\s+(var engine = new RobotEngine\(root, TimeSpan\.FromSeconds\(1\), mode\);)\s+engine\.Start\(\);'
replacement = r'\1\n\n' + replay_code + r'\2\n    engine.Start();'

content = re.sub(pattern, replacement, content, flags=re.MULTILINE)

# Remove duplicate specLoaded/timeSvc
content = re.sub(r'// Simulate time progression and bars \(UTC-based\)\s+var specLoaded = ParitySpec\.LoadFromFile.*?var timeSvc = new TimeService\(specLoaded\.Timezone\);', '// Simulate time progression and bars (UTC-based)', content, flags=re.DOTALL)

with open(file_path, 'w', encoding='utf-8') as f:
    f.write(content)

print("Replay code inserted")
