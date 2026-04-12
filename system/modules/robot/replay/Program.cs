// Minimal entry point. Not part of NinjaTrader deployment.
// Run: dotnet run --project modules/robot/replay/Robot.Replay.csproj -- --test-loader [path]
// Run: dotnet run --project modules/robot/replay/Robot.Replay.csproj -- --checksum --file <path>
// Run: dotnet run --project modules/robot/replay/Robot.Replay.csproj -- --determinism-test --file <path>

using System.Diagnostics;
using System.Reflection;
using QTSW2.Robot.Replay;
using QTSW2.Robot.Replay.IncidentPack;

if (args.Length > 0 && args[0] == "--test-loader")
{
    var path = args.Length > 1 ? args[1] : null;
    ReplayLoaderTests.RunSelfTest(path);
    return 0;
}

if (args.Length >= 3 && args[0] == "--write-canonical" && args[1] == "--file")
{
    var inPath = args[2];
    var outPath = args.Length >= 5 && args[3] == "--out" ? args[4] : Path.ChangeExtension(inPath, ".canonical.json");
    if (!File.Exists(inPath)) { Console.WriteLine($"File not found: {inPath}"); return 1; }
    var events = ReplayLoader.LoadAndValidate(inPath);
    ReplayLoader.WriteCanonical(outPath, events);
    Console.WriteLine($"Wrote {outPath}");
    return 0;
}

if (args.Length >= 3 && args[0] == "--checksum" && args[1] == "--file")
{
    var path = args[2];
    if (!File.Exists(path)) { Console.WriteLine($"File not found: {path}"); return 1; }
    var events = ReplayLoader.LoadAndValidate(path);
    var hash = ReplayStateChecksum.ComputeEventsHash(events);
    Console.WriteLine($"checksum={hash}");
    return 0;
}

if (args.Length >= 3 && args[0] == "--extract-incident" && args[1] == "--from")
{
    var fromPath = ArgValue(args, "--from");
    var outDir = ArgValue(args, "--out");
    if (string.IsNullOrEmpty(fromPath) || string.IsNullOrEmpty(outDir))
    {
        Console.Error.WriteLine("REPLAY:ERROR:--extract-incident requires --from and --out");
        return 1;
    }
    if (!File.Exists(fromPath))
    {
        Console.Error.WriteLine($"REPLAY:ERROR:File not found: {fromPath}");
        return 1;
    }
    var opts = new IncidentPackExtractor.ExtractOptions
    {
        FromPath = fromPath,
        OutDir = outDir,
        ErrorEventType = ArgValue(args, "--error-event-type"),
        MessageContains = ArgValue(args, "--message-contains"),
        Instrument = ArgValue(args, "--instrument"),
        Account = ArgValue(args, "--account"),
        PreEvents = int.TryParse(ArgValue(args, "--pre-events"), out var pre) ? pre : 200,
        PostEvents = int.TryParse(ArgValue(args, "--post-events"), out var post) ? post : 200
    };
    try
    {
        var (id, count, sha) = IncidentPackExtractor.Extract(opts);
        Console.WriteLine($"INCIDENT_EXTRACTED:id={id},events={count},canonical_sha256={sha}");
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"REPLAY:ERROR:{ex.Message}");
        return 1;
    }
}

if (args.Length >= 3 && args[0] == "--determinism-test" && args[1] == "--file")
{
    var path = args[2];
    if (!File.Exists(path)) { Console.WriteLine($"File not found: {path}"); return 1; }

    var events = ReplayLoader.LoadAndValidate(path);
    var tempFile = Path.Combine(Path.GetTempPath(), $"replay_validated_{Guid.NewGuid():N}.json");
    try
    {
        ReplayLoader.WriteCanonical(tempFile, events);

        var hostProj = FindReplayHostProject();
        if (hostProj == null || !File.Exists(hostProj))
        {
            Console.Error.WriteLine($"REPLAY:ERROR:ReplayHost project not found. Build modules/robot/replay_host first.");
            return 1;
        }

        var hostArgs = new List<string> { "run", "--project", hostProj, "--", "--determinism-test", "--file", tempFile };
        var expectedPath = ArgValue(args, "--expected");
        if (!string.IsNullOrEmpty(expectedPath) && File.Exists(expectedPath))
        {
            hostArgs.Add("--run-invariants");
            hostArgs.Add("--expected");
            hostArgs.Add(expectedPath);
        }

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var a in hostArgs)
            psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi);
        if (proc == null) { Console.Error.WriteLine("REPLAY:ERROR:Failed to start ReplayHost"); return 1; }

        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(60000);

        Console.Write(stdout);
        if (!string.IsNullOrEmpty(stderr))
            Console.Error.Write(stderr);

        return proc.ExitCode;
    }
    finally
    {
        if (File.Exists(tempFile))
            try { File.Delete(tempFile); } catch { }
    }
}

Console.WriteLine("Robot.Replay — IEA deterministic replay module");
Console.WriteLine("Usage:");
Console.WriteLine("  --test-loader [path]           Run loader self-test");
Console.WriteLine("  --checksum --file <path>       Print state checksum");
Console.WriteLine("  --write-canonical --file <path> [--out <path>]  Load JSONL, write canonical JSON");
Console.WriteLine("  --determinism-test --file <path>  Validate, spawn net48 host, run per-step IEA determinism");
Console.WriteLine("  --extract-incident --from <path> --out <dir> [--error-event-type X] [--message-contains S] [--instrument I] [--account A] [--pre-events N] [--post-events N]");
return 0;

static string? ArgValue(string[] args, string name)
{
    for (var i = 0; i < args.Length - 1; i++)
        if (args[i] == name) return args[i + 1];
    return null;
}

static string? FindReplayHostProject()
{
    var baseDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly()?.Location) ?? Directory.GetCurrentDirectory();
    var candidates = new[]
    {
        Path.Combine(Directory.GetCurrentDirectory(), "modules", "robot", "replay_host", "Robot.ReplayHost.csproj"),
        Path.Combine(baseDir, "..", "..", "..", "replay_host", "Robot.ReplayHost.csproj"),
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "replay_host", "Robot.ReplayHost.csproj")
    };
    foreach (var c in candidates)
    {
        var full = Path.GetFullPath(c);
        if (File.Exists(full)) return full;
    }
    return null;
}
