# QTSW2 Authority Feature Flag Matrix

Audit date: 2026-04-30

Purpose:
- Document production defaults, isolated playback overrides, and cleanup readiness for execution authority flags.
- Prevent accidental deletion of rollback/fail-closed paths before runtime proof exists.

Evidence sources:
- `system/modules/robot/core/Execution/FeatureFlags.cs`
- `system/RobotCore_For_NinjaTrader/Execution/FeatureFlags.cs`
- `system/modules/robot/core/RobotEngine.cs` `ApplyIsolatedPlaybackAuditShadowDefaults`
- `system/RobotCore_For_NinjaTrader/RobotEngine.cs` `ApplyIsolatedPlaybackAuditShadowDefaults`
- Runtime anchor in cleanup master audit: `runs/2e1edfe08dc14685ac10bbf138e6222a`

## Runtime Override Summary

When isolated playback persistence is active, `RobotEngine.ApplyIsolatedPlaybackAuditShadowDefaults` force-enables:
- `CanonicalOwnershipLedgerEnabled=true`
- `UnifiedExecutionAuthorityShadowEnabled=true`
- `UnifiedExecutionAuthorityEnabled=true`
- `StructuralLayerUseLedgerOwnership=true`

It only enables `ReconciliationRepairExecutorEnabled` when it was already enabled or when `PlaybackAuditAutoEnableReconciliationRepairExecutor=true`.

This means playback/audit runs are exercising the newer authority path, while default static flag values still preserve rollback behavior.

## Flag Matrix

| Flag | Default | Isolated playback override | Domain | Cleanup classification | Evidence |
|---|---:|---:|---|---|---|
| `EnablePositionAuthorityEnforcement` | `false` | no override found | journal/position authority | KEEP | `FeatureFlags.cs` default; no runtime assignment outside tests found. |
| `EnableHardFailClosedJournalIntegrity` | `true` | no override found | fail-closed journal integrity | KEEP | Safety-critical default in `FeatureFlags.cs`; tests temporarily disable. |
| `EnableHardFailClosedBrokerFlatten` | `true` | no override found | fail-closed broker flatten | KEEP | Safety-critical default in `FeatureFlags.cs`; no runtime assignment found. |
| `EnablePostFillAlignmentGate` | `true` | no override found | post-fill alignment | KEEP | Active lag-control default; tests toggle. |
| `PostFillAlignmentWindowMs` | `5000` | no override found | post-fill alignment | KEEP | Active timing parameter; tests tune. |
| `ControlPlaneParityHardFlattenFromTryEnsureJournalIntegrity` | `false` | no override found | parity-driven hard flatten | KEEP | Rollback/activation gate; tests enable explicitly. |
| `FailClosedStrictReleaseConfirmationEnabled` | `true` | no override found | fail-closed recovery release | KEEP | Safety-critical release confirmation default. |
| `StructuralLayerAllowSubmitDuringPendingAlignmentLag` | `true` | no override found | structural submit lag handling | KEEP | Active submit behavior; tests toggle. |
| `QuantExecutionControlStoreEnabled` | `true` | no override found | quant execution control | KEEP | Active state-control default; tests toggle. |
| `AggregatedProtectiveSingleWaveEnabled` | `true` | no override found | protective coverage | KEEP | Active protective behavior default. |
| `QuantEscalationRecoveryStabilizationWindowMs` | `60000` | no override found | quant recovery escalation | KEEP | Active timing parameter; no runtime assignment found. |
| `StructuralLayerPhase3DemoteParityNotOkSubmitDeny` | `false` | no override found | structural submit demotion | KEEP | Rollout gate; tests cover true/false. |
| `StructuralLayerPhase3DemoteRepairActiveSubmitDeny` | `false` | no override found | structural submit demotion | KEEP | Rollout gate; tests cover true/false. |
| `CanonicalOwnershipLedgerEnabled` | `false` | forced `true` | ownership authority | CONDITIONAL_REMOVE_LATER | Runtime playback uses it, but default fallback still exists. |
| `TransientMismatchWindowMs` | `5000` | no override found | mismatch classification | KEEP | Active timing parameter; no runtime assignment found. |
| `UnifiedExecutionAuthorityShadowEnabled` | `false` | forced `true` | submit authority shadow | CONDITIONAL_REMOVE_LATER | Runtime playback forces shadow path; default keeps rollback. |
| `UnifiedExecutionAuthorityEnabled` | `false` | forced `true` | submit authority active path | CONDITIONAL_REMOVE_LATER | Runtime playback uses UEA; default keeps old gate chain rollback. |
| `ReconciliationRepairExecutorEnabled` | `false` | conditional only | reconciliation repair | KEEP | Explicitly not forced unless `PlaybackAuditAutoEnableReconciliationRepairExecutor` is enabled. |
| `PlaybackAuditAutoEnableReconciliationRepairExecutor` | `false` | controls repair override | playback audit repair | KEEP | Guard prevents repair mutations during ordinary playback audit. |
| `StructuralLayerUseLedgerOwnership` | `false` | forced `true` | structural ownership source | CONDITIONAL_REMOVE_LATER | Runtime playback uses ledger ownership; default keeps journal fallback. |

## Cleanup Verdict

No authority/fallback flag is safe to delete now.

Reason:
- Latest validated runtime evidence is promising, including proof-hardening playback on DLL hash `7D82321EE84875988D141042...`, but still not enough to delete fail-closed or rollback paths.
- Playback/audit mode force-enables the newer authority path, while default values intentionally preserve rollback.
- The cleanup master audit still requires multiple clean playback runs before fallback removal.

Safe next step:
- Keep the flags.
- Keep logging `PLAYBACK_AUDIT_ACTIVE_FLAGS_APPLIED`.
- Use `docs/audits/mismatch_authority_next_level_map_2026-05-01.md` as the next cleanup map.
- Use fresh playback runs to prove whether `CanonicalOwnershipLedgerEnabled`, `UnifiedExecutionAuthorityEnabled`, and `StructuralLayerUseLedgerOwnership` can become hard production defaults later.
