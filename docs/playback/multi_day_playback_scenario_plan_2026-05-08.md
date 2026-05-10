# QTSW2 Multi-Day Playback Scenario Plan

Date: 2026-05-08

Purpose:
- Enable explicit multi-day NinjaTrader Playback tests without changing normal SIM/live scheduling.
- Prove carryover stream behavior with event-clock day switching.
- Keep proof classification honest: this is playback proof only, not SIM/runtime proof.

## Contract

Normal Playback-account run:
- Uses isolated `runs/<run_id>` persistence.
- Ignores existing stream journals.
- Remains single-day proof only.

Explicit multi-day playback scenario:
- Enabled only when `QTSW2_PLAYBACK_SCENARIO` points to a generated `playback_scenario.json`.
- Uses isolated `runs/<scenario_run_id>` persistence.
- Does not bypass stream journals.
- Selects per-day replay timetables from the scenario manifest using CME event-clock session date.
- On day rollover, keeps only streams with active lifecycle evidence.
- Allows new-day streams from the new timetable after a one-tick rollover guard.

## Build Scenario

Example:

```powershell
powershell -ExecutionPolicy Bypass -File tools\playback\build_multi_day_scenario.ps1 -Start 2026-05-05 -End 2026-05-08 -RunId playback_week_20260505
```

The tool writes:
- `runs/<run_id>/playback_scenario/playback_scenario.json`
- `runs/<run_id>/playback_scenario/timetables/timetable_YYYY-MM-DD.json`

Before launching NinjaTrader Playback:

```powershell
$env:QTSW2_PLAYBACK_SCENARIO = "C:\Users\jakej\QTSW2\runs\<run_id>\playback_scenario\playback_scenario.json"
```

Unset it after the scenario run:

```powershell
Remove-Item Env:\QTSW2_PLAYBACK_SCENARIO
```

## Pass Criteria

Playback scenario pass requires:
- `PLAYBACK_SCENARIO_TIMETABLE_SELECTED` at startup and every scenario day rollover.
- `TRADING_DATE_ROLLED_FORWARD` with `playback_scenario=true`.
- Carryover stream journals remain open when active exposure/lifecycle exists.
- New-day streams are created from the next replay timetable.
- No `PLAYBACK_SCENARIO_*` CRITICAL.
- No OCO reuse, NT thread violation, execution callback stall, protective under-coverage, crash/freeze, or open exposure at intended final shutdown.

## Proof Status

Current implementation status:
- `PlaybackScenarioManifest`: source/build/harness-proven.
- Event-clock scenario timetable selection: source/build/harness-proven.
- Scenario carryover stream journal preservation: source/build/harness-proven.
- Normal one-day playback journal bypass unchanged: harness-proven.

Not proven yet:
- Multi-day Playback run through NinjaTrader: not playback-proven.
- SIM/live multi-day carryover scheduling: not SIM/runtime-proven.
- Prop evaluation readiness from this feature: not deployed-live-proven.
