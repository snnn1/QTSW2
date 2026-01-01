# Analyzer ↔ NinjaTrader Robot Parity Table (Execution Semantics)

This file is the **single source of truth** for Analyzer ↔ NinjaTrader Robot parity on *execution semantics*.

It is **governance**, not engineering:
- **Any Analyzer execution-logic change** MUST update this file (and `configs/analyzer_robot_parity.json`).
- **Any Robot execution-logic change** MUST be justified by this file (or by an Analyzer change first).

## Scope

In scope:
- Range building window definition (start/end, lock behavior)
- Breakout trigger levels (including tick rounding)
- Entry semantics and entry cutoff
- Stop / target / break-even semantics

Out of scope:
- Matrix / timetable selection logic
- Performance stats / UI-only calculations

## Stream mapping (Analyzer truth)

Analyzer runs **per instrument** and assigns a stream tag by session:
- **S1 stream**: `<INSTRUMENT>1`
- **S2 stream**: `<INSTRUMENT>2`

Source: `modules/analyzer/logic/instrument_logic.py` (`get_stream_tag`).

## Shared execution rules (Analyzer truth)

- **Timezone**: Slot times are interpreted in **America/Chicago**.
- **Range window**: \([range_start_time, slot_time)\) — range locks at `slot_time`.
- **Breakout trigger levels** (before entry detection):
  - \(brk\_long = round\_to\_tick(range\_high + tick\_size)\)
  - \(brk\_short = round\_to\_tick(range\_low - tick\_size)\)
  - Tick rounding MUST match `UtilityManager.round_to_tick` exactly (do not substitute an “equivalent” rounding method).
- **Entry semantics**:
  - Immediate if range “freeze close” is already beyond trigger:
    - Long if `freeze_close >= brk_long`
    - Short if `freeze_close <= brk_short`
  - Otherwise, first breakout wins:
    - Long breakout if any bar satisfies `high >= brk_long`
    - Short breakout if any bar satisfies `low <= brk_short`
  - Equality is allowed (>= / <=).
- **Entry cutoff (market close)**:
  - Default: **16:00 CT**
  - Breakouts after market close are ignored (treated as NoTrade).
- **Timetable slot_time validation (fail closed)**:
  - Robot must validate `slot_time` is in the allowed list for that session (see `slot_end_times`).
  - If invalid → skip that stream/day (no orders, no risk).
- **Target rule**:
  - `target_pts = base_target(instrument)` (first value in the instrument’s ladder)
  - Target price = entry ± target_pts.
- **Initial stop rule**:
  - Stop distance (points) = `min(range_size, 3 * target_pts)`
  - Stop price = entry ∓ stop_distance.
- **Break-even rule**:
  - Trigger at **65%** of target: `t1_threshold = target_pts * 0.65`
  - On trigger, stop moves to entry ± 1 tick:
    - Long: `entry - tick_size`
    - Short: `entry + tick_size`
  - Trigger is one-way (a single transition to “triggered”).

## Parity table (one row per stream)

Notes:
- Most columns are shared; the only per-stream differences are **instrument config** and **session range start**.
- “Range End Time” is always **slot_time** from the timetable directive.

| Stream | Instrument | Tick Size | Range Start (CT) | Range End (CT) | Breakout Trigger Rule | Tick Rounding | Entry Type | Stop Rule | Target Rule | BE Trigger % | BE Offset | Entry Cutoff (CT) | Sources |
|---|---|---:|---|---|---|---|---|---|---|---:|---|---|---|
| ES1 | ES | 0.25 | 02:00 | slot_time | `round_to_tick(range_high + tick_size)` / `round_to_tick(range_low - tick_size)` | must match Analyzer `round_to_tick` exactly | trigger on trade-through (>= / <=), immediate if beyond at lock | `min(range_size, 3×target_pts)` | `base_target(ES)` | 65% | ±1 tick | 16:00 | `instrument_logic.py`, `config_logic.py`, `range_logic.py`, `engine.py`, `utility_logic.py`, `entry_logic.py`, `loss_logic.py`, `price_tracking_logic.py` |
| ES2 | ES | 0.25 | 08:00 | slot_time | same | same | same | same | same | 65% | ±1 tick | 16:00 | same |
| CL1 | CL | 0.01 | 02:00 | slot_time | same | same | same | same | `base_target(CL)` | 65% | ±1 tick | 16:00 | same |
| CL2 | CL | 0.01 | 08:00 | slot_time | same | same | same | same | `base_target(CL)` | 65% | ±1 tick | 16:00 | same |
| RTY1 | RTY | 0.10 | 02:00 | slot_time | same | same | same | same | `base_target(RTY)` | 65% | ±1 tick | 16:00 | same |
| RTY2 | RTY | 0.10 | 08:00 | slot_time | same | same | same | same | `base_target(RTY)` | 65% | ±1 tick | 16:00 | same |
| M2K1 | M2K | 0.10 | 02:00 | slot_time | same | same | same | same | `base_target(M2K)` | 65% | ±1 tick | 16:00 | same |
| M2K2 | M2K | 0.10 | 08:00 | slot_time | same | same | same | same | `base_target(M2K)` | 65% | ±1 tick | 16:00 | same |

## Non-trading symbols (Analyzer universe, but not Robot-supported)

- `MINUTEDATAEXPORT`: helper/export symbol used by the Analyzer; not intended for live trading by the Robot.

## Source anchors (exact implementations)

- **Instrument tick sizes + ladders**: `modules/analyzer/logic/instrument_logic.py`
- **Session starts + market close**: `modules/analyzer/logic/config_logic.py`
- **Range window filtering**: `modules/analyzer/logic/range_logic.py`
- **Breakout level rounding**: `modules/analyzer/breakout_core/engine.py` + `modules/analyzer/logic/utility_logic.py`
- **Entry detection + market close cutoff**: `modules/analyzer/logic/entry_logic.py`
- **Initial stop formula**: `modules/analyzer/logic/loss_logic.py`
- **BE trigger and BE stop move**: `modules/analyzer/logic/price_tracking_logic.py`

