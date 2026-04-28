// Contradiction / single-truth tests: staged conflicts must not yield two incompatible authoritative outcomes.
// Run: dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --test AUTHORITY_CONTRADICTIONS

using System;
using System.Collections.Generic;
using System.IO;
using QTSW2.Robot.Core;
using QTSW2.Robot.Core.Execution;

namespace QTSW2.Robot.Core.Tests;

public static class AuthorityContradictionTests
{
    public static (bool Pass, string? Error) RunAll()
    {
        var e = Case_MismatchBlockVsBrokerFlattenTruth();
        if (e != null) return (false, e);
        e = Case_SubmitPathAwareMismatchBypass();
        if (e != null) return (false, e);
        e = Case_RecoveryVsStructuralExecution();
        if (e != null) return (false, e);
        e = Case_BrokerVsJournalSingleParityClassification();
        if (e != null) return (false, e);
        e = Case_UeaCarriesSinglePrebuiltAuthorityFrame();
        if (e != null) return (false, e);
        e = Case_UeaTrustsSinglePreflightAuthoritySample();
        if (e != null) return (false, e);
        return (true, null);
    }

    /// <summary>
    /// Mismatch block (G1/EPA) denies risk-increasing submits; broker canonical exposure still shows risk — do not
    /// treat journal-only or log-only “flat” as overriding reconciliation abs quantity.
    /// </summary>
    private static string? Case_MismatchBlockVsBrokerFlattenTruth()
    {
        if (ExecutionPermissionAuthority.TryAdapterOrderSubmitPreflight(() => false, _ => true, null, (_, _) => false, "MES", "SUBMIT_ENTRY_STOP", out var deny) ||
            deny != "MISMATCH_EXECUTION_BLOCK")
            return "expected EPA deny MISMATCH_EXECUTION_BLOCK when mismatch callback true";

        var snap = new AccountSnapshot
        {
            Positions = new List<PositionSnapshot> { new() { Instrument = "MES", Quantity = 2 } },
            WorkingOrders = new List<WorkingOrderSnapshot>(),
            CapturedAtUtc = DateTimeOffset.UtcNow
        };
        var exposure = BrokerPositionResolver.ResolveFromSnapshots(snap.Positions, "MES");
        // Official flatten-complete (G4) is broker-model zero; non-zero => not complete — single normative story.
        if (exposure.ReconciliationAbsQuantityTotal == 0)
            return "expected non-zero reconciliation abs for open broker position";
        return null;
    }

    private static string? Case_SubmitPathAwareMismatchBypass()
    {
        bool GateByPath(string _, string? submitPath) =>
            !string.Equals(submitPath, "SUBMIT_ENTRY_STOP", StringComparison.Ordinal);

        if (!ExecutionPermissionAuthority.TryAdapterOrderSubmitPreflight(
                () => false,
                _ => true,
                GateByPath,
                (_, _) => false,
                "MES",
                "SUBMIT_ENTRY_STOP",
                out var allowDeny))
            return "expected submit-path mismatch bypass to allow SUBMIT_ENTRY_STOP";
        if (!string.IsNullOrEmpty(allowDeny))
            return "expected no deny reason on SUBMIT_ENTRY_STOP bypass";

        if (ExecutionPermissionAuthority.TryAdapterOrderSubmitPreflight(
                () => false,
                _ => false,
                GateByPath,
                (_, _) => false,
                "MES",
                "SUBMIT_ENTRY",
                out var deny) ||
            deny != "MISMATCH_EXECUTION_BLOCK")
            return "expected submit-path mismatch deny for SUBMIT_ENTRY";

        return null;
    }

