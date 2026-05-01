using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using QTSW2.Robot.Core;

namespace QTSW2.Robot.Core.Execution;

public sealed partial class MismatchEscalationCoordinator
{
    private readonly Func<string, DateTimeOffset, MismatchConvergenceCanonicalProbeResult>? _probeCanonicallyUnexplainedExposure;

    /// <summary>Phase 3: expected-transient convergence windows, independent of gate state.</summary>
    private readonly ConcurrentDictionary<string, MismatchConvergenceEntry> _convergenceByInstrument =
        new(StringComparer.OrdinalIgnoreCase);

    private sealed class MismatchConvergenceEntry
    {
        public string Cause = "";
        public DateTimeOffset ArmedAtUtc;
        public DateTimeOffset ExpiresAtUtc;
    }

    private readonly Dictionary<string, DateTimeOffset> _mismatchPendingConvergenceLastEmit =
        new(StringComparer.OrdinalIgnoreCase);

    private const int MismatchPendingConvergenceLogSeconds = 3;

    /// <summary>Harness: first-detect escalations skipped due to Phase 3 convergence window + clean canonical probe.</summary>
    internal int TestConvergenceFirstEscalationSuppressedCount { get; private set; }

    /// <summary>Harness: convergence + clean canonical probe must not pair with authority transition.</summary>
    internal int TestMismatchEvalInvariantViolationCount { get; private set; }

    private sealed class MismatchEvalScratchRow
    {
        public bool EpisodeExtended;
        public bool AuthorityPublished;
    }

    /// <summary>Per audit tick: instruments processed this cycle; used for convergence invariant logging.</summary>
    private readonly Dictionary<string, MismatchEvalScratchRow> _mismatchEvalScratch =
        new(StringComparer.OrdinalIgnoreCase);

    private bool _mismatchEvalInvariantCycleActive;

    /// <summary>Phase 3: Arm a convergence window. Suppresses premature first escalation when the probe is clean.</summary>
    public void ArmConvergence(string instrument, string cause, DateTimeOffset utcNow)
    {
        if (string.IsNullOrWhiteSpace(instrument)) return;
        ArmConvergenceCore(instrument.Trim(), string.IsNullOrWhiteSpace(cause) ? "unspecified" : cause.Trim(), utcNow);
    }

    private void ArmConvergenceCore(string inst, string cause, DateTimeOffset utcNow)
    {
        _convergenceByInstrument[inst] = new MismatchConvergenceEntry
        {
            Cause = cause,
            ArmedAtUtc = utcNow,
            ExpiresAtUtc = utcNow.AddMilliseconds(MismatchEscalationPolicy.MISMATCH_CONVERGENCE_WINDOW_MS)
        };
    }

    private static bool ShouldArmConvergenceFromExecutionTriggerDetails(MismatchExecutionTriggerDetails d) =>
        d.FillDelta != 0 || d.EntryToProtectivesTransition || d.WorkingOrderSubmitTransition;

    private static string BuildExecutionTriggerConvergenceCause(MismatchExecutionTriggerDetails d)
    {
        if (d.FillDelta != 0 && d.EntryToProtectivesTransition)
            return "execution_fill_and_protective_transition";
        if (d.EntryToProtectivesTransition)
            return "protective_transition";
        if (d.WorkingOrderSubmitTransition)
            return "working_order_submit_transition";
        if (d.FillDelta != 0)
            return "execution_fill";
        return "execution_trigger";
    }

    private void TryRemoveExpiredConvergence(string inst, DateTimeOffset utcNow)
    {
        if (!_convergenceByInstrument.TryGetValue(inst, out var e)) return;
        if (utcNow > e.ExpiresAtUtc)
            _convergenceByInstrument.TryRemove(inst, out _);
    }

    private bool TryGetActiveConvergence(string inst, DateTimeOffset utcNow, out MismatchConvergenceEntry entry)
    {
        TryRemoveExpiredConvergence(inst, utcNow);
        if (!_convergenceByInstrument.TryGetValue(inst, out var e) || utcNow > e.ExpiresAtUtc)
        {
            entry = null!;
            return false;
        }

        entry = e;
        return true;
    }

