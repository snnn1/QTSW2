# Deploy Source of Truth — 2026-03-19

## Summary

**NinjaTrader uses pre-built Robot.Core.dll** — it does NOT compile from AddOns source. The deploy copies the DLL and strategy only.

---

## Correct Setup

- **Robot.Core.dll** — Built from RobotCore_For_NinjaTrader, copied to NinjaTrader Custom root
- **Robot.Contracts.dll** — Dependency, copied with DLLs
- **RobotSimStrategy.cs** — Strategy source, copied to Strategies folder
- **AddOns source** — Do NOT copy. NinjaTrader's project lacks references (Robot.Contracts, System.Text.Json) needed to compile it.
- **NinjaTrader user folder** — Keep active runtime files under local Documents, not OneDrive. Current expected path: `C:\Users\jakej\Documents\NinjaTrader 8`.

As of 2026-05-01, Windows `MyDocuments` is intentionally set to `C:\Users\jakej\Documents`. Deploy tools resolve NinjaTrader through the Windows Documents known-folder and no longer deploy to stale OneDrive mirrors just because they still exist.

---

## Workflow

`system/NT_ADDONS` is a legacy/manual mirror only. Do not sync or deploy it during the normal DLL-plus-strategy workflow.

If the legacy mirror is audited, use `tools/sync_nt_addons_from_robotcore.ps1 -CheckOnly`. A warning about source files missing from `NT_ADDONS` means the mirror is partial; it does not imply runtime deploy drift.

1. Edit RobotCore_For_NinjaTrader
2. Run `.\deploy_to_ninjatrader.ps1`
3. Restart NinjaTrader

---

## What Went Wrong (2026-03-19)

We added a step to copy AddOns source to NinjaTrader. NinjaTrader then tried to compile that source but its project doesn't reference Robot.Contracts.dll or System.Text.Json. Result: hundreds of CS0234 errors.

**Fix applied:** Removed AddOns copy from deploy. Deleted AddOns/RobotCore_For_NinjaTrader from NinjaTrader Custom. NinjaTrader now uses the pre-built Robot.Core.dll again.
