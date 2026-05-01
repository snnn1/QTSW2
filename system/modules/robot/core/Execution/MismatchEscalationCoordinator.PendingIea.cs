using System;
using System.Collections.Generic;

namespace QTSW2.Robot.Core.Execution;

public sealed partial class MismatchEscalationCoordinator
{
    private const int PendingIeaSkipLogSeconds = 15;
    private static readonly TimeSpan PendingIeaStableSignatureWindow = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PendingIeaGateAdvanceCooldown = TimeSpan.FromSeconds(10);

    private sealed class PendingIeaDeferState
    {
        public int PendingExecutionCount;
        public DateTimeOffset FirstObservedUtc;
        public DateTimeOffset LastObservedUtc;
        public DateTimeOffset LastSkipLogUtc;
        public DateTimeOffset NextGateAdvanceEligibleUtc;
    }

    private readonly struct PendingIeaDeferDecision
    {
        public bool EmitDiagnostic { get; init; }
        public bool SkipGateAdvance { get; init; }
        public bool GateAdvanceBackoffArmed { get; init; }
        public double StableSignatureMs { get; init; }
        public double GateAdvanceCooldownRemainingMs { get; init; }
    }

    private readonly Dictionary<string, PendingIeaDeferState> _pendingIeaDeferStates =
        new(StringComparer.OrdinalIgnoreCase);

    private PendingIeaDeferDecision ObservePendingIeaDefer(
        string inst,
        int pendingIeA,
        DateTimeOffset utcNow,
        bool forGateAdvance)
    {
        var trimmed = inst?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(trimmed))
            return default;

        if (pendingIeA <= 0)
        {
            _pendingIeaDeferStates.Remove(trimmed);
            return default;
        }

        if (!_pendingIeaDeferStates.TryGetValue(trimmed, out var state) ||
            state.PendingExecutionCount != pendingIeA)
        {
            state = new PendingIeaDeferState
            {
                PendingExecutionCount = pendingIeA,
                FirstObservedUtc = utcNow,
                LastObservedUtc = utcNow,
                LastSkipLogUtc = utcNow,
                NextGateAdvanceEligibleUtc = utcNow
            };
            _pendingIeaDeferStates[trimmed] = state;
            return new PendingIeaDeferDecision
            {
                EmitDiagnostic = true,
                StableSignatureMs = 0
            };
        }

        state.LastObservedUtc = utcNow;
        var stableSignatureMs = Math.Max(0, (utcNow - state.FirstObservedUtc).TotalMilliseconds);
        var gateAdvanceBackoffArmed = false;
        var skipGateAdvance = false;

        if (forGateAdvance && stableSignatureMs >= PendingIeaStableSignatureWindow.TotalMilliseconds)
        {
            if (utcNow < state.NextGateAdvanceEligibleUtc)
            {
                skipGateAdvance = true;
            }
            else
            {
                state.NextGateAdvanceEligibleUtc = utcNow + PendingIeaGateAdvanceCooldown;
                gateAdvanceBackoffArmed = true;
            }
        }

        var emitDiagnostic = gateAdvanceBackoffArmed ||
            (utcNow - state.LastSkipLogUtc).TotalSeconds >= PendingIeaSkipLogSeconds;
        if (emitDiagnostic)
            state.LastSkipLogUtc = utcNow;

        _pendingIeaDeferStates[trimmed] = state;
        return new PendingIeaDeferDecision
        {
            EmitDiagnostic = emitDiagnostic,
            SkipGateAdvance = skipGateAdvance,
            GateAdvanceBackoffArmed = gateAdvanceBackoffArmed,
            StableSignatureMs = stableSignatureMs,
            GateAdvanceCooldownRemainingMs = skipGateAdvance
                ? Math.Max(0, (state.NextGateAdvanceEligibleUtc - utcNow).TotalMilliseconds)
                : 0
        };
    }
}
