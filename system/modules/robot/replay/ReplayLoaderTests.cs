namespace QTSW2.Robot.Replay;

/// <summary>
/// Simple tests for ReplayLoader. Run with: dotnet run -- --test-loader [path]
/// </summary>
public static class ReplayLoaderTests
{
    public static void RunSelfTest(string? path = null)
    {
        var samplePath = path ?? FindSamplePath();
        if (!File.Exists(samplePath))
        {
            Console.WriteLine("ReplayLoaderTests: sample_replay.jsonl not found, skipping");
            return;
        }

        try
        {
            var events = ReplayLoader.LoadAndValidate(samplePath, expectedInstrument: "MNQ");
            Console.WriteLine($"ReplayLoaderTests: Loaded {events.Count} events OK");
            foreach (var e in events)
                Console.WriteLine($"  {e.Type} seq={e.Sequence}");
        }
        catch (ReplayLoadException ex)
        {
            Console.WriteLine($"ReplayLoaderTests FAILED: {ex.ValidationError} (line {ex.LineNumber})");
            throw;
        }
    }

    private static string FindSamplePath()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "..", "..", "..", "sample_replay.jsonl"),
            Path.Combine(Directory.GetCurrentDirectory(), "sample_replay.jsonl"),
            Path.Combine(Directory.GetCurrentDirectory(), "modules", "robot", "replay", "sample_replay.jsonl")
        };
        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }
}
