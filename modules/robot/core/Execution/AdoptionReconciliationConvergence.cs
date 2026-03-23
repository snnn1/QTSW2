using System;
using System.Collections.Generic;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Per broker order id: detect repeated unchanged evaluations, emit escalation once, quarantine with cooldown.
/// </summary>
public sealed class AdoptionReconciliationConvergence
{
    private sealed class EntryState
    {
        public DateTimeOffset FirstSeenUtc { get; set; }
        public DateTimeOffset LastSeenUtc { get; set; }
        public int UnchangedStreak { get; set; }
        public string? LastFingerprint { get; set; }
        public bool EscalationEmitted { get; set; }
        public DateTimeOffset? SuppressedUntilUtc { get; set; }
    }

    private readonly Dictionary<string, EntryState> _entries = new(StringComparer.Ordinal);

    public const int DefaultUnchangedThreshold = 4;
    public const int DefaultCooldownSeconds = 120;

    /// <summary>
    /// True if this order is in active quarantine (skip heavy evaluation; count suppressed recheck).
    /// </summary>
    public bool IsQuarantined(string brokerOrderId, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(brokerOrderId)) return false;
        return _entries.TryGetValue(brokerOrderId, out var st) &&
               st.SuppressedUntilUtc.HasValue &&
               now < st.SuppressedUntilUtc.Value;
    }

    /// <summary>
    /// After registering, if <paramref name="suppressHeavyWork"/> is true, skip recovery/adoption classification for this scan.
    /// If <paramref name="emitNonConvergenceEscalation"/> is true, emit <c>ADOPTION_NON_CONVERGENCE_ESCALATED</c> once for this episode.
    /// </summary>
    public void RegisterEvaluation(
        string brokerOrderId,
        DateTimeOffset now,
        string fingerprint,
        int unchangedThreshold,
        int cooldownSeconds,
        out bool emitNonConvergenceEscalation,
        out bool suppressHeavyWork,
        out int unchangedStreak)
    {
        emitNonConvergenceEscalation = false;
        suppressHeavyWork = false;
        unchangedStreak = 1;
        if (string.IsNullOrEmpty(brokerOrderId)) return;

        if (!_entries.TryGetValue(brokerOrderId, out var st))
        {
            _entries[brokerOrderId] = new EntryState
            {
                FirstSeenUtc = now,
                LastSeenUtc = now,
                UnchangedStreak = 1,
                LastFingerprint = fingerprint
            };
            unchangedStreak = 1;
            return;
        }

        st.LastSeenUtc = now;

        if (st.SuppressedUntilUtc.HasValue && now >= st.SuppressedUntilUtc.Value)
        {
            st.SuppressedUntilUtc = null;
            st.UnchangedStreak = 1;
            st.LastFingerprint = fingerprint;
            st.EscalationEmitted = false;
            st.FirstSeenUtc = now;
            unchangedStreak = 1;
            return;
        }

        if (st.SuppressedUntilUtc.HasValue && now < st.SuppressedUntilUtc.Value)
        {
            suppressHeavyWork = true;
            unchangedStreak = st.UnchangedStreak;
            return;
        }

        if (string.Equals(st.LastFingerprint, fingerprint, StringComparison.Ordinal))
        {
            st.UnchangedStreak++;
            unchangedStreak = st.UnchangedStreak;
            if (st.UnchangedStreak >= unchangedThreshold)
            {
                suppressHeavyWork = true;
                if (!st.EscalationEmitted)
                {
                    emitNonConvergenceEscalation = true;
                    st.EscalationEmitted = true;
                }
                st.SuppressedUntilUtc = now.AddSeconds(cooldownSeconds);
            }
        }
        else
        {
            st.LastFingerprint = fingerprint;
            st.UnchangedStreak = 1;
            st.EscalationEmitted = false;
            st.SuppressedUntilUtc = null;
            st.FirstSeenUtc = now;
            unchangedStreak = 1;
        }
    }

    /// <summary>Audit timestamps for escalation payloads.</summary>
    public bool TryGetTimestamps(string brokerOrderId, out DateTimeOffset firstSeenUtc, out DateTimeOffset lastSeenUtc)
    {
        if (_entries.TryGetValue(brokerOrderId, out var st))
        {
            firstSeenUtc = st.FirstSeenUtc;
            lastSeenUtc = st.LastSeenUtc;
            return true;
        }
        firstSeenUtc = default;
        lastSeenUtc = default;
        return false;
    }

    /// <summary>Clear fingerprint when material state changes (e.g. adopted into registry).</summary>
    public void ClearOrder(string brokerOrderId)
    {
        if (!string.IsNullOrEmpty(brokerOrderId))
            _entries.Remove(brokerOrderId);
    }

    /// <summary>Test-only reset.</summary>
    internal void ResetForTests() => _entries.Clear();
}
