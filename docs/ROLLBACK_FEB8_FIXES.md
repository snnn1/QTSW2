# Rollback to Feb 8 – Fixes Applied

After rolling back to commit 65c8d13 (Feb 8), several components needed fixes. This document summarizes what was fixed.

## Fixes Applied

### 1. Master Matrix – Frontend Path
**Issue:** Batch files used `%PROJECT_ROOT%\matrix_timetable_app\frontend` but the actual path is `modules\matrix_timetable_app\frontend`.

**Fixed files:**
- `batch/RUN_MASTER_MATRIX.bat`
- `batch/RUN_MASTER_MATRIX_VISIBLE.bat`

**Note:** `modules/matrix/batch/RUN_MASTER_MATRIX.bat` already had the correct path.

### 2. Master Matrix – Vite / node_modules
**Issue:** Corrupted or incomplete `node_modules` caused `ERR_MODULE_NOT_FOUND` for Vite CLI.

**Fix:** Run `npm install` in `modules/matrix_timetable_app/frontend/`:
```powershell
cd modules\matrix_timetable_app\frontend
npm install
```

### 3. Robot – Timetable File Missing
**Issue:** `data/timetable/timetable_current.json` did not exist. Robot engines failed with `TIMETABLE_INVALID`, `error = MISSING`, and stood down.

**Fix:**
- Created `data/timetable/` directory
- Created minimal `timetable_current.json` (placeholder with empty streams)

**Next step:** Generate a full timetable from the Master Matrix UI (Timetable tab → Generate or Save).

### 4. Watchdog – Unicode Encoding
**Issue:** Checkmark character (✓) in print statements caused `'charmap' codec can't encode character '\u2713'` on Windows.

**Fixed file:** `modules/watchdog/backend/main.py` – replaced ✓ with `[OK]`.

### 5. Dashboard – Timetable Save Path
**Issue:** `save_execution_timetable` used relative `Path("data/timetable")`, which could fail if the server was started from a different directory.

**Fix:** `modules/dashboard/backend/main.py` – use `QTSW2_ROOT / "data" / "timetable"` instead.

### 6. Robot Build (from earlier rollback)
**Issue:** Feb 8 codebase had build errors:
- `RobotSimStrategy.cs` references NinjaTrader `Strategy` – excluded from Robot.Core build
- `TimetableContract` lacked `LoadFromBytes()` – added for `TimetableCache`

**Fixed files:**
- `RobotCore_For_NinjaTrader/Robot.Core.csproj` – exclude `Strategies/RobotSimStrategy.cs`
- `RobotCore_For_NinjaTrader/Models.TimetableContract.cs` – add `LoadFromBytes()`

## Verification

- **Dashboard backend:** `python -c "from modules.dashboard.backend.main import app; print('OK')"` ✓
- **Watchdog backend:** `python -c "from modules.watchdog.backend.main import app; print('OK')"` ✓
- **Robot.Core.dll:** Builds successfully
- **Timetable file:** Exists at `data/timetable/timetable_current.json`

## Running the System

1. **Master Matrix:** Run `batch\RUN_MASTER_MATRIX.bat` or `batch\RUN_MASTER_MATRIX_VISIBLE.bat`
2. **Watchdog:** Run `batch\START_WATCHDOG.bat` or `batch\START_WATCHDOG_FULL.bat`
3. **Robot:** Deploy DLL + strategy to NinjaTrader, ensure timetable is generated from Matrix first

## Timetable Generation

The placeholder timetable has empty streams. To enable trading:

1. Start Master Matrix
2. Open Timetable tab
3. Generate or configure streams
4. Save execution timetable

This writes `data/timetable/timetable_current.json` with enabled streams. The Robot reads this file at startup.
