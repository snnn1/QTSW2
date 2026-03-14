// Gap 5: Execution event replay validation tests.
// Run: dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --test EXECUTION_EVENT_REPLAY

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using QTSW2.Robot.Core;
using QTSW2.Robot.Core.Execution;

namespace QTSW2.Robot.Core.Tests;

public static class ExecutionEventReplayTests
{
    public static (bool Pass, string? Error) RunExecutionEventReplayTests()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "QTSW2_Gap5_Replay_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        try
        {
            Directory.CreateDirectory(tempDir);
            var tradingDate = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");
            var eventDir = Path.Combine(tempDir, "automation", "logs", "execution_events", tradingDate);
            Directory.CreateDirectory(eventDir);
            var streamPath = Path.Combine(eventDir, "ES.jsonl");

            var log = new RobotLogger(tempDir, Path.Combine(tempDir, "logs"), "ES");
            var writer = new ExecutionEventWriter(tempDir, () => tradingDate, log);

            // 1. Writer appends valid ordered JSONL
            writer.Emit(new CanonicalExecutionEvent
            {
                TimestampUtc = DateTimeOffset.UtcNow.ToString("o"),
                Instrument = "ES",
                EventType = ExecutionEventTypes.COMMAND_RECEIVED,
                Source = "Test",
                Payload = new { commandType = "FlattenIntentCommand" }
            });
            writer.Emit(new CanonicalExecutionEvent
            {
                TimestampUtc = DateTimeOffset.UtcNow.ToString("o"),
                Instrument = "ES",
                EventType = ExecutionEventTypes.COMMAND_COMPLETED,
                Source = "Test",
                Payload = new { commandType = "FlattenIntentCommand" }
            });

            if (writer.WriteCount != 2)
                return (false, $"Expected 2 writes, got {writer.WriteCount}");
            if (!File.Exists(streamPath))
                return (false, "Stream file not created");

            var lines = File.ReadAllLines(streamPath);
            if (lines.Length != 2)
                return (false, $"Expected 2 lines, got {lines.Length}");

            // 2. Replay reader returns events in order
            var events = ExecutionReplayReader.ReadEvents(streamPath).ToList();
            if (events.Count != 2)
                return (false, $"Replay reader expected 2 events, got {events.Count}");
            if (events[0].EventType != ExecutionEventTypes.COMMAND_RECEIVED)
                return (false, $"First event expected COMMAND_RECEIVED, got {events[0].EventType}");
            if (events[1].EventType != ExecutionEventTypes.COMMAND_COMPLETED)
                return (false, $"Second event expected COMMAND_COMPLETED, got {events[1].EventType}");
            if (events[0].EventSequence >= events[1].EventSequence)
                return (false, "Event sequences should be strictly increasing");

            // 3. Replay rebuilder applies lifecycle and protective events
            writer.Emit(new CanonicalExecutionEvent
            {
                TimestampUtc = DateTimeOffset.UtcNow.ToString("o"),
                Instrument = "ES",
                IntentId = "intent-1",
                EventType = ExecutionEventTypes.LIFECYCLE_TRANSITIONED,
                LifecycleStateAfter = "TERMINAL",
                Source = "Test",
                Payload = new { from_state = "WORKING", to_state = "TERMINAL" }
            });
            writer.Emit(new CanonicalExecutionEvent
            {
                TimestampUtc = DateTimeOffset.UtcNow.ToString("o"),
                Instrument = "ES",
                EventType = ExecutionEventTypes.PROTECTIVE_INSTRUMENT_BLOCKED,
                Source = "Test",
                Payload = new { status = "PROTECTIVE_MISSING_STOP" }
            });
            writer.Emit(new CanonicalExecutionEvent
            {
                TimestampUtc = DateTimeOffset.UtcNow.ToString("o"),
                Instrument = "ES",
                EventType = ExecutionEventTypes.INTENT_TERMINALIZED,
                IntentId = "intent-1",
                Source = "Test",
                Payload = new { terminal_reason = "TARGET_FILLED" }
            });

            events = ExecutionReplayReader.ReadEvents(streamPath).ToList();
            var state = ExecutionReplayRebuilder.Rebuild(events);

            if (!state.LifecycleByIntent.TryGetValue("intent-1", out var lc) || lc != "TERMINAL")
                return (false, $"Lifecycle for intent-1 expected TERMINAL, got {lc}");
            if (!state.ProtectiveBlockedInstruments.Contains("ES"))
                return (false, "ES should be in ProtectiveBlockedInstruments");
            if (!state.TerminalIntents.Contains("intent-1"))
                return (false, "intent-1 should be in TerminalIntents");

            // 4. Mismatch fail-closed replay
            writer.Emit(new CanonicalExecutionEvent
            {
                TimestampUtc = DateTimeOffset.UtcNow.ToString("o"),
                Instrument = "NQ",
                EventType = ExecutionEventTypes.MISMATCH_FAIL_CLOSED,
                Source = "Test",
                Payload = new { mismatch_type = "BROKER_AHEAD" }
            });
            var nqPath = Path.Combine(eventDir, "NQ.jsonl");
            events = ExecutionReplayReader.ReadEvents(nqPath).ToList();
            state = ExecutionReplayRebuilder.Rebuild(events);
            if (!state.MismatchFailClosedInstruments.Contains("NQ"))
                return (false, "NQ should be in MismatchFailClosedInstruments");

            return (true, null);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* ignore */ }
        }
    }
}
