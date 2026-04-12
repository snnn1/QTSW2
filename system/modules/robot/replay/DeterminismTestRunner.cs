using System.Collections.Generic;
using QTSW2.Robot.Contracts;

namespace QTSW2.Robot.Replay;

/// <summary>
/// Per-step determinism test. Identifies exactly which event introduces divergence.
/// Run 1: feed events, capture snapshot after each, store hash per step.
/// Run 2: feed events, capture snapshot after each, compare hash per step.
/// </summary>
public static class DeterminismTestRunner
{
    /// <summary>
    /// Run per-step determinism test. Returns (passed, divergenceStep).
    /// divergenceStep is -1 if passed; otherwise the 0-based step index where hashes first differed.
    /// </summary>
    /// <param name="events">Events to feed (same list for both runs).</param>
    /// <param name="captureSnapshot">Given (stepIndex, eventsProcessedSoFar), return state to hash. When IEA runner exists, return IEA snapshot.</param>
    /// <returns>(passed, divergenceStep). divergenceStep &lt; 0 means pass.</returns>
    public static (bool Passed, int DivergenceStep) Run(
        IReadOnlyList<ReplayEventEnvelope> events,
        System.Func<int, IReadOnlyList<ReplayEventEnvelope>, object> captureSnapshot)
    {
        var hashesRun1 = new List<string>(events.Count + 1);
        for (var step = 0; step <= events.Count; step++)
        {
            var slice = Slice(events, step);
            var state = captureSnapshot(step, slice);
            hashesRun1.Add(ReplayStateChecksum.ComputeHash(state));
        }

        for (var step = 0; step <= events.Count; step++)
        {
            var slice = Slice(events, step);
            var state = captureSnapshot(step, slice);
            var hash2 = ReplayStateChecksum.ComputeHash(state);
            if (hashesRun1[step] != hash2)
                return (false, step);
        }
        return (true, -1);
    }

    /// <summary>
    /// Snapshot = cumulative events processed so far. Use until IEA runner provides state.
    /// When IEA runner exists: create fresh IEA, feed eventsProcessed, return IEA snapshot (OrderMap, IntentMap, dedup, BE state).
    /// </summary>
    public static object CaptureCumulativeEvents(int step, IReadOnlyList<ReplayEventEnvelope> eventsProcessed)
    {
        return eventsProcessed;
    }

    private static IReadOnlyList<ReplayEventEnvelope> Slice(IReadOnlyList<ReplayEventEnvelope> events, int count)
    {
        if (count <= 0) return Array.Empty<ReplayEventEnvelope>();
        if (count >= events.Count) return events;
        var arr = new ReplayEventEnvelope[count];
        for (var i = 0; i < count; i++)
            arr[i] = events[i];
        return arr;
    }
}
