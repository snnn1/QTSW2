using System;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Phase 5: Testable supervisory policy logic (hysteresis, dwell suppression).
/// Extracted for unit testing; used by InstrumentExecutionAuthority.
/// </summary>
public static class SupervisoryPolicy
{
    /// <summary>
    /// Phase 5 hardening: Should we suppress re-escalation to COOLDOWN?
    /// Returns true when lastResume was recent (within minDwellSeconds), to reduce flapping.
    /// </summary>
    /// <param name="lastResumeToActiveUtc">When we last transitioned to ACTIVE (from COOLDOWN or AWAITING_OPERATOR_ACK). Null = no restriction.</param>
    /// <param name="utcNow">Current time.</param>
    /// <param name="minDwellSeconds">Minimum seconds in ACTIVE before allowing COOLDOWN again.</param>
    /// <returns>True to suppress (do not escalate to COOLDOWN); false to allow.</returns>
    public static bool ShouldSuppressCooldownEscalation(DateTimeOffset? lastResumeToActiveUtc, DateTimeOffset utcNow, int minDwellSeconds)
    {
        if (!lastResumeToActiveUtc.HasValue) return false;
        var dwellSeconds = (utcNow - lastResumeToActiveUtc.Value).TotalSeconds;
        return dwellSeconds < minDwellSeconds;
    }
}
