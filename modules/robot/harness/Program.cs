using System.Text.Json;
using QTSW2.Robot.Core;
using QTSW2.Robot.Core.Execution;
using QTSW2.Robot.Harness;

static void PrintUsage()
{
    Console.WriteLine("Robot Harness (non-NinjaTrader)");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --write-sample-timetable");
    Console.WriteLine("  dotnet run --project modules/robot/harness/Robot.Harness.csproj [--mode DRYRUN|SIM|LIVE] [--test SCENARIO]");
    Console.WriteLine("  dotnet run --project modules/robot/harness/Robot.Harness.csproj --mode DRYRUN --replay --start YYYY-MM-DD --end YYYY-MM-DD [--log-dir DIR] [--timetable-path PATH]");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  --mode DRYRUN|SIM|LIVE  Execution mode (default: DRYRUN)");
    Console.WriteLine("  --replay                Replay historical bars from snapshot (requires --start and --end)");
    Console.WriteLine("  --start YYYY-MM-DD      Start date for replay (Chicago timezone)");
    Console.WriteLine("  --end YYYY-MM-DD        End date for replay (Chicago timezone)");
    Console.WriteLine("  --log-dir DIR           Custom log directory (default: logs/robot/)");
    Console.WriteLine("  --timetable-path PATH   Custom timetable file path (default: data/timetable/timetable_current.json)");
    Console.WriteLine();
    Console.WriteLine("Notes:");
    Console.WriteLine("- Engine reads ONLY from:");
    Console.WriteLine("  configs/analyzer_robot_parity.json");
    Console.WriteLine("  data/timetable/timetable_current.json");
    Console.WriteLine("  data/translated_test/ (when --replay is used)");
    Console.WriteLine("- Logs: logs/robot/robot_skeleton.jsonl (or --log-dir if specified)");
    Console.WriteLine("- Journal: logs/robot/journal/<trading_date>_<stream>.json");
}

var argsList = args.ToList();
if (argsList.Contains("--help") || argsList.Contains("-h"))
{
    PrintUsage();
    return;
}

var root = ProjectRootResolver.ResolveProjectRoot();

// Parse timetable path argument
string? customTimetablePath = null;
var timetablePathIndex = argsList.IndexOf("--timetable-path");
if (timetablePathIndex >= 0 && timetablePathIndex + 1 < argsList.Count)
{
    customTimetablePath = argsList[timetablePathIndex + 1];
    if (!Path.IsPathRooted(customTimetablePath))
    {
        // If relative path, resolve relative to project root
        customTimetablePath = Path.Combine(root, customTimetablePath);
    }
    if (!File.Exists(customTimetablePath))
    {
        Console.Error.WriteLine($"Error: Timetable file not found: {customTimetablePath}");
        return;
    }
}

var timetablePath = customTimetablePath ?? Path.Combine(root, "data", "timetable", "timetable_current.json");

if (argsList.Contains("--write-sample-timetable"))
{
    Directory.CreateDirectory(Path.GetDirectoryName(timetablePath)!);
    var spec = ParitySpec.LoadFromFile(Path.Combine(root, "configs", "analyzer_robot_parity.json"));
    var time = new TimeService(spec.Timezone);
    var utcNow = DateTimeOffset.UtcNow;
    var tradingDate = time.GetChicagoDateToday(utcNow).ToString("yyyy-MM-dd");

    // Sample: ES1 S1 07:30 (valid slot), ES2 S2 09:30 (valid slot)
    var sample = new
    {
        as_of = time.GetChicagoNow(utcNow).ToString("o"),
        trading_date = tradingDate,
        timezone = "America/Chicago",
        source = "harness",
        streams = new[]
        {
            new { stream = "ES1", instrument = "ES", session = "S1", slot_time = "07:30", enabled = true },
            new { stream = "ES2", instrument = "ES", session = "S2", slot_time = "09:30", enabled = true }
        }
    };

    File.WriteAllText(timetablePath, JsonSerializer.Serialize(sample, ParitySpec.JsonOptions()));
    Console.WriteLine($"Wrote sample timetable to: {timetablePath}");
}

