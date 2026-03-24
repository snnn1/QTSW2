# ENGINE_CPU_PROFILE — find what burns CPU in NinjaTrader (robot)

## Enable

1. Create an **empty file** (zero bytes is fine):

   `{QTSW2 project root}/data/engine_cpu_profile.enabled`

   Example: `c:\Users\jakej\QTSW2\data\engine_cpu_profile.enabled`

2. **Restart** NinjaTrader (or reload strategies) so `Robot.Core.dll` picks up the flag.

3. Remove the file to disable. The flag is re-checked about every **5 seconds** (no restart needed to turn off after delete).

## What you get

### `ENGINE_CPU_PROFILE` (robot ENGINE log, ~every 10s per strategy when Realtime tick runs)

Wall-clock **milliseconds** spent inside the main engine **lock** during `TickInternal`:

| Field | Meaning |
|--------|---------|
| `pre_lock_ms` | Identity check, timetable poll + parse (outside lock wait). |
| `pre_reconciliation_ms` | Heartbeat, guards, disconnect/recovery transition **before** periodic reconciliation. |
| `reconciliation_runner_ms` | `RunReconciliationPeriodicThrottle` (can include snapshot work when it runs). |
| `timetable_reload_ms` | `ReloadTimetableIfChanged` when a poll happened. |
| `stream_tick_ms` | `StreamStateMachine.Tick` for all streams. |
| `second_reconciliation_ms` | One-shot second reconciliation block (if it runs). |
| `forced_flatten_ms` | Session close / forced flatten logic. |
| `tail_coordinators_ms` | Stream summary, gap summary, health monitor, protective + mismatch metric emits. |
| `lock_sum_ms` | Sum of the slices above (approximates work attributed in the tick). |
| `onbar_lock_ms_window` / `onbar_calls_window` / `onbar_avg_lock_ms` | Time spent inside **`OnBar`** under the same lock **since the last profile emit** (bars × instruments dominate if this is large). |

**How to read it:** whichever field is largest **on average** over several lines is your primary robot-side hotspot for that chart. If `onbar_avg_lock_ms` is high with many charts, **bar frequency × stream work** is likely the driver. If `reconciliation_runner_ms` or `tail_coordinators_ms` spikes, dig there next.

### `ADOPTION_SCAN_SUMMARY` — `scan_wall_ms`

When profiling is enabled, **milliseconds** for the full **`ScanAndAdoptExistingOrders`** pass (per IEA). If this is large while CPU is high, adoption + account order iteration is still hot (see also recovery throttling).

## Outside the robot DLL

NinjaTrader itself (charts, indicators, market data) is **not** measured here. If Task Manager shows high **NinjaTrader.exe** but `ENGINE_CPU_PROFILE` and `scan_wall_ms` stay low, use **NinjaTrader’s own profiling** or Windows **Performance Recorder** / **PerfView** on the NT process.