    private bool ShouldSuppressFirstMismatchEscalationForConvergence(
        string inst,
        DateTimeOffset utcNow,
        MismatchObservation obs,
        out MismatchConvergenceCanonicalProbeResult probeResult,
        out string suppressionReason,
        out MismatchConvergenceEntry? convEntry)
    {
        probeResult = default;
        suppressionReason = "";
        convEntry = null;
        if (_probeCanonicallyUnexplainedExposure == null)
            return false;
        if (!TryGetActiveConvergence(inst, utcNow, out var conv))
            return false;
        if (MismatchConvergenceEscalationPolicy.IsSeriousMismatchType(obs.MismatchType))
            return false;

        try
        {
            probeResult = _probeCanonicallyUnexplainedExposure(inst, utcNow);
        }
        catch
        {
            return false;
        }

        if (probeResult.HasUnexplainedBrokerExposure)
            return false;

        convEntry = conv;
        suppressionReason = "convergence_window_transient_mismatch_no_canonical_unexplained_exposure";
        return true;
    }

    private void EmitConvergenceSuppressedFirstEscalation(
        string inst,
        DateTimeOffset utcNow,
        MismatchObservation obs,
        in MismatchConvergenceCanonicalProbeResult probe,
        string suppressionReason,
        MismatchConvergenceEntry conv)
    {
        if (_log == null) return;
        var payload = new
        {
            instrument = inst,
            convergence_state = "armed",
            convergence_cause = conv.Cause,
            convergence_armed_at_utc = conv.ArmedAtUtc.ToString("o"),
            convergence_expires_at_utc = conv.ExpiresAtUtc.ToString("o"),
            suppression_reason = suppressionReason,
            mismatch_type = obs.MismatchType.ToString(),
            unexplained_broker_position_qty = probe.UnexplainedBrokerPositionQty,
            unexplained_broker_working_count = probe.UnexplainedBrokerWorkingCount,
            has_unexplained_broker_exposure = probe.HasUnexplainedBrokerExposure,
            observation_summary = obs.Summary,
            run_id = _getRunIdForMismatchDiagnostics?.Invoke() ?? ""
        };
        _log.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "RECONCILIATION_MISMATCH_FIRST_ESCALATION_SUPPRESSED_FOR_CONVERGENCE",
            payload));
        if (IsInstrumentInEngineScopeForDiagnostics(inst))
            EmitCanonical(inst, "RECONCILIATION_MISMATCH_FIRST_ESCALATION_SUPPRESSED_FOR_CONVERGENCE", utcNow, payload, "INFO");
    }

    private void NoteMismatchEvalAuthorityPublished(string instrument)
    {
        if (!_mismatchEvalInvariantCycleActive || string.IsNullOrWhiteSpace(instrument)) return;
        var key = instrument.Trim();
        if (!_mismatchEvalScratch.TryGetValue(key, out var row)) return;
        row.AuthorityPublished = true;
    }

    private void NoteMismatchEvalEpisodeExtended(string instrument)
    {
        if (!_mismatchEvalInvariantCycleActive || string.IsNullOrWhiteSpace(instrument)) return;
        var key = instrument.Trim();
        if (!_mismatchEvalScratch.TryGetValue(key, out var row)) return;
        row.EpisodeExtended = true;
    }

    private void EmitMismatchEvaluationInvariant(string inst, DateTimeOffset utcNow)
    {
        if (!_mismatchEvalInvariantCycleActive) return;

        var convergenceActive = TryGetActiveConvergence(inst, utcNow, out _);
        var probeFn = _probeCanonicallyUnexplainedExposure;
        var canonicalProbeAvailable = false;
        var canonicalExposureOk = false;
        if (probeFn != null)
        {
            try
            {
                var pr = probeFn(inst, utcNow);
                canonicalProbeAvailable = true;
                canonicalExposureOk = !pr.HasUnexplainedBrokerExposure;
            }
            catch
            {
                canonicalProbeAvailable = false;
            }
        }

        _stateByInstrument.TryGetValue(inst, out var state);
        var episodeExists = state?.ConvergenceEpisode.EpisodeId != 0;

        _mismatchEvalScratch.TryGetValue(inst, out var scratch);
        var episodeExtended = scratch?.EpisodeExtended ?? false;
        var authorityPublished = scratch?.AuthorityPublished ?? false;

        var assertionApplicable = convergenceActive && canonicalProbeAvailable && canonicalExposureOk;
        var invariantViolated = assertionApplicable && authorityPublished;
        var episodeExtendedWithoutPublicationDeferred = assertionApplicable && episodeExtended && !authorityPublished;
        if (invariantViolated)
            TestMismatchEvalInvariantViolationCount++;

        var invariantOk = !invariantViolated;

        if (_log == null) return;

        var payload = new
        {
            instrument = inst,
            convergence_active = convergenceActive,
            canonical_exposure_ok = canonicalExposureOk,
            canonical_probe_available = canonicalProbeAvailable,
            episode_exists = episodeExists,
            episode_extended = episodeExtended,
            authority_published = authorityPublished,
            episode_extended_without_publication_deferred = episodeExtendedWithoutPublicationDeferred,
            assertion_applicable = assertionApplicable,
            convergence_invariant_ok = invariantOk,
            run_id = _getRunIdForMismatchDiagnostics?.Invoke() ?? "",
            ts_utc = utcNow.ToString("o")
        };

        _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "RECONCILIATION_MISMATCH_EVAL_INVARIANT",
            state: "ENGINE", payload));

        if (invariantViolated)
        {
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "RECONCILIATION_MISMATCH_EVAL_INVARIANT_VIOLATION",
                state: "ENGINE",
                new
                {
                    instrument = inst,
                    convergence_active = convergenceActive,
                    canonical_exposure_ok = canonicalExposureOk,
                    episode_exists = episodeExists,
                    episode_extended = episodeExtended,
                    authority_published = authorityPublished,
                    expected_when_convergence_and_canonical_clean = "episode_extended=false and authority_published=false",
                    run_id = _getRunIdForMismatchDiagnostics?.Invoke() ?? "",
                    ts_utc = utcNow.ToString("o")
                }));
            if (IsInstrumentInEngineScopeForDiagnostics(inst))
                EmitCanonical(inst, "RECONCILIATION_MISMATCH_EVAL_INVARIANT_VIOLATION", utcNow, payload, "WARN");
        }
    }

    private void EmitMismatchPendingConvergence(string inst, DateTimeOffset utcNow, MismatchObservation obs)
    {
        if (_mismatchPendingConvergenceLastEmit.TryGetValue(inst, out var last) &&
            (utcNow - last).TotalSeconds < MismatchPendingConvergenceLogSeconds)
            return;
        _mismatchPendingConvergenceLastEmit[inst] = utcNow;

        var payload = new
        {
            instrument = inst,
            mismatch_type = obs.MismatchType.ToString(),
            summary = obs.Summary,
            gate_escalation_deferred = true,
            note = "Tier-1 PendingAlignment window — first mismatch gate engagement deferred"
        };
        _log?.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "MISMATCH_PENDING_CONVERGENCE", payload));
        EmitCanonical(inst, "MISMATCH_PENDING_CONVERGENCE", utcNow, payload, "INFO");
    }

    internal void ResetConvergenceTestCountersForTest()
    {
        TestConvergenceFirstEscalationSuppressedCount = 0;
        TestMismatchEvalInvariantViolationCount = 0;
    }

    internal bool HasActiveConvergenceForTest(string instrument, DateTimeOffset utcNow) =>
        !string.IsNullOrWhiteSpace(instrument) && TryGetActiveConvergence(instrument.Trim(), utcNow, out _);

    internal void ClearConvergenceForTest(string instrument)
    {
        if (!string.IsNullOrWhiteSpace(instrument))
            _convergenceByInstrument.TryRemove(instrument.Trim(), out _);
    }
}