    /// <summary>
    /// Recovery disallows structural RI path — single denial reason (repair_active), not a competing “allow” from parity OK.
    /// </summary>
    private static string? Case_RecoveryVsStructuralExecution()
    {
        var utc = DateTimeOffset.UtcNow;
        var root = Path.Combine(Path.GetTempPath(), "auth_contra_rec_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var log = new RobotLogger(root);
            var journal = new ExecutionJournal(root, log);
            var snap = new AccountSnapshot
            {
                Positions = new List<PositionSnapshot>(),
                WorkingOrders = new List<WorkingOrderSnapshot>(),
                CapturedAtUtc = utc
            };
            var req = new ExecutionSafetyEvaluationRequest
            {
                Instrument = "MES",
                CanonicalInstrument = "MES",
                UtcNow = utc,
                Journal = journal,
                AccountSnapshot = snap,
                UseInstrumentExecutionAuthority = false,
                IeaOwnedPlusAdoptedWorking = 0,
                RecoveryExecutionDisallowed = true,
                JournalIntegrityOrReconciliationRepairActive = false
            };

            if (ExecutionStructuralLayer.TryEvaluateOrderSubmitStructure(req, "SUBMIT_ENTRY", false, out var s) ||
                s.Reason != "repair_active")
                return "expected structural deny repair_active when recovery disallows execution";

            // EPA layer alone would not encode recovery; structural outcome is the single RI submit story here.
            return null;
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, true);
            }
            catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Broker shows position and journal is empty — exactly one parity classification (POSITION_MISMATCH), not PARITY_OK.
    /// </summary>
    private static string? Case_BrokerVsJournalSingleParityClassification()
    {
        var utc = DateTimeOffset.UtcNow;
        var root = Path.Combine(Path.GetTempPath(), "auth_contra_j_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var log = new RobotLogger(root);
            var journal = new ExecutionJournal(root, log);
            var snap = new AccountSnapshot
            {
                Positions = new List<PositionSnapshot> { new() { Instrument = "ES", Quantity = 3 } },
                WorkingOrders = new List<WorkingOrderSnapshot>(),
                CapturedAtUtc = utc
            };
            var reg = new TestRegistryView(false, 0);
            var r = JournalParityChecker.CheckJournalParity("ES", snap, journal, reg, "ES", utc);
            if (r.Status != JournalParityStatus.POSITION_MISMATCH)
                return "expected single classification POSITION_MISMATCH when broker open and journal empty";
            if (r.IsOk || r.IsOkOrPendingAlignment)
                return "did not expect OK or pending-alignment flags for raw position mismatch";
            return null;
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, true);
            }
            catch { /* best effort */ }
        }
    }

    private static string? Case_UeaCarriesSinglePrebuiltAuthorityFrame()
    {
        var utc = DateTimeOffset.UtcNow;
        var root = Path.Combine(Path.GetTempPath(), "auth_frame_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var log = new RobotLogger(root);
            var journal = new ExecutionJournal(root, log);
            var frame = new ExecutionAuthorityFrame
            {
                FrameId = "frame-test",
                Source = "test",
                Instrument = "MES",
                CanonicalInstrument = "MES",
                IntentId = "intent-frame",
                SubmitPath = "SUBMIT_TARGET",
                DecisionUtc = utc,
                FrameCreatedUtc = utc,
                BrokerPositionQty = 1,
                JournalOpenQty = 0
            };
            var safety = new ExecutionSafetyEvaluationRequest
            {
                Instrument = "MES",
                CanonicalInstrument = "MES",
                UtcNow = utc,
                Journal = journal,
                AccountSnapshot = null,
                AuthorityFrame = frame
            };
            if (ExecutionStructuralLayer.TryEvaluateOrderSubmitStructure(safety, "SUBMIT_TARGET", false, out var structural) ||
                structural.AuthorityFrameId != "frame-test")
                return "expected structural deny snapshot to carry the prebuilt authority frame id";

            var uea = new UnifiedExecutionAuthority(log);
            var decision = uea.Evaluate(new AuthorityEvaluationRequest
            {
                Instrument = "MES",
                IntentId = "intent-frame",
                SubmitIntent = SubmitIntent.RiskCoverage,
                SubmitPath = "SUBMIT_TARGET",
                UtcNow = utc,
                GlobalKillSwitchActive = () => false,
                MismatchExecutionBlocked = _ => false,
                MismatchExecutionBlockedForSubmit = (_, _) => false,
                InstrumentFrozenOrEpaBlocked = (_, _) => false,
                PrebuiltSafetyRequest = safety,
                AuthorityFrame = frame,
                BuildSafetyRequest = (_, _, _) => throw new InvalidOperationException("prebuilt frame was not used")
            });

            if (decision.Allowed)
                return "expected UEA structural deny with null account snapshot";
            if (decision.AuthorityFrame?.FrameId != "frame-test")
                return "expected UEA decision to carry the same prebuilt authority frame id";
            if (decision.DenyGate != "Gate3_Structural")
                return "expected Gate3_Structural deny from prebuilt safety request";

            return null;
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, true);
            }
            catch { /* best effort */ }
        }
    }

    private static string? Case_UeaTrustsSinglePreflightAuthoritySample()
    {
        var utc = DateTimeOffset.UtcNow;
        var root = Path.Combine(Path.GetTempPath(), "auth_preflight_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var log = new RobotLogger(root);
            var uea = new UnifiedExecutionAuthority(log);

            var sampledAllow = uea.Evaluate(new AuthorityEvaluationRequest
            {
                Instrument = "MES",
                IntentId = "intent-sampled-allow",
                SubmitIntent = SubmitIntent.OpeningEntry,
                SubmitPath = "SUBMIT_ENTRY_STOP",
                UtcNow = utc,
                GlobalKillSwitchActive = () => false,
                MismatchExecutionBlocked = _ => true,
                MismatchExecutionBlockedForSubmit = (_, _) => true,
                InstrumentFrozenOrEpaBlocked = (_, _) => false,
                PreflightGlobalKillSwitchActive = false,
                PreflightMismatchExecutionBlocked = false,
                PreflightMismatchExecutionBlockedForSubmit = false,
                PreflightInstrumentFrozenOrEpaBlocked = false
            });
            if (!sampledAllow.Allowed)
                return "expected UEA to trust the sampled preflight allow instead of re-reading later mismatch callbacks";

            var sampledDeny = uea.Evaluate(new AuthorityEvaluationRequest
            {
                Instrument = "MES",
                IntentId = "intent-sampled-deny",
                SubmitIntent = SubmitIntent.OpeningEntry,
                SubmitPath = "SUBMIT_ENTRY_STOP",
                UtcNow = utc,
                GlobalKillSwitchActive = () => false,
                MismatchExecutionBlocked = _ => false,
                MismatchExecutionBlockedForSubmit = (_, _) => false,
                InstrumentFrozenOrEpaBlocked = (_, _) => false,
                PreflightGlobalKillSwitchActive = false,
                PreflightMismatchExecutionBlocked = true,
                PreflightInstrumentFrozenOrEpaBlocked = false
            });
            if (sampledDeny.Allowed || sampledDeny.DenyReason != "MISMATCH_EXECUTION_BLOCK")
                return "expected UEA to trust the sampled preflight mismatch deny";

            return null;
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, true);
            }
            catch { /* best effort */ }
        }
    }

    private sealed class TestRegistryView : IJournalParityRegistryView
    {
        public TestRegistryView(bool useIea, int ieaOwnedPlus)
        {
            UseInstrumentExecutionAuthority = useIea;
            IeaOwnedPlusAdoptedWorking = ieaOwnedPlus;
        }

        public bool UseInstrumentExecutionAuthority { get; }
        public int IeaOwnedPlusAdoptedWorking { get; }
    }
}
