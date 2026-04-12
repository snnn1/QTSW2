// Robot.ReplayHost — net48 IEA replay. Consumes validated JSON from net8.
// Run: Robot.ReplayHost.exe --file validated.json [--account X] [--instrument Y]
//      Robot.ReplayHost.exe --determinism-test --file validated.json
//      Robot.ReplayHost.exe --per-step-hashes --file validated.json

using System;
using QTSW2.Robot.ReplayHost;
using System.Collections.Generic;
using System.IO;
using QTSW2.Robot.Contracts;
using QTSW2.Robot.Core.Execution;

var argsList = new List<string>(args);
string? filePath = null;
string? account = null;
string? instrument = null;
var determinismTest = false;
var perStepHashes = false;
var runInvariants = false;
string? expectedPath = null;

for (var i = 0; i < argsList.Count; i++)
{
    if (argsList[i] == "--file" && i + 1 < argsList.Count)
    {
        filePath = argsList[++i];
    }
    else if (argsList[i] == "--account" && i + 1 < argsList.Count)
    {
        account = argsList[++i];
    }
    else if (argsList[i] == "--instrument" && i + 1 < argsList.Count)
    {
        instrument = argsList[++i];
    }
    else if (argsList[i] == "--determinism-test")
    {
        determinismTest = true;
    }
    else if (argsList[i] == "--per-step-hashes")
    {
        perStepHashes = true;
    }
    else if (argsList[i] == "--run-invariants")
    {
        runInvariants = true;
    }
    else if (argsList[i] == "--expected" && i + 1 < argsList.Count)
    {
        expectedPath = argsList[++i];
    }
}

if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
{
    Console.Error.WriteLine("REPLAY_HOST:ERROR:--file required and must exist");
    PrintUsage();
    return 1;
}

IReadOnlyList<ReplayEventEnvelope> events;
try
{
    events = CanonicalEventLoader.Load(filePath!);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"REPLAY_HOST:ERROR:Load failed: {ex.Message}");
    return 1;
}

if (events.Count == 0)
{
    Console.Error.WriteLine("REPLAY_HOST:ERROR:No events in file");
    return 1;
}

account ??= "Replay";
instrument ??= events[0].ExecutionInstrumentKey ?? "UNKNOWN";

var canonicalHash = CanonicalHasher.ComputeFileHash(filePath!);
Console.WriteLine($"CANONICAL:sha256={canonicalHash}");
Console.WriteLine($"HOST:version={VersionInfo.Version}");

if (runInvariants && string.IsNullOrEmpty(expectedPath))
{
    Console.Error.WriteLine("REPLAY_HOST:ERROR:--run-invariants requires --expected <path>");
    PrintUsage();
    return 1;
}

if (determinismTest)
{
    return RunDeterminismTest(events, account, instrument, runInvariants, expectedPath);
}

if (perStepHashes)
{
    return RunPerStepHashes(events, account, instrument);
}

if (runInvariants)
{
    return RunInvariantsOnly(events, account, instrument, expectedPath!);
}

return RunSingle(events, account, instrument);

static int RunDeterminismTest(IReadOnlyList<ReplayEventEnvelope> events, string account, string instrument, bool runInvariants, string? expectedPath)
{
    var hashes1 = RunAndCollectHashes(events, account, instrument);
    var hashes2 = RunAndCollectHashes(events, account, instrument);

    for (var i = 0; i < hashes1.Count; i++)
    {
        if (i >= hashes2.Count || hashes1[i] != hashes2[i])
        {
            var e = i > 0 && i <= events.Count ? events[i - 1] : null;
            Console.WriteLine($"DETERMINISM:FAIL");
            Console.WriteLine($"DIVERGENCE:index={i},hash1={hashes1[i]},hash2={(i < hashes2.Count ? hashes2[i] : "N/A")}" +
                (e != null ? $",source={e.Source},sequence={e.Sequence},type={e.Type}" : ""));
            return 1;
        }
    }
    Console.WriteLine($"DETERMINISM:PASS");
    Console.WriteLine($"DETERMINISM:steps={hashes1.Count}");

    if (runInvariants && !string.IsNullOrEmpty(expectedPath) && File.Exists(expectedPath))
    {
        var invExit = RunInvariantsOnly(events, account, instrument, expectedPath);
        if (invExit != 0) return invExit;
    }
    return 0;
}

static int RunPerStepHashes(IReadOnlyList<ReplayEventEnvelope> events, string account, string instrument)
{
    var hashes = RunAndCollectHashes(events, account, instrument);
    for (var i = 0; i < hashes.Count; i++)
    {
        Console.WriteLine($"HASH:index={i}:hash={hashes[i]}");
    }
    return 0;
}

static int RunSingle(IReadOnlyList<ReplayEventEnvelope> events, string account, string instrument)
{
    var driver = new ReplayDriver(account, instrument);
    driver.ProcessAll(events);
    var snapshot = driver.GetSnapshot();
    var hash = SnapshotHasher.ComputeHash(snapshot);
    Console.WriteLine($"HASH:final={hash}");
    return 0;
}

static List<string> RunAndCollectHashes(IReadOnlyList<ReplayEventEnvelope> events, string account, string instrument)
{
    var hashes = new List<string>(events.Count + 1);
    var driver = new ReplayDriver(account, instrument);

    hashes.Add(SnapshotHasher.ComputeHash(driver.GetSnapshot()));

    for (var i = 0; i < events.Count; i++)
    {
        driver.ProcessEvent(events[i]);
        hashes.Add(SnapshotHasher.ComputeHash(driver.GetSnapshot()));
    }
    return hashes;
}

static int RunInvariantsOnly(IReadOnlyList<ReplayEventEnvelope> events, string account, string instrument, string expectedPath)
{
    var invariants = InvariantRunner.LoadExpected(expectedPath);
    if (invariants.Count == 0)
    {
        Console.WriteLine("INVARIANTS:PASS");
        Console.WriteLine("INVARIANTS:count=0");
        return 0;
    }

    var driver = new ReplayDriver(account, instrument);
    var processed = new List<ReplayEventEnvelope>(events.Count);

    for (var i = 0; i < events.Count; i++)
    {
        driver.ProcessEvent(events[i]);
        processed.Add(events[i]);
        var snapshot = driver.GetSnapshot();
        var results = InvariantRunner.RunAll(invariants, snapshot, i + 1, processed);
        foreach (var r in results)
        {
            if (!r.Passed)
            {
                Console.WriteLine("INVARIANTS:FAIL");
                var reasonPart = !string.IsNullOrEmpty(r.Reason) ? $",reason={r.Reason}" : "";
                Console.WriteLine($"INVARIANT_FAIL:id={r.InvariantId},type={r.InvariantType},step={r.Step},detail={r.Detail}{reasonPart}");
                return 1;
            }
        }
    }

    Console.WriteLine("INVARIANTS:PASS");
    Console.WriteLine($"INVARIANTS:count={invariants.Count}");
    return 0;
}

static void PrintUsage()
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  --file <path>              Validated JSON array (required)");
    Console.WriteLine("  --account <name>           IEA account (default: Replay)");
    Console.WriteLine("  --instrument <key>         IEA execution instrument (default: from first event)");
    Console.WriteLine("  --determinism-test         Run twice, compare per-step hashes, exit 1 on divergence");
    Console.WriteLine("  --run-invariants           Run invariant checks (requires --expected)");
    Console.WriteLine("  --expected <path>          Path to expected.json for invariants");
    Console.WriteLine("  --per-step-hashes          Print HASH:index=N:hash=H for each step");
}