Console.WriteLine($"Project root: {root}");
Console.WriteLine($"Timetable path: {timetablePath}");
if (customTimetablePath != null)
{
    Console.WriteLine($"Using custom timetable: {customTimetablePath}");
}

// Parse execution mode argument (fail-closed validation)
var executionMode = ExecutionMode.DRYRUN;
var modeIndex = argsList.IndexOf("--mode");
if (modeIndex >= 0 && modeIndex + 1 < argsList.Count)
{
    var modeStr = argsList[modeIndex + 1];
    if (!Enum.TryParse<ExecutionMode>(modeStr, ignoreCase: true, out var parsedMode))
    {
        Console.Error.WriteLine($"Error: Invalid execution mode '{modeStr}'. Must be DRYRUN, SIM, or LIVE.");
        return;
    }
    executionMode = parsedMode;
    
    // LIVE mode requires explicit confirmation (Phase C)
    if (executionMode == ExecutionMode.LIVE)
    {
        Console.Error.WriteLine("Error: LIVE mode is not yet enabled. Use DRYRUN or SIM.");
        return;
    }
}

// Load spec and time service early (needed for replay)
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
    
    // In replay mode, use timetable_replay.json if it exists and --timetable-path not specified
    string? replayTimetablePath = customTimetablePath;
    if (replayTimetablePath == null)
    {
        var replayTimetable = Path.Combine(root, "data", "timetable", "timetable_replay.json");
        if (File.Exists(replayTimetable))
        {
            replayTimetablePath = replayTimetable;
            Console.WriteLine($"Replay mode: Using timetable_replay.json");
        }
    }
    
    Console.WriteLine($"Replay mode: {startDate:yyyy-MM-dd} to {endDate:yyyy-MM-dd}");
    if (logDir != null)
        Console.WriteLine($"Custom log directory: {logDir}");
    var engine = new RobotEngine(root, TimeSpan.FromSeconds(1), executionMode, logDir, replayTimetablePath);
    engine.Start();
    var snapshotRoot = Path.Combine(root, "data", "translated_test");
    HistoricalReplay.Replay(engine, specLoaded, timeSvc, snapshotRoot, root, startDate, endDate);
    engine.Stop();
    Console.WriteLine($"Done. Inspect logs in: {logDir ?? "logs/robot/"}");
    return;
}

// Non-replay path: synthetic bar generation
{
    var engine = new RobotEngine(root, TimeSpan.FromSeconds(1), executionMode, null, customTimetablePath);
    engine.Start();

// Simulate time progression and bars (UTC-based)
var startUtc = DateTimeOffset.UtcNow;
var chicagoDate = timeSvc.GetChicagoDateToday(startUtc);

// Pick ES1 window: S1 starts 02:00 CT, slot_time 07:30 CT (from sample)
var rangeStartUtc = timeSvc.ConvertChicagoLocalToUtc(chicagoDate, specLoaded.Sessions["S1"].RangeStartTime);
var slotUtc = timeSvc.ConvertChicagoLocalToUtc(chicagoDate, "07:30");
var closeUtc = timeSvc.ConvertChicagoLocalToUtc(chicagoDate, specLoaded.EntryCutoff.MarketCloseTime);

// If it's already after close in real time, just run a short tick loop for wiring verification.
var simStart = startUtc < rangeStartUtc ? startUtc : rangeStartUtc.AddMinutes(-1);
var simEnd = startUtc < closeUtc ? closeUtc.AddMinutes(1) : startUtc.AddMinutes(2);

Console.WriteLine($"Simulating from {simStart:o} to {simEnd:o} (UTC)");

var rng = new Random(1);
decimal px = 5000m;

for (var t = simStart; t <= simEnd; t = t.AddMinutes(1))
{
    // Emit a bar each minute for ES
    var delta = (decimal)(rng.NextDouble() - 0.5) * 2m;
    var open = px;
    var high = open + Math.Abs(delta) + 0.25m;
    var low = open - Math.Abs(delta) - 0.25m;
    var close = open + delta;
    px = close;

    engine.OnBar(t, "ES", high, low, close, t);
    engine.Tick(t);
}

    engine.Stop();
    Console.WriteLine("Done. Inspect logs/robot/robot_skeleton.jsonl and logs/robot/journal/ for output.");
}

