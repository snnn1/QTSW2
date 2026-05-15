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
        e = Case_RiskCoverageSubmitsBypassEntryOnlyBlocks();
        if (e != null) return (false, e);
        e = Case_RecoveryVsStructuralExecution();
        if (e != null) return (false, e);
        e = Case_BrokerVsJournalSingleParityClassification();
        if (e != null) return (false, e);
        e = Case_UeaCarriesSinglePrebuiltAuthorityFrame();
        if (e != null) return (false, e);
        e = Case_UeaTrustsSinglePreflightAuthoritySample();
        if (e != null) return (false, e);
        e = Case_UeaEntrySubmitAuthorityBlocksUnsafeFrames();
        if (e != null) return (false, e);
        e = Case_UeaMarketReentryAuthorityBlocksUnsafeFrames();
        if (e != null) return (false, e);
        e = Case_UeaProtectiveSubmitAuthorityAllowsSafetyThroughEntryBlocks();
        if (e != null) return (false, e);
        e = Case_UeaProtectiveBlockAuthorityRequiresCriticalCreateAndCoveredClear();
        if (e != null) return (false, e);
        e = Case_UeaCancelSubmitAuthorityAllowsSafetyThroughEntryBlocks();
        if (e != null) return (false, e);
        e = Case_UeaFlattenAuthorityAllowsSafetyThroughEntryBlocks();
        if (e != null) return (false, e);
        e = Case_UeaLifecycleAuthorityDeniesTerminalCommitWithOpenReentry();
        if (e != null) return (false, e);
        e = Case_UeaLifecycleAuthorityDeniesStaleSessionSweepForTrackedReentry();
        if (e != null) return (false, e);
        e = Case_UeaJournalBrokerFlatCompletionRequiresBrokerWorkingOwnershipClean();
        if (e != null) return (false, e);
        e = Case_UeaLatchCreateRequiresInstrumentAndReason();
        if (e != null) return (false, e);
        e = Case_UeaLatchClearRequiresCleanFlatAndEligibleReason();
        if (e != null) return (false, e);
        e = Case_UeaMismatchReleaseRequiresValidatorAndNonContradictoryFrame();
        if (e != null) return (false, e);
        e = Case_UeaShutdownSafeVerdictRequiresCleanFlat();
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

    private static string? Case_RiskCoverageSubmitsBypassEntryOnlyBlocks()
    {
        if (!ExecutionPermissionAuthority.IsRiskCoverageSubmitPath("SUBMIT_PROTECTIVE_STOP"))
            return "expected protective stop to classify as risk coverage";
        if (!ExecutionPermissionAuthority.IsRiskCoverageSubmitPath("SUBMIT_TARGET"))
            return "expected target to classify as risk coverage";
        if (ExecutionPermissionAuthority.ShouldApplyEntryRiskBlockToSubmitPath("SUBMIT_PROTECTIVE_STOP"))
            return "expected protective stop to bypass entry-only instrument blocks";
        if (ExecutionPermissionAuthority.ShouldApplyEntryRiskBlockToSubmitPath("SUBMIT_TARGET"))
            return "expected target to bypass entry-only instrument blocks";
        if (ExecutionPermissionAuthority.ShouldApplyEntryRiskBlockToSubmitPath("FLATTEN_ENQUEUE"))
            return "expected flatten enqueue to bypass entry-only instrument blocks";
        if (ExecutionPermissionAuthority.ShouldApplyEntryRiskBlockToSubmitPath("CANCEL_INTENT_ORDERS"))
            return "expected cancel to bypass entry-only instrument blocks";
        if (!ExecutionPermissionAuthority.ShouldApplyEntryRiskBlockToSubmitPath("SUBMIT_ENTRY_STOP"))
            return "expected entry stop to apply entry-only instrument blocks";
        if (!ExecutionPermissionAuthority.ShouldApplyEntryRiskBlockToSubmitPath("SUBMIT_MARKET_REENTRY"))
            return "expected market reentry to apply entry-only instrument blocks";

        bool EntryOnlyLatchByPath(string _, string? submitPath) =>
            ExecutionPermissionAuthority.ShouldApplyEntryRiskBlockToSubmitPath(submitPath);

        if (!ExecutionPermissionAuthority.TryAdapterOrderSubmitPreflight(
                () => false, _ => false, null, EntryOnlyLatchByPath, "MES", "SUBMIT_PROTECTIVE_STOP", out var protectiveDeny) ||
            !string.IsNullOrEmpty(protectiveDeny))
            return "expected protective stop to bypass entry-only latch preflight";

        if (!ExecutionPermissionAuthority.TryAdapterOrderSubmitPreflight(
                () => false, _ => false, null, EntryOnlyLatchByPath, "MES", "SUBMIT_TARGET", out var targetDeny) ||
            !string.IsNullOrEmpty(targetDeny))
            return "expected target to bypass entry-only latch preflight";

        if (ExecutionPermissionAuthority.TryAdapterOrderSubmitPreflight(
                () => false, _ => false, null, EntryOnlyLatchByPath, "MES", "SUBMIT_MARKET_REENTRY", out var reentryDeny) ||
            reentryDeny != "INSTRUMENT_FROZEN_OR_EPA_BLOCKED")
            return "expected market reentry to be denied by entry-only latch";

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
            if (decision.DenyGate != "AuthorityProtectiveSubmit")
                return "expected AuthorityProtectiveSubmit deny from prebuilt safety request";

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

            var sampledInstrumentDeny = uea.Evaluate(new AuthorityEvaluationRequest
            {
                Instrument = "MES",
                IntentId = "intent-sampled-instrument-deny",
                SubmitIntent = SubmitIntent.OpeningEntry,
                SubmitPath = "SUBMIT_ENTRY_STOP",
                UtcNow = utc,
                GlobalKillSwitchActive = () => false,
                MismatchExecutionBlocked = _ => false,
                MismatchExecutionBlockedForSubmit = (_, _) => false,
                InstrumentFrozenOrEpaBlocked = (_, _) => false,
                InstrumentFrozenOrEpaBlockReason = (_, _) => "ENGINE_SHOULD_NOT_RESAMPLE",
                PreflightGlobalKillSwitchActive = false,
                PreflightMismatchExecutionBlocked = false,
                PreflightInstrumentFrozenOrEpaBlocked = true,
                PreflightInstrumentFrozenOrEpaBlockReason = "DURABLE_RISK_LATCH_ACTIVE:FORCED_CONVERGENCE_FAILED:position_qty_delta_2"
            });
            if (sampledInstrumentDeny.Allowed ||
                sampledInstrumentDeny.DenyReason != "DURABLE_RISK_LATCH_ACTIVE:FORCED_CONVERGENCE_FAILED:position_qty_delta_2")
                return "expected UEA to preserve sampled exact instrument block reason";

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

    private static string? Case_UeaMarketReentryAuthorityBlocksUnsafeFrames()
    {
        var utc = DateTimeOffset.UtcNow;

        var missingFrame = UnifiedExecutionAuthority.EvaluateAction(new ExecutionAuthorityActionEvaluationRequest
        {
            Action = ExecutionAuthorityAction.MarketReentry,
            Source = "test",
            Instrument = "MYM",
            IntentId = "reentry-missing-frame",
            UtcNow = utc
        });
        if (missingFrame.Allowed || missingFrame.DenyReason != "AUTHORITY_FRAME_MISSING")
            return "expected market reentry authority to deny missing frame";

        var snapshotErrorFrame = ExecutionAuthorityFrameBuilder.Build(new ExecutionAuthorityFrameBuilderInput
        {
            Source = "test",
            Instrument = "MYM",
            IntentId = "reentry-snapshot-error",
            SubmitPath = "SUBMIT_MARKET_REENTRY",
            DecisionUtc = utc,
            FrameCreatedUtc = utc,
            SnapshotError = "account_snapshot_failed"
        });
        var snapshotError = UnifiedExecutionAuthority.EvaluateAction(new ExecutionAuthorityActionEvaluationRequest
        {
            Action = ExecutionAuthorityAction.MarketReentry,
            Source = "test",
            Instrument = "MYM",
            IntentId = "reentry-snapshot-error",
            UtcNow = utc,
            SnapshotSufficient = false,
            AuthorityFrame = snapshotErrorFrame
        });
        if (snapshotError.Allowed || snapshotError.DenyReason != "SNAPSHOT_INSUFFICIENT")
            return "expected market reentry authority to deny insufficient snapshot";

        var killFrame = ExecutionAuthorityFrameBuilder.Build(new ExecutionAuthorityFrameBuilderInput
        {
            Source = "test",
            Instrument = "MYM",
            IntentId = "reentry-kill",
            SubmitPath = "SUBMIT_MARKET_REENTRY",
            DecisionUtc = utc,
            FrameCreatedUtc = utc,
            BrokerSnapshotCapturedUtc = utc,
            PreflightAuthoritySampled = true,
            PreflightGlobalKillSwitchActive = true
        });
        var kill = UnifiedExecutionAuthority.EvaluateAction(new ExecutionAuthorityActionEvaluationRequest
        {
            Action = ExecutionAuthorityAction.MarketReentry,
            Source = "test",
            Instrument = "MYM",
            IntentId = "reentry-kill",
            UtcNow = utc,
            SnapshotSufficient = true,
            AuthorityFrame = killFrame
        });
        if (kill.Allowed || kill.DenyReason != "GLOBAL_KILL_SWITCH_ACTIVE")
            return "expected market reentry authority to deny global kill switch";

        var untrackedFrame = ExecutionAuthorityFrameBuilder.Build(new ExecutionAuthorityFrameBuilderInput
        {
            Source = "test",
            Instrument = "MYM",
            IntentId = "reentry-untracked",
            SubmitPath = "SUBMIT_MARKET_REENTRY",
            DecisionUtc = utc,
            FrameCreatedUtc = utc,
            BrokerSnapshotCapturedUtc = utc,
            BrokerPositionQty = 1
        });
        var untracked = UnifiedExecutionAuthority.EvaluateAction(new ExecutionAuthorityActionEvaluationRequest
        {
            Action = ExecutionAuthorityAction.MarketReentry,
            Source = "test",
            Instrument = "MYM",
            IntentId = "reentry-untracked",
            UtcNow = utc,
            SnapshotSufficient = true,
            AuthorityFrame = untrackedFrame
        });
        if (untracked.Allowed || untracked.DenyReason != "UNTRACKED_BROKER_EXPOSURE")
            return "expected market reentry authority to deny untracked broker exposure";

        var boundedSiblingLagFrame = ExecutionAuthorityFrameBuilder.Build(new ExecutionAuthorityFrameBuilderInput
        {
            Source = "test",
            Instrument = "MYM",
            IntentId = "reentry-bounded-sibling",
            SubmitPath = "SUBMIT_MARKET_REENTRY",
            DecisionUtc = utc,
            FrameCreatedUtc = utc,
            BrokerSnapshotCapturedUtc = utc,
            BrokerPositionQty = 1,
            QecPendingAlignment = true
        });
        var boundedSiblingLag = UnifiedExecutionAuthority.EvaluateAction(new ExecutionAuthorityActionEvaluationRequest
        {
            Action = ExecutionAuthorityAction.MarketReentry,
            Source = "test",
            Instrument = "MYM",
            IntentId = "reentry-bounded-sibling",
            UtcNow = utc,
            SnapshotSufficient = true,
            AuthorityFrame = boundedSiblingLagFrame
        });
        if (!boundedSiblingLag.Allowed)
            return "expected market reentry authority to allow bounded sibling-transition alignment lag";

        var staleGrossOpenFrame = ExecutionAuthorityFrameBuilder.Build(new ExecutionAuthorityFrameBuilderInput
        {
            Source = "test",
            Instrument = "MYM",
            IntentId = "reentry-stale-gross",
            SubmitPath = "SUBMIT_MARKET_REENTRY",
            DecisionUtc = utc,
            FrameCreatedUtc = utc,
            BrokerSnapshotCapturedUtc = utc,
            BrokerPositionQty = 0,
            JournalOpenQty = 1
        });
        var staleGrossOpen = UnifiedExecutionAuthority.EvaluateAction(new ExecutionAuthorityActionEvaluationRequest
        {
            Action = ExecutionAuthorityAction.MarketReentry,
            Source = "test",
            Instrument = "MYM",
            IntentId = "reentry-stale-gross",
            UtcNow = utc,
            SnapshotSufficient = true,
            AuthorityFrame = staleGrossOpenFrame
        });
        if (staleGrossOpen.Allowed || staleGrossOpen.DenyReason != "AUTHORITY_FRAME_CONTRADICTION")
            return "expected market reentry authority to deny broker-flat/robot-open contradiction without bounded alignment";

        var boundedRunnerLagFrame = ExecutionAuthorityFrameBuilder.Build(new ExecutionAuthorityFrameBuilderInput
        {
            Source = "test",
            Instrument = "MYM",
            IntentId = "reentry-bounded-runner",
            SubmitPath = "SUBMIT_MARKET_REENTRY",
            DecisionUtc = utc,
            FrameCreatedUtc = utc,
            BrokerSnapshotCapturedUtc = utc,
            BrokerPositionQty = 0,
            JournalOpenQty = 1,
            QecPendingAlignment = true
        });
        var boundedRunnerLag = UnifiedExecutionAuthority.EvaluateAction(new ExecutionAuthorityActionEvaluationRequest
        {
            Action = ExecutionAuthorityAction.MarketReentry,
            Source = "test",
            Instrument = "MYM",
            IntentId = "reentry-bounded-runner",
            UtcNow = utc,
            SnapshotSufficient = true,
            AuthorityFrame = boundedRunnerLagFrame
        });
        if (!boundedRunnerLag.Allowed)
            return "expected market reentry authority to allow bounded broker-flat runner-journal alignment lag";

        var workingEmptyRegistryFrame = ExecutionAuthorityFrameBuilder.Build(new ExecutionAuthorityFrameBuilderInput
        {
            Source = "test",
            Instrument = "MYM",
            IntentId = "reentry-working-empty-registry",
            SubmitPath = "SUBMIT_MARKET_REENTRY",
            DecisionUtc = utc,
            FrameCreatedUtc = utc,
            BrokerSnapshotCapturedUtc = utc,
            BrokerWorkingOrdersCount = 1,
            UseInstrumentExecutionAuthority = true,
            QecPendingAlignment = true
        });
        var workingEmptyRegistry = UnifiedExecutionAuthority.EvaluateAction(new ExecutionAuthorityActionEvaluationRequest
        {
            Action = ExecutionAuthorityAction.MarketReentry,
            Source = "test",
            Instrument = "MYM",
            IntentId = "reentry-working-empty-registry",
            UtcNow = utc,
            SnapshotSufficient = true,
            AuthorityFrame = workingEmptyRegistryFrame
        });
        if (workingEmptyRegistry.Allowed || workingEmptyRegistry.DenyReason != "UNTRACKED_BROKER_EXPOSURE")
            return "expected market reentry authority to deny working broker orders with empty IEA registry";

        return null;
    }

    private static string? Case_UeaEntrySubmitAuthorityBlocksUnsafeFrames()
    {
        var utc = DateTimeOffset.UtcNow;

        var missingFrame = UnifiedExecutionAuthority.EvaluateAction(new ExecutionAuthorityActionEvaluationRequest
        {
            Action = ExecutionAuthorityAction.EntrySubmit,
            Source = "test",
            Instrument = "MES",
            IntentId = "entry-missing-frame",
            UtcNow = utc
        });
        if (missingFrame.Allowed || missingFrame.DenyReason != "AUTHORITY_FRAME_MISSING")
            return "expected entry authority to deny missing frame";

        var killFrame = ExecutionAuthorityFrameBuilder.Build(new ExecutionAuthorityFrameBuilderInput
        {
            Source = "test",
            Instrument = "MES",
            IntentId = "entry-kill",
            SubmitPath = "SUBMIT_ENTRY_STOP",
            DecisionUtc = utc,
            FrameCreatedUtc = utc,
            BrokerSnapshotCapturedUtc = utc,
            PreflightAuthoritySampled = true,
            PreflightGlobalKillSwitchActive = true
        });
        var kill = UnifiedExecutionAuthority.EvaluateAction(new ExecutionAuthorityActionEvaluationRequest
        {
            Action = ExecutionAuthorityAction.EntrySubmit,
            Source = "test",
            Instrument = "MES",
            IntentId = "entry-kill",
            UtcNow = utc,
            SnapshotSufficient = true,
            AuthorityFrame = killFrame
        });
        if (kill.Allowed || kill.DenyReason != "GLOBAL_KILL_SWITCH_ACTIVE")
            return "expected entry authority to deny global kill switch";

        var untrackedFrame = ExecutionAuthorityFrameBuilder.Build(new ExecutionAuthorityFrameBuilderInput
        {
            Source = "test",
            Instrument = "MES",
            IntentId = "entry-untracked",
            SubmitPath = "SUBMIT_ENTRY_STOP",
            DecisionUtc = utc,
            FrameCreatedUtc = utc,
            BrokerSnapshotCapturedUtc = utc,
            BrokerPositionQty = 1
        });
        var untracked = UnifiedExecutionAuthority.EvaluateAction(new ExecutionAuthorityActionEvaluationRequest
        {
            Action = ExecutionAuthorityAction.EntrySubmit,
            Source = "test",
            Instrument = "MES",
            IntentId = "entry-untracked",
            UtcNow = utc,
            SnapshotSufficient = true,
            AuthorityFrame = untrackedFrame
        });
        if (untracked.Allowed || untracked.DenyReason != "UNTRACKED_BROKER_EXPOSURE")
            return "expected entry authority to deny untracked broker exposure";

        var staleGrossOpenFrame = ExecutionAuthorityFrameBuilder.Build(new ExecutionAuthorityFrameBuilderInput
        {
            Source = "test",
            Instrument = "MES",
            IntentId = "entry-stale-gross",
            SubmitPath = "SUBMIT_ENTRY_STOP",
            DecisionUtc = utc,
            FrameCreatedUtc = utc,
            BrokerSnapshotCapturedUtc = utc,
            BrokerPositionQty = 0,
            JournalOpenQty = 1
        });
        var staleGrossOpen = UnifiedExecutionAuthority.EvaluateAction(new ExecutionAuthorityActionEvaluationRequest
        {
            Action = ExecutionAuthorityAction.EntrySubmit,
            Source = "test",
            Instrument = "MES",
            IntentId = "entry-stale-gross",
            UtcNow = utc,
            SnapshotSufficient = true,
            AuthorityFrame = staleGrossOpenFrame
        });
        if (staleGrossOpen.Allowed || staleGrossOpen.DenyReason != "AUTHORITY_FRAME_CONTRADICTION")
            return "expected entry authority to deny broker-flat/robot-open contradiction";

        var cleanFrame = ExecutionAuthorityFrameBuilder.Build(new ExecutionAuthorityFrameBuilderInput
        {
            Source = "test",
            Instrument = "MES",
            IntentId = "entry-clean",
            SubmitPath = "SUBMIT_ENTRY_STOP",
            DecisionUtc = utc,
            FrameCreatedUtc = utc,
            BrokerSnapshotCapturedUtc = utc
        });
        var clean = UnifiedExecutionAuthority.EvaluateAction(new ExecutionAuthorityActionEvaluationRequest
        {
            Action = ExecutionAuthorityAction.EntrySubmit,
            Source = "test",
            Instrument = "MES",
            IntentId = "entry-clean",
            UtcNow = utc,
            SnapshotSufficient = true,
            AuthorityFrame = cleanFrame
        });
        if (!clean.Allowed)
            return "expected entry authority to allow clean frame";

        return null;
    }

    private static string? Case_UeaProtectiveSubmitAuthorityAllowsSafetyThroughEntryBlocks()
    {
        var utc = DateTimeOffset.UtcNow;

        var frame = ExecutionAuthorityFrameBuilder.Build(new ExecutionAuthorityFrameBuilderInput
        {
            Source = "test",
            Instrument = "MES",
            IntentId = "protective-entry-blocks",
            SubmitPath = "SUBMIT_PROTECTIVE_STOP",
            DecisionUtc = utc,
            FrameCreatedUtc = utc,
            BrokerSnapshotCapturedUtc = utc,
            BrokerPositionQty = 1,
            JournalOpenQty = 1,
            BrokerStopQty = 1,
            PreflightAuthoritySampled = true,
            PreflightGlobalKillSwitchActive = true,
            PreflightMismatchExecutionBlocked = true
        });
        var protective = UnifiedExecutionAuthority.EvaluateAction(new ExecutionAuthorityActionEvaluationRequest
        {
            Action = ExecutionAuthorityAction.ProtectiveSubmit,
            Source = "test",
            Instrument = "MES",
            IntentId = "protective-entry-blocks",
            UtcNow = utc,
            SnapshotSufficient = true,
            AuthorityFrame = frame
        });
        if (!protective.Allowed)
            return "expected protective authority to allow safety submit through entry/kill/mismatch blocks";

        var snapshotErrorFrame = ExecutionAuthorityFrameBuilder.Build(new ExecutionAuthorityFrameBuilderInput
        {
            Source = "test",
            Instrument = "MES",
            IntentId = "protective-snapshot-error",
            SubmitPath = "SUBMIT_PROTECTIVE_STOP",
            DecisionUtc = utc,
            FrameCreatedUtc = utc,
            SnapshotError = "account_snapshot_failed"
        });
        var snapshotError = UnifiedExecutionAuthority.EvaluateAction(new ExecutionAuthorityActionEvaluationRequest
        {
            Action = ExecutionAuthorityAction.ProtectiveSubmit,
            Source = "test",
            Instrument = "MES",
            IntentId = "protective-snapshot-error",
            UtcNow = utc,
            SnapshotSufficient = false,
            AuthorityFrame = snapshotErrorFrame
        });
        if (snapshotError.Allowed || snapshotError.DenyReason != "SNAPSHOT_INSUFFICIENT")
            return "expected protective authority to deny insufficient snapshot";

        var root = Path.Combine(Path.GetTempPath(), "auth_protective_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var activeUea = new UnifiedExecutionAuthority(new RobotLogger(root));
            var activeCoverage = activeUea.Evaluate(new AuthorityEvaluationRequest
            {
                Instrument = "MES",
                IntentId = "protective-active-through-kill",
                SubmitIntent = SubmitIntent.RiskCoverage,
                SubmitPath = "SUBMIT_PROTECTIVE_STOP",
                UtcNow = utc,
                PreflightGlobalKillSwitchActive = true,
                PreflightMismatchExecutionBlocked = true,
                PreflightMismatchExecutionBlockedForSubmit = true,
                PreflightInstrumentFrozenOrEpaBlocked = false
            });
            if (!activeCoverage.Allowed)
                return "expected active UEA to allow risk coverage through global kill and mismatch entry blocks";
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

        return null;
    }

    private static string? Case_UeaProtectiveBlockAuthorityRequiresCriticalCreateAndCoveredClear()
    {
        var utc = DateTimeOffset.UtcNow;

        var missingInstrumentCreate = UnifiedExecutionAuthority.EvaluateAction(new ExecutionAuthorityActionEvaluationRequest
        {
            Action = ExecutionAuthorityAction.ProtectiveBlockCreate,
            Source = "test",
            Instrument = "",
            UtcNow = utc,
            HasProtectiveBlock = true
        });
        if (missingInstrumentCreate.Allowed || missingInstrumentCreate.DenyReason != "INSTRUMENT_REQUIRED")
            return "expected protective block create to deny missing instrument";

        var nonCriticalFrame = ExecutionAuthorityFrameBuilder.Build(new ExecutionAuthorityFrameBuilderInput
        {
            Source = "test",
            Instrument = "MES",
            ProtectiveCoverageState = "PROTECTIVE_OK",
            DecisionUtc = utc,
            FrameCreatedUtc = utc,
            BrokerSnapshotCapturedUtc = utc
        });
        var nonCriticalCreate = UnifiedExecutionAuthority.EvaluateAction(new ExecutionAuthorityActionEvaluationRequest
        {
            Action = ExecutionAuthorityAction.ProtectiveBlockCreate,
            Source = "test",
            Instrument = "MES",
            UtcNow = utc,
            HasProtectiveBlock = false,
            AuthorityFrame = nonCriticalFrame
        });
        if (nonCriticalCreate.Allowed || nonCriticalCreate.DenyReason != "PROTECTIVE_CRITICAL_REQUIRED")
            return "expected protective block create to require critical protective evidence";

        var criticalFrame = ExecutionAuthorityFrameBuilder.Build(new ExecutionAuthorityFrameBuilderInput
        {
            Source = "test",
            Instrument = "MES",
            ProtectiveCoverageState = "PROTECTIVE_MISSING_STOP",
            DecisionUtc = utc,
            FrameCreatedUtc = utc,
            BrokerSnapshotCapturedUtc = utc,
            BrokerPositionQty = 1,
            ProtectiveMissingQty = 1
        });
        var criticalCreate = UnifiedExecutionAuthority.EvaluateAction(new ExecutionAuthorityActionEvaluationRequest
        {
            Action = ExecutionAuthorityAction.ProtectiveBlockCreate,
            Source = "test",
            Instrument = "MES",
            UtcNow = utc,
            HasProtectiveBlock = true,
            AuthorityFrame = criticalFrame
        });
        if (!criticalCreate.Allowed)
            return "expected protective block create to allow scoped critical protective evidence";

        var underCoveredFrame = ExecutionAuthorityFrameBuilder.Build(new ExecutionAuthorityFrameBuilderInput
        {
            Source = "test",
            Instrument = "MES",
            ProtectiveCoverageState = "PROTECTIVE_OK",
            DecisionUtc = utc,
            FrameCreatedUtc = utc,
            BrokerSnapshotCapturedUtc = utc,
            BrokerPositionQty = 2,
            BrokerStopQty = 1
        });
        var underCoveredClear = UnifiedExecutionAuthority.EvaluateAction(new ExecutionAuthorityActionEvaluationRequest
        {
            Action = ExecutionAuthorityAction.ProtectiveBlockClear,
            Source = "test",
            Instrument = "MES",
            UtcNow = utc,
            BrokerAbsQty = 2,
            HasProtectiveBlock = false,
            SnapshotSufficient = true,
            AuthorityFrame = underCoveredFrame
        });
        if (underCoveredClear.Allowed || underCoveredClear.DenyReason != "PROTECTIVE_STOP_COVERAGE_INSUFFICIENT")
            return "expected protective block clear to deny open exposure without stop coverage";

        var coveredFrame = ExecutionAuthorityFrameBuilder.Build(new ExecutionAuthorityFrameBuilderInput
        {
            Source = "test",
            Instrument = "MES",
            ProtectiveCoverageState = "PROTECTIVE_OK",
            DecisionUtc = utc,
            FrameCreatedUtc = utc,
            BrokerSnapshotCapturedUtc = utc,
            BrokerPositionQty = 2,
            BrokerStopQty = 2
        });
        var coveredClear = UnifiedExecutionAuthority.EvaluateAction(new ExecutionAuthorityActionEvaluationRequest
        {
            Action = ExecutionAuthorityAction.ProtectiveBlockClear,
            Source = "test",
            Instrument = "MES",
            UtcNow = utc,
            BrokerAbsQty = 2,
            HasProtectiveBlock = false,
            SnapshotSufficient = true,
            AuthorityFrame = coveredFrame
        });
        if (!coveredClear.Allowed)
            return "expected protective block clear to allow open exposure only when stop coverage is sufficient";

        return null;
    }

    private static string? Case_UeaCancelSubmitAuthorityAllowsSafetyThroughEntryBlocks()
    {
        var utc = DateTimeOffset.UtcNow;

        var frame = ExecutionAuthorityFrameBuilder.Build(new ExecutionAuthorityFrameBuilderInput
        {
            Source = "test",
            Instrument = "MES",
            IntentId = "cancel-entry-blocks",
            SubmitPath = "CANCEL_INTENT_ORDERS",
            DecisionUtc = utc,
            FrameCreatedUtc = utc,
            BrokerSnapshotCapturedUtc = utc,
            BrokerWorkingOrdersCount = 1,
            PreflightAuthoritySampled = true,
            PreflightGlobalKillSwitchActive = true,
            PreflightMismatchExecutionBlocked = true,
            PreflightInstrumentFrozenOrEpaBlocked = true,
            PreflightInstrumentFrozenOrEpaBlockReason = "DURABLE_RISK_LATCH_ACTIVE:FORCED_CONVERGENCE_FAILED"
        });
        var cancel = UnifiedExecutionAuthority.EvaluateAction(new ExecutionAuthorityActionEvaluationRequest
        {
            Action = ExecutionAuthorityAction.CancelSubmit,
            Source = "test",
            Instrument = "MES",
            IntentId = "cancel-entry-blocks",
            UtcNow = utc,
            SnapshotSufficient = false,
            AuthorityFrame = frame
        });
        if (!cancel.Allowed)
            return "expected cancel authority to allow safety cancel through entry/kill/mismatch blocks";

        var noFrameCancel = UnifiedExecutionAuthority.EvaluateAction(new ExecutionAuthorityActionEvaluationRequest
        {
            Action = ExecutionAuthorityAction.CancelSubmit,
            Source = "test",
            Instrument = "MES",
            IntentId = "cancel-no-frame",
            UtcNow = utc,
            SnapshotSufficient = false
        });
        if (!noFrameCancel.Allowed)
            return "expected cancel authority to allow safety cancel without a canonical frame";

        var root = Path.Combine(Path.GetTempPath(), "auth_cancel_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var activeUea = new UnifiedExecutionAuthority(new RobotLogger(root));
            var activeCancel = activeUea.Evaluate(new AuthorityEvaluationRequest
            {
                Instrument = "MES",
                IntentId = "cancel-active-through-kill",
                SubmitIntent = SubmitIntent.RiskReducing,
                SubmitPath = "CANCEL_INTENT_ORDERS",
                UtcNow = utc,
                PreflightGlobalKillSwitchActive = true,
                PreflightMismatchExecutionBlocked = true,
                PreflightMismatchExecutionBlockedForSubmit = true,
                PreflightInstrumentFrozenOrEpaBlocked = false
            });
            if (!activeCancel.Allowed)
                return "expected active UEA to allow risk-reducing cancel through global kill and mismatch entry blocks";
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

        return null;
    }

    private static string? Case_UeaFlattenAuthorityAllowsSafetyThroughEntryBlocks()
    {
        var utc = DateTimeOffset.UtcNow;

        var frame = ExecutionAuthorityFrameBuilder.Build(new ExecutionAuthorityFrameBuilderInput
        {
            Source = "test",
            Instrument = "MES",
            IntentId = "flatten-entry-blocks",
            SubmitPath = "FLATTEN_ENQUEUE",
            DecisionUtc = utc,
            FrameCreatedUtc = utc,
            BrokerSnapshotCapturedUtc = utc,
            BrokerPositionQty = 1,
            JournalOpenQty = 1,
            PreflightAuthoritySampled = true,
            PreflightGlobalKillSwitchActive = true,
            PreflightMismatchExecutionBlocked = true,
            PreflightInstrumentFrozenOrEpaBlocked = true,
            PreflightInstrumentFrozenOrEpaBlockReason = "DURABLE_RISK_LATCH_ACTIVE:FORCED_CONVERGENCE_FAILED"
        });
        var flatten = UnifiedExecutionAuthority.EvaluateAction(new ExecutionAuthorityActionEvaluationRequest
        {
            Action = ExecutionAuthorityAction.Flatten,
            Source = "test",
            Instrument = "MES",
            IntentId = "flatten-entry-blocks",
            UtcNow = utc,
            SnapshotSufficient = true,
            AuthorityFrame = frame
        });
        if (!flatten.Allowed)
            return "expected flatten authority to allow emergency safety action through entry blocks";

        var snapshotErrorFrame = ExecutionAuthorityFrameBuilder.Build(new ExecutionAuthorityFrameBuilderInput
        {
            Source = "test",
            Instrument = "MES",
            IntentId = "flatten-snapshot-error",
            SubmitPath = "FLATTEN_ENQUEUE",
            DecisionUtc = utc,
            FrameCreatedUtc = utc,
            SnapshotError = "account_snapshot_failed"
        });
        var snapshotError = UnifiedExecutionAuthority.EvaluateAction(new ExecutionAuthorityActionEvaluationRequest
        {
            Action = ExecutionAuthorityAction.Flatten,
            Source = "test",
            Instrument = "MES",
            IntentId = "flatten-snapshot-error",
            UtcNow = utc,
            SnapshotSufficient = false,
            AuthorityFrame = snapshotErrorFrame
        });
        if (snapshotError.Allowed || snapshotError.DenyReason != "SNAPSHOT_INSUFFICIENT")
            return "expected flatten authority to deny insufficient snapshot";

        var root = Path.Combine(Path.GetTempPath(), "auth_flatten_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var activeUea = new UnifiedExecutionAuthority(new RobotLogger(root));
            var activeFlatten = activeUea.Evaluate(new AuthorityEvaluationRequest
            {
                Instrument = "MES",
                IntentId = "flatten-active-through-kill",
                SubmitIntent = SubmitIntent.Emergency,
                SubmitPath = "FLATTEN_ENQUEUE",
                UtcNow = utc,
                PreflightGlobalKillSwitchActive = true,
                PreflightMismatchExecutionBlocked = true,
                PreflightMismatchExecutionBlockedForSubmit = true,
                PreflightInstrumentFrozenOrEpaBlocked = false
            });
            if (!activeFlatten.Allowed)
                return "expected active UEA to allow emergency flatten through global kill and mismatch entry blocks";
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

        return null;
    }

    private static string? Case_UeaLifecycleAuthorityDeniesTerminalCommitWithOpenReentry()
    {
        var utc = DateTimeOffset.UtcNow;
        var denied = UnifiedExecutionAuthority.EvaluateAction(new ExecutionAuthorityActionEvaluationRequest
        {
            Action = ExecutionAuthorityAction.TerminalCommit,
            Source = "test",
            Instrument = "MYM",
            Stream = "YM1",
            IntentId = "reentry-open",
            UtcNow = utc,
            CommitReason = "NO_TRADE_LATE_START_UNIFIED_BREAKOUT_INVALID",
            EventType = "NO_TRADE_LATE_START_UNIFIED_BREAKOUT_INVALID",
            HasOpenLifecycleExposureOrPendingReentry = true,
            HasCompletedTradeForCurrentStream = false,
            IsIntentionalOpenLifecycleTerminalCommit = false
        });
        if (denied.Allowed || denied.DenyReason != "OPEN_LIFECYCLE_EXPOSURE_OR_REENTRY")
            return "expected lifecycle authority to deny terminal commit while reentry/open lifecycle evidence remains";

        var slotExpiry = UnifiedExecutionAuthority.EvaluateAction(new ExecutionAuthorityActionEvaluationRequest
        {
            Action = ExecutionAuthorityAction.TerminalCommit,
            Source = "test",
            Instrument = "MYM",
            Stream = "YM1",
            IntentId = "reentry-slot-expiry",
            UtcNow = utc,
            CommitReason = "SLOT_EXPIRED",
            EventType = "SLOT_EXPIRED",
            HasOpenLifecycleExposureOrPendingReentry = true,
            HasCompletedTradeForCurrentStream = false,
            IsIntentionalOpenLifecycleTerminalCommit = true
        });
        if (!slotExpiry.Allowed)
            return "expected lifecycle authority to allow intentional slot-expiry terminal commit";

        var completedTrade = UnifiedExecutionAuthority.EvaluateAction(new ExecutionAuthorityActionEvaluationRequest
        {
            Action = ExecutionAuthorityAction.TerminalCommit,
            Source = "test",
            Instrument = "MYM",
            Stream = "YM1",
            IntentId = "reentry-complete",
            UtcNow = utc,
            CommitReason = "TRADE_COMPLETED",
            EventType = "TRADE_COMPLETED",
            HasOpenLifecycleExposureOrPendingReentry = true,
            HasCompletedTradeForCurrentStream = true,
            IsIntentionalOpenLifecycleTerminalCommit = false
        });
        if (!completedTrade.Allowed)
            return "expected lifecycle authority to allow completed trade terminal commit";

        return null;
    }

    private static string? Case_UeaLifecycleAuthorityDeniesStaleSessionSweepForTrackedReentry()
    {
        var utc = DateTimeOffset.UtcNow;
        var tracked = UnifiedExecutionAuthority.EvaluateAction(new ExecutionAuthorityActionEvaluationRequest
        {
            Action = ExecutionAuthorityAction.SessionCloseGlobalSweep,
            Source = "test",
            Instrument = "MYM",
            IntentId = "reentry-open",
            UtcNow = utc,
            HasActiveTrackedReentry = true,
            HasRobotEvidence = true,
            BrokerAbsQty = 2,
            BrokerWorkingOrderCount = 2,
            JournalOpenQty = 2
        });
        if (tracked.Allowed || tracked.DenyReason != "TRACKED_ACTIVE_REENTRY")
            return "expected lifecycle authority to deny stale session sweep for tracked active reentry";

        var unknown = UnifiedExecutionAuthority.EvaluateAction(new ExecutionAuthorityActionEvaluationRequest
        {
            Action = ExecutionAuthorityAction.SessionCloseGlobalSweep,
            Source = "test",
            Instrument = "MYM",
            UtcNow = utc,
            HasActiveTrackedReentry = false,
            HasRobotEvidence = false,
            BrokerAbsQty = 2,
            BrokerWorkingOrderCount = 0,
            JournalOpenQty = 0
        });
        if (unknown.Allowed || unknown.DenyReason != "NO_ROBOT_EVIDENCE")
            return "expected lifecycle authority to deny session sweep without robot evidence";

        var staleWindow = UnifiedExecutionAuthority.EvaluateAction(new ExecutionAuthorityActionEvaluationRequest
        {
            Action = ExecutionAuthorityAction.SessionCloseGlobalSweep,
            Source = "test",
            Instrument = "MYM",
            IntentId = "stale-window",
            UtcNow = utc,
            HasActiveTrackedReentry = false,
            HasActiveLifecycleEvidence = true,
            HasRobotEvidence = true,
            SessionCloseSweepAnchorUtc = utc.AddHours(-1),
            SessionCloseSweepWindowAgeMs = 60 * 60 * 1000,
            SessionCloseSweepWindowFresh = false,
            BrokerAbsQty = 2,
            BrokerWorkingOrderCount = 0,
            JournalOpenQty = 2
        });
        if (staleWindow.Allowed || staleWindow.DenyReason != "SESSION_CLOSE_SWEEP_WINDOW_STALE")
            return "expected lifecycle authority to deny session sweep after stale close-window expiry";

        var journalWithoutLifecycle = UnifiedExecutionAuthority.EvaluateAction(new ExecutionAuthorityActionEvaluationRequest
        {
            Action = ExecutionAuthorityAction.SessionCloseGlobalSweep,
            Source = "test",
            Instrument = "MYM",
            IntentId = "journal-no-lifecycle",
            UtcNow = utc,
            HasActiveTrackedReentry = false,
            HasActiveLifecycleEvidence = false,
            HasRobotEvidence = true,
            BrokerAbsQty = 2,
            BrokerWorkingOrderCount = 0,
            JournalOpenQty = 2
        });
        if (journalWithoutLifecycle.Allowed || journalWithoutLifecycle.DenyReason != "JOURNAL_OPEN_WITHOUT_ACTIVE_LIFECYCLE")
            return "expected lifecycle authority to deny journal-open sweep without active lifecycle evidence";

        var freshLifecycleExposure = UnifiedExecutionAuthority.EvaluateAction(new ExecutionAuthorityActionEvaluationRequest
        {
            Action = ExecutionAuthorityAction.SessionCloseGlobalSweep,
            Source = "test",
            Instrument = "MYM",
            IntentId = "fresh-original",
            UtcNow = utc,
            HasActiveTrackedReentry = false,
            HasActiveLifecycleEvidence = true,
            HasRobotEvidence = true,
            SessionCloseSweepAnchorUtc = utc.AddMinutes(-5),
            SessionCloseSweepWindowAgeMs = 5 * 60 * 1000,
            SessionCloseSweepWindowFresh = true,
            BrokerAbsQty = 2,
            BrokerWorkingOrderCount = 0,
            JournalOpenQty = 2
        });
        if (!freshLifecycleExposure.Allowed)
            return "expected lifecycle authority to allow fresh session-close lifecycle exposure with no active tracked reentry";

        return null;
    }

    private static string? Case_UeaJournalBrokerFlatCompletionRequiresBrokerWorkingOwnershipClean()
    {
        var utc = DateTimeOffset.UtcNow;
        var brokerOpen = UnifiedExecutionAuthority.EvaluateAction(new ExecutionAuthorityActionEvaluationRequest
        {
            Action = ExecutionAuthorityAction.JournalCompleteBrokerFlat,
            Source = "test",
            Instrument = "NG",
            IntentId = "ng-open",
            UtcNow = utc,
            BrokerAbsQty = 1,
            BrokerWorkingOrderCount = 0,
            JournalOpenQty = 1
        });
        if (brokerOpen.Allowed || brokerOpen.DenyReason != "BROKER_NOT_FLAT")
            return "expected journal broker-flat completion to deny when broker position remains";

        var workingOpen = UnifiedExecutionAuthority.EvaluateAction(new ExecutionAuthorityActionEvaluationRequest
        {
            Action = ExecutionAuthorityAction.JournalCompleteBrokerFlat,
            Source = "test",
            Instrument = "M2K",
            IntentId = "m2k-working",
            UtcNow = utc,
            BrokerAbsQty = 0,
            BrokerWorkingOrderCount = 2,
            JournalOpenQty = 1
        });
        if (workingOpen.Allowed || workingOpen.DenyReason != "WORKING_ORDERS_OPEN")
            return "expected journal broker-flat completion to deny while working orders remain";

        var ownershipOpen = UnifiedExecutionAuthority.EvaluateAction(new ExecutionAuthorityActionEvaluationRequest
        {
            Action = ExecutionAuthorityAction.JournalCompleteBrokerFlat,
            Source = "test",
            Instrument = "MYM",
            IntentId = "mym-ownership",
            UtcNow = utc,
            BrokerAbsQty = 0,
            BrokerWorkingOrderCount = 0,
            JournalOpenQty = 1,
            OwnershipOpenQty = 1,
            OwnershipActiveSlotCount = 1
        });
        if (ownershipOpen.Allowed || ownershipOpen.DenyReason != "OWNERSHIP_GROSS_OPEN")
            return "expected journal broker-flat completion to deny while ownership gross exposure remains";

        var insufficientSnapshot = UnifiedExecutionAuthority.EvaluateAction(new ExecutionAuthorityActionEvaluationRequest
        {
            Action = ExecutionAuthorityAction.JournalCompleteBrokerFlat,
            Source = "test",
            Instrument = "MNQ",
            IntentId = "mnq-manual",
            UtcNow = utc,
            SnapshotSufficient = false,
            BrokerAbsQty = 0,
            BrokerWorkingOrderCount = 0,
            JournalOpenQty = 1
        });
        if (insufficientSnapshot.Allowed || insufficientSnapshot.DenyReason != "SNAPSHOT_INSUFFICIENT")
            return "expected journal broker-flat completion to deny without sufficient broker/account evidence";

        var clean = UnifiedExecutionAuthority.EvaluateAction(new ExecutionAuthorityActionEvaluationRequest
        {
            Action = ExecutionAuthorityAction.JournalCompleteBrokerFlat,
            Source = "test",
            Instrument = "MES",
            IntentId = "mes-flat",
            UtcNow = utc,
            BrokerAbsQty = 0,
            BrokerWorkingOrderCount = 0,
            JournalOpenQty = 1,
            OwnershipOpenQty = 0,
            OwnershipActiveSlotCount = 0,
            OwnershipOrphanSlotCount = 0
        });
        if (!clean.Allowed)
            return "expected journal broker-flat completion to allow when broker/working/ownership are clean";

        return null;
    }

    private static string? Case_UeaLatchCreateRequiresInstrumentAndReason()
    {
        var utc = DateTimeOffset.UtcNow;

        var missingInstrument = UnifiedExecutionAuthority.EvaluateAction(new ExecutionAuthorityActionEvaluationRequest
        {
            Action = ExecutionAuthorityAction.LatchCreate,
            Source = "test",
            Instrument = "",
            DurableLatchReason = "FORCED_CONVERGENCE_FAILED",
            UtcNow = utc
        });
        if (missingInstrument.Allowed || missingInstrument.DenyReason != "INSTRUMENT_REQUIRED")
            return "expected latch-create authority to deny missing instrument";

        var missingReason = UnifiedExecutionAuthority.EvaluateAction(new ExecutionAuthorityActionEvaluationRequest
        {
            Action = ExecutionAuthorityAction.LatchCreate,
            Source = "test",
            Instrument = "MES",
            DurableLatchReason = "",
            UtcNow = utc
        });
        if (missingReason.Allowed || missingReason.DenyReason != "LATCH_REASON_REQUIRED")
            return "expected latch-create authority to deny missing reason";

        var validCreate = UnifiedExecutionAuthority.EvaluateAction(new ExecutionAuthorityActionEvaluationRequest
        {
            Action = ExecutionAuthorityAction.LatchCreate,
            Source = "test",
            Instrument = "MES",
            DurableLatchReason = "FORCED_CONVERGENCE_FAILED",
            UtcNow = utc
        });
        if (!validCreate.Allowed)
            return "expected latch-create authority to allow scoped fail-closed latch creation";

        return null;
    }

    private static string? Case_UeaLatchClearRequiresCleanFlatAndEligibleReason()
    {
        var utc = DateTimeOffset.UtcNow;
        var badReason = UnifiedExecutionAuthority.EvaluateAction(new ExecutionAuthorityActionEvaluationRequest
        {
            Action = ExecutionAuthorityAction.LatchClear,
            Source = "test",
            Instrument = "MNG",
            UtcNow = utc,
            DurableLatchReason = "MANUAL_OR_EXTERNAL_ORDER_DETECTED"
        });
        if (badReason.Allowed || badReason.DenyReason != "LATCH_REASON_NOT_AUTO_CLEAR_ELIGIBLE")
            return "expected latch clear to deny non-auto-clear reason";

        var brokerOpen = UnifiedExecutionAuthority.EvaluateAction(new ExecutionAuthorityActionEvaluationRequest
        {
            Action = ExecutionAuthorityAction.LatchClear,
            Source = "test",
            Instrument = "MNG",
            UtcNow = utc,
            DurableLatchReason = "FORCED_CONVERGENCE_FAILED:test",
            BrokerAbsQty = 1
        });
        if (brokerOpen.Allowed || brokerOpen.DenyReason != "BROKER_NOT_FLAT")
            return "expected latch clear to deny open broker qty";

        var protectiveBlocked = UnifiedExecutionAuthority.EvaluateAction(new ExecutionAuthorityActionEvaluationRequest
        {
            Action = ExecutionAuthorityAction.LatchClear,
            Source = "test",
            Instrument = "MNG",
            UtcNow = utc,
            DurableLatchReason = "FORCED_CONVERGENCE_FAILED:test",
            HasProtectiveBlock = true
        });
        if (protectiveBlocked.Allowed || protectiveBlocked.DenyReason != "PROTECTIVE_BLOCK_ACTIVE")
            return "expected latch clear to deny active protective block";

        var clean = UnifiedExecutionAuthority.EvaluateAction(new ExecutionAuthorityActionEvaluationRequest
        {
            Action = ExecutionAuthorityAction.LatchClear,
            Source = "test",
            Instrument = "MNG",
            UtcNow = utc,
            DurableLatchReason = "FORCED_CONVERGENCE_FAILED:test",
            AccountQty = 0,
            BrokerAbsQty = 0,
            BrokerWorkingOrderCount = 0,
            JournalOpenQty = 0,
            RealOpenQty = 0,
            RecoveryOpenQty = 0,
            HasSupervisoryBlock = false,
            HasProtectiveBlock = false,
            HasMismatchBlock = false
        });
        if (!clean.Allowed)
            return "expected latch clear to allow only when eligible reason and clean-flat predicates pass";

        return null;
    }

    private static string? Case_UeaMismatchReleaseRequiresValidatorAndNonContradictoryFrame()
    {
        var utc = DateTimeOffset.UtcNow;
        var validatorNotReadyFrame = ExecutionAuthorityFrameBuilder.Build(new ExecutionAuthorityFrameBuilderInput
        {
            Source = "test",
            Instrument = "M2K",
            CanonicalInstrument = "RTY",
            SubmitPath = "MISMATCH_RELEASE",
            DecisionUtc = utc,
            FrameCreatedUtc = utc,
            BrokerSnapshotCapturedUtc = utc,
            BrokerPositionQty = 0,
            BrokerWorkingOrdersCount = 1,
            JournalOpenQty = 2,
            IeaMismatchTrustedWorkingCount = 1,
            UseInstrumentExecutionAuthority = true,
            AuthorityState = "MISMATCH_RELEASE"
        });
        var validatorNotReady = UnifiedExecutionAuthority.EvaluateAction(new ExecutionAuthorityActionEvaluationRequest
        {
            Action = ExecutionAuthorityAction.MismatchRelease,
            Source = "test",
            Instrument = "M2K",
            UtcNow = utc,
            SnapshotSufficient = true,
            ReleaseValidatorReady = false,
            BrokerWorkingOrderCount = 1,
            JournalOpenQty = 2,
            AuthorityFrame = validatorNotReadyFrame
        });
        if (validatorNotReady.Allowed || validatorNotReady.DenyReason != "STATE_CONSISTENCY_NOT_RELEASE_READY")
            return "expected mismatch release to deny when state-consistency validator is not release-ready";

        var contradictoryFrame = ExecutionAuthorityFrameBuilder.Build(new ExecutionAuthorityFrameBuilderInput
        {
            Source = "test",
            Instrument = "M2K",
            CanonicalInstrument = "RTY",
            SubmitPath = "MISMATCH_RELEASE",
            DecisionUtc = utc,
            FrameCreatedUtc = utc,
            BrokerSnapshotCapturedUtc = utc,
            BrokerPositionQty = 0,
            BrokerWorkingOrdersCount = 1,
            JournalOpenQty = 2,
            IeaMismatchTrustedWorkingCount = 1,
            UseInstrumentExecutionAuthority = true,
            AuthorityState = "MISMATCH_RELEASE"
        });
        var contradictory = UnifiedExecutionAuthority.EvaluateAction(new ExecutionAuthorityActionEvaluationRequest
        {
            Action = ExecutionAuthorityAction.MismatchRelease,
            Source = "test",
            Instrument = "M2K",
            UtcNow = utc,
            SnapshotSufficient = true,
            ReleaseValidatorReady = true,
            BrokerWorkingOrderCount = 1,
            JournalOpenQty = 2,
            AuthorityFrame = contradictoryFrame
        });
        if (contradictory.Allowed || contradictory.DenyReason != "AUTHORITY_FRAME_CONTRADICTION")
            return "expected mismatch release to deny when authority frame has broker-net-flat/robot-open contradiction";

        var activeTrackedFrame = ExecutionAuthorityFrameBuilder.Build(new ExecutionAuthorityFrameBuilderInput
        {
            Source = "test",
            Instrument = "MNQ",
            CanonicalInstrument = "NQ",
            SubmitPath = "MISMATCH_RELEASE",
            DecisionUtc = utc,
            FrameCreatedUtc = utc,
            BrokerSnapshotCapturedUtc = utc,
            BrokerPositionQty = 1,
            BrokerWorkingOrdersCount = 2,
            BrokerStopQty = 1,
            JournalOpenQty = 1,
            OwnershipOpenQty = 1,
            OwnershipSignedQty = 1,
            OwnershipActiveSlots = 1,
            IeaMismatchTrustedWorkingCount = 2,
            UseInstrumentExecutionAuthority = true,
            AuthorityState = "MISMATCH_RELEASE"
        });
        var activeTracked = UnifiedExecutionAuthority.EvaluateAction(new ExecutionAuthorityActionEvaluationRequest
        {
            Action = ExecutionAuthorityAction.MismatchRelease,
            Source = "test",
            Instrument = "MNQ",
            UtcNow = utc,
            SnapshotSufficient = true,
            ReleaseValidatorReady = true,
            BrokerAbsQty = 1,
            BrokerWorkingOrderCount = 2,
            JournalOpenQty = 1,
            OwnershipOpenQty = 1,
            OwnershipActiveSlotCount = 1,
            AuthorityFrame = activeTrackedFrame
        });
        if (!activeTracked.Allowed)
            return "expected mismatch release to allow validator-ready tracked exposure without authority-frame contradiction";

        var snapshotMissing = UnifiedExecutionAuthority.EvaluateAction(new ExecutionAuthorityActionEvaluationRequest
        {
            Action = ExecutionAuthorityAction.MismatchRelease,
            Source = "test",
            Instrument = "MGC",
            UtcNow = utc,
            SnapshotSufficient = false,
            ReleaseValidatorReady = true,
            AuthorityFrame = ExecutionAuthorityFrameBuilder.Build(new ExecutionAuthorityFrameBuilderInput
            {
                Source = "test",
                Instrument = "MGC",
                SubmitPath = "MISMATCH_RELEASE",
                DecisionUtc = utc,
                FrameCreatedUtc = utc,
                SnapshotError = "snapshot_unavailable",
                AuthorityState = "MISMATCH_RELEASE"
            })
        });
        if (snapshotMissing.Allowed || snapshotMissing.DenyReason != "SNAPSHOT_INSUFFICIENT")
            return "expected mismatch release to deny when snapshot is insufficient";

        return null;
    }

    private static string? Case_UeaShutdownSafeVerdictRequiresCleanFlat()
    {
        var utc = DateTimeOffset.UtcNow;
        var cleanFrame = ExecutionAuthorityFrameBuilder.Build(new ExecutionAuthorityFrameBuilderInput
        {
            Source = "test",
            Instrument = "__RUN__",
            SubmitPath = "SHUTDOWN_SAFE_VERDICT",
            DecisionUtc = utc,
            FrameCreatedUtc = utc,
            AuthorityState = "SHUTDOWN_SAFE_VERDICT"
        });
        var clean = UnifiedExecutionAuthority.EvaluateAction(new ExecutionAuthorityActionEvaluationRequest
        {
            Action = ExecutionAuthorityAction.ShutdownSafeVerdict,
            Source = "test",
            Instrument = "__RUN__",
            UtcNow = utc,
            SnapshotSufficient = true,
            AuthorityFrame = cleanFrame
        });
        if (!clean.Allowed)
            return "expected shutdown safe verdict to allow only when clean-flat predicates pass";

        var journalOpenFrame = ExecutionAuthorityFrameBuilder.Build(new ExecutionAuthorityFrameBuilderInput
        {
            Source = "test",
            Instrument = "__RUN__",
            SubmitPath = "SHUTDOWN_SAFE_VERDICT",
            DecisionUtc = utc,
            FrameCreatedUtc = utc,
            JournalOpenQty = 2,
            AuthorityState = "SHUTDOWN_SAFE_VERDICT"
        });
        var journalOpen = UnifiedExecutionAuthority.EvaluateAction(new ExecutionAuthorityActionEvaluationRequest
        {
            Action = ExecutionAuthorityAction.ShutdownSafeVerdict,
            Source = "test",
            Instrument = "__RUN__",
            UtcNow = utc,
            SnapshotSufficient = true,
            JournalOpenQty = 2,
            AuthorityFrame = journalOpenFrame
        });
        if (journalOpen.Allowed || journalOpen.DenyReason != "JOURNAL_OPEN_QTY")
            return "expected shutdown safe verdict to deny open journal quantity";

        var workingFrame = ExecutionAuthorityFrameBuilder.Build(new ExecutionAuthorityFrameBuilderInput
        {
            Source = "test",
            Instrument = "__RUN__",
            SubmitPath = "SHUTDOWN_SAFE_VERDICT",
            DecisionUtc = utc,
            FrameCreatedUtc = utc,
            BrokerWorkingOrdersCount = 1,
            IeaMismatchTrustedWorkingCount = 1,
            UseInstrumentExecutionAuthority = true,
            AuthorityState = "SHUTDOWN_SAFE_VERDICT"
        });
        var working = UnifiedExecutionAuthority.EvaluateAction(new ExecutionAuthorityActionEvaluationRequest
        {
            Action = ExecutionAuthorityAction.ShutdownSafeVerdict,
            Source = "test",
            Instrument = "__RUN__",
            UtcNow = utc,
            SnapshotSufficient = true,
            BrokerWorkingOrderCount = 1,
            AuthorityFrame = workingFrame
        });
        if (working.Allowed || working.DenyReason != "WORKING_ORDERS_OPEN")
            return "expected shutdown safe verdict to deny broker working orders";

        var activeIntentFrame = ExecutionAuthorityFrameBuilder.Build(new ExecutionAuthorityFrameBuilderInput
        {
            Source = "test",
            Instrument = "__RUN__",
            SubmitPath = "SHUTDOWN_SAFE_VERDICT",
            DecisionUtc = utc,
            FrameCreatedUtc = utc,
            ActiveIntentsCount = 1,
            AuthorityState = "SHUTDOWN_SAFE_VERDICT"
        });
        var activeIntent = UnifiedExecutionAuthority.EvaluateAction(new ExecutionAuthorityActionEvaluationRequest
        {
            Action = ExecutionAuthorityAction.ShutdownSafeVerdict,
            Source = "test",
            Instrument = "__RUN__",
            UtcNow = utc,
            SnapshotSufficient = true,
            ActiveIntentCount = 1,
            AuthorityFrame = activeIntentFrame
        });
        if (activeIntent.Allowed || activeIntent.DenyReason != "ACTIVE_INTENTS_OPEN")
            return "expected shutdown safe verdict to deny active submitted intents";

        var nonTerminalStreamFrame = ExecutionAuthorityFrameBuilder.Build(new ExecutionAuthorityFrameBuilderInput
        {
            Source = "test",
            Instrument = "__RUN__",
            SubmitPath = "SHUTDOWN_SAFE_VERDICT",
            DecisionUtc = utc,
            FrameCreatedUtc = utc,
            NonTerminalStreamsCount = 1,
            AuthorityState = "SHUTDOWN_SAFE_VERDICT"
        });
        var nonTerminalStream = UnifiedExecutionAuthority.EvaluateAction(new ExecutionAuthorityActionEvaluationRequest
        {
            Action = ExecutionAuthorityAction.ShutdownSafeVerdict,
            Source = "test",
            Instrument = "__RUN__",
            UtcNow = utc,
            SnapshotSufficient = true,
            NonTerminalStreamCount = 1,
            AuthorityFrame = nonTerminalStreamFrame
        });
        if (nonTerminalStream.Allowed || nonTerminalStream.DenyReason != "NONTERMINAL_STREAMS_OPEN")
            return "expected shutdown safe verdict to deny nonterminal streams";

        var contradictoryFrame = ExecutionAuthorityFrameBuilder.Build(new ExecutionAuthorityFrameBuilderInput
        {
            Source = "test",
            Instrument = "__RUN__",
            SubmitPath = "SHUTDOWN_SAFE_VERDICT",
            DecisionUtc = utc,
            FrameCreatedUtc = utc,
            BrokerPositionQty = 0,
            JournalOpenQty = 1,
            IeaMismatchTrustedWorkingCount = 1,
            AuthorityState = "SHUTDOWN_SAFE_VERDICT"
        });
        var contradictory = UnifiedExecutionAuthority.EvaluateAction(new ExecutionAuthorityActionEvaluationRequest
        {
            Action = ExecutionAuthorityAction.ShutdownSafeVerdict,
            Source = "test",
            Instrument = "__RUN__",
            UtcNow = utc,
            SnapshotSufficient = true,
            AuthorityFrame = contradictoryFrame
        });
        if (contradictory.Allowed || contradictory.DenyReason != "AUTHORITY_FRAME_CONTRADICTION")
            return "expected shutdown safe verdict to deny authority-frame contradiction";

        return null;
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
