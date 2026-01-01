using System.Text.Json;
using QTSW2.Robot.Core;

static void PrintUsage()
{
    Console.WriteLine("Robot Skeleton Harness (non-NinjaTrader)");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --write-sample-timetable");
    Console.WriteLine("  dotnet run --project modules/robot/harness/Robot.Harness.csproj");
    Console.WriteLine();
    Console.WriteLine("Notes:");
    Console.WriteLine("- Engine reads ONLY from:");
    Console.WriteLine("  configs/analyzer_robot_parity.json");
    Console.WriteLine("  data/timetable/timetable_current.json");
    Console.WriteLine("- Logs: logs/robot/robot_skeleton.jsonl");
    Console.WriteLine("- Journal: logs/robot/journal/<trading_date>_<stream>.json");
}

var argsList = args.ToList();
if (argsList.Contains("--help") || argsList.Contains("-h"))
{
    PrintUsage();
    return;
}

var root = ProjectRootResolver.ResolveProjectRoot();
var timetablePath = Path.Combine(root, "data", "timetable", "timetable_current.json");

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

var engine = new RobotEngine(root, TimeSpan.FromSeconds(1));
engine.Start();

// Simulate time progression and bars (UTC-based)
var specLoaded = ParitySpec.LoadFromFile(Path.Combine(root, "configs", "analyzer_robot_parity.json"));
var timeSvc = new TimeService(specLoaded.Timezone);
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

