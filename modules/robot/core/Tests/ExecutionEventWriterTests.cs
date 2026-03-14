// Gap 5: Execution event writer unit tests.

using System;
using System.IO;
using System.Linq;
using QTSW2.Robot.Core;
using QTSW2.Robot.Core.Execution;

namespace QTSW2.Robot.Core.Tests;

public static class ExecutionEventWriterTests
{
    public static (bool Pass, string? Error) RunExecutionEventWriterTests()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "QTSW2_Gap5_Writer_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        try
        {
            Directory.CreateDirectory(tempDir);
            var tradingDate = "2025-03-12";
            var log = new RobotLogger(tempDir, Path.Combine(tempDir, "logs"), "ES");
            var writer = new ExecutionEventWriter(tempDir, () => tradingDate, log);

            writer.Emit(new CanonicalExecutionEvent { TimestampUtc = DateTimeOffset.UtcNow.ToString("o"), Instrument = "ES", EventType = ExecutionEventTypes.COMMAND_RECEIVED, Source = "Test", Payload = new { } });
            writer.Emit(new CanonicalExecutionEvent { TimestampUtc = DateTimeOffset.UtcNow.ToString("o"), Instrument = "ES", EventType = ExecutionEventTypes.COMMAND_COMPLETED, Source = "Test", Payload = new { } });
            writer.Emit(new CanonicalExecutionEvent { TimestampUtc = DateTimeOffset.UtcNow.ToString("o"), Instrument = "ES", EventType = ExecutionEventTypes.COMMAND_RECEIVED, Source = "Test", Payload = new { } });

            if (writer.WriteCount != 3)
                return (false, $"WriteCount expected 3, got {writer.WriteCount}");

            var path = Path.Combine(tempDir, "automation", "logs", "execution_events", tradingDate, "ES.jsonl");
            if (!File.Exists(path))
                return (false, "Event file not created");

            var lines = File.ReadAllLines(path).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
            if (lines.Count != 3)
                return (false, $"Expected 3 event lines, got {lines.Count}");

            long prevSeq = 0;
            foreach (var line in lines)
            {
                if (line.IndexOf("\"event_sequence\":", StringComparison.Ordinal) < 0)
                    return (false, "Line missing event_sequence");
                var seqStart = line.IndexOf("\"event_sequence\":", StringComparison.Ordinal) + 17;
                var seqEnd = line.IndexOf(",", seqStart, StringComparison.Ordinal);
                if (seqEnd < 0) seqEnd = line.IndexOf("}", seqStart, StringComparison.Ordinal);
                var seqStr = line.Substring(seqStart, seqEnd - seqStart).Trim();
                if (!long.TryParse(seqStr, out var seq))
                    return (false, $"Invalid sequence: {seqStr}");
                if (seq <= prevSeq)
                    return (false, $"Sequence not strictly increasing: {prevSeq} -> {seq}");
                prevSeq = seq;
            }

            return (true, null);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* ignore */ }
        }
    }
}
