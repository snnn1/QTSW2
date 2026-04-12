using System;

namespace QTSW2.Robot.Core.Execution;

/// <summary>Which branch of <see cref="StructuralMultiIntentPolicy"/> ran (for tests and telemetry).</summary>
public enum StructuralMultiIntentPolicyActionKind
{
    AllowObservation,
    BlockNewEntries,
    GateRecoveryRequested
}

/// <summary>
/// Single implementation for structural multi-intent policy side effects — used by <see cref="RobotEngine"/> and harness tests
/// so <c>auto_offset</c> behavior stays deterministic and auditable.
/// </summary>
public static class StructuralMultiIntentPolicyRuntime
{
    /// <summary>
    /// Invokes policy callbacks. Logging remains the caller's responsibility (engine events vs test capture).
    /// </summary>
    public static StructuralMultiIntentPolicyActionKind Invoke(
        StructuralMultiIntentPolicy policy,
        string instrument,
        DateTimeOffset utcNow,
        Action<string, DateTimeOffset, string>? onStandDownStreams,
        Action<string, DateTimeOffset>? onGateRecoveryRequested)
    {
        switch (policy)
        {
            case StructuralMultiIntentPolicy.Allow:
                return StructuralMultiIntentPolicyActionKind.AllowObservation;
            case StructuralMultiIntentPolicy.BlockNewEntries:
                onStandDownStreams?.Invoke(instrument, utcNow, "STRUCTURAL_MULTI_INTENT_POLICY_BLOCK_NEW_ENTRIES");
                return StructuralMultiIntentPolicyActionKind.BlockNewEntries;
            case StructuralMultiIntentPolicy.AutoOffsetRequest:
                onGateRecoveryRequested?.Invoke(instrument, utcNow);
                return StructuralMultiIntentPolicyActionKind.GateRecoveryRequested;
            default:
                return StructuralMultiIntentPolicyActionKind.AllowObservation;
        }
    }
}
