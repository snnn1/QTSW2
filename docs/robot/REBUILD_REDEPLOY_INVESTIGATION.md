# Rebuild & Redeploy Investigation

**Date:** 2026-03-13  
**Status:** Pipeline verified working. No critical failures found.

---

## Executive Summary

The rebuild and redeploy pipeline is **functioning correctly**. Build succeeds, deploy copies to the correct NinjaTrader path, and NinjaTrader loads Robot.Core.dll and RobotSimStrategy successfully. Recent logs show "Engine ready - all initialization complete" with no errors.

---

## 1. Build

| Check | Result |
|-------|--------|
| Command | `dotnet build RobotCore_For_NinjaTrader\Robot.Core.csproj -c Release` |
| Exit code | 0 (success) |
| Output | `Robot.Core.dll` (1,655,296 bytes) in `RobotCore_For_NinjaTrader\bin\Release\net48\` |
| Source | `RobotCore_For_NinjaTrader\` + `modules\robot\contracts\` (commit 22d2c0f) |

**Warnings (non-blocking):**
- NU1903: System.Text.Json 8.0.0 has known vulnerabilities
- MSB3277: Assembly version conflicts (System.Text.Json 8.0 vs 9.0, System.Threading.Tasks.Extensions, System.Buffers). MSBuild resolves by choosing primary; NinjaTrader loads its own versions at runtime.

---

## 2. Deploy

| Check | Result |
|-------|--------|
| Script | `batch\DEPLOY_ROBOT_TO_NINJATRADER.bat` |
| Target | `%USERPROFILE%\OneDrive\Documents\NinjaTrader 8\bin\Custom` |
| MyDocuments | Resolves to `C:\Users\jakej\OneDrive\Documents` ✓ |
| Robot.Core.dll | Hash match: build output = deployed |
| RobotSimStrategy.cs | Hash match: source = deployed |
| Robot.Contracts.dll | Copied ✓ |
| Dependencies | System.Text.Json, System.Buffers, etc. copied ✓ |

**Note:** Deploy copies to OneDrive only. `Documents\NinjaTrader 8` and `OneDrive\Documents\NinjaTrader 8` both exist; MyDocuments points to OneDrive, so NinjaTrader uses the correct path.

---

## 3. NinjaTrader Load

| Check | Result |
|-------|--------|
| Config reference | `*MyDocuments*\NinjaTrader 8\bin\Custom\Robot.Core.dll` ✓ |
| Vendor assemblies | Robot.Core, Robot.Contracts loaded (log.20260313.00005/00006) |
| Strategy init | "Engine ready - all initialization complete. Instrument=MES, EngineReady=True, InitFailed=False" |
| Errors | None in recent logs |

---

## 4. Potential Failure Points (and mitigations)

| Risk | Mitigation |
|------|------------|
| **NinjaTrader running during deploy** | DLL may be locked. Copy can fail or old DLL stays in memory. **Close NinjaTrader before deploy, then restart after.** |
| **No restart after deploy** | .NET loads DLLs once. Copying a new DLL does not update the in-memory copy. **Restart NinjaTrader after every deploy.** |
| **Tools → Compile not run** | RobotSimStrategy.cs is compiled by NinjaTrader. If you change the strategy source, run **Tools → Compile** in NinjaScript Editor. Robot.Core.dll is a reference and does not require recompile unless the strategy's usage changes. |
| **Assembly version conflicts** | Build uses System.Text.Json 8.0; NinjaTrader loads 9.0.0.8 from its bin. No runtime errors observed. |
| **OneDrive sync** | Possible sync delay or conflict. Verify file timestamp/size after deploy. |

---

## 5. Verified Post-Deploy Checklist

1. ✓ Build succeeds
2. ✓ Robot.Core.dll copied to `OneDrive\Documents\NinjaTrader 8\bin\Custom\`
3. ✓ RobotSimStrategy.cs copied to `...\Custom\Strategies\`
4. ✓ NinjaTrader Config.xml references Robot.Core.dll
5. ✓ NinjaTrader loads Robot.Core (see log "Vendor assembly 'Robot.Core' version='1.0.0.0' loaded")
6. ✓ Strategy initializes ("Engine ready - all initialization complete")

---

## 6. Recommended Workflow

1. **Close NinjaTrader**
2. Run `batch\DEPLOY_ROBOT_TO_NINJATRADER.bat`
3. **Restart NinjaTrader**
4. If you changed RobotSimStrategy.cs: open NinjaScript Editor → **Tools → Compile**
5. Start strategy and verify in Control Center → Log

---

## 7. If Strategy Still Fails

If the strategy shows "connected" but does not receive ticks, the issue is likely **not** rebuild/deploy. Check:

- **Data feed:** Live vs Simulation connection, instrument subscription
- **BarsRequest:** Log shows "BarsRequest returned 0 bars" when starting late (after slot end) — expected; live bars only
- **Timetable:** `data\timetable\timetable_current.json` and stream configuration
- **Robot logs:** `logs\robot\robot_*.jsonl` for engine/connection events
