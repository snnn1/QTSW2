# Robot Configuration Knobs (Section 16)

This document provides the **five configuration knobs** required by Section 16 of the NinjaTrader Robot Blueprint, extracted directly from the Analyzer implementation to ensure exact parity.

## Source Files
- **Primary Source**: `modules/analyzer/logic/config_logic.py`
- **Parity Reference**: `configs/analyzer_robot_parity.json`

---

## 1. S1 Range Start Time (Chicago)

**Value**: `"02:00"`

**Source**: `ConfigManager.slot_start["S1"]` (line 31)

**Usage**: Defines when the S1 (overnight) session range calculation begins. Range window is `[02:00, slot_time)`.

---

## 2. S2 Range Start Time (Chicago)

**Value**: `"08:00"`

**Source**: `ConfigManager.slot_start["S2"]` (line 31)

**Usage**: Defines when the S2 (regular hours) session range calculation begins. Range window is `[08:00, slot_time)`.

---

## 3. Tick Size Per Instrument

**Values** (must match Analyzer exactly):

| Instrument | Tick Size | Source |
|------------|-----------|--------|
| ES         | 0.25      | `ConfigManager.tick_size["ES"]` |
| NQ         | 0.25      | `ConfigManager.tick_size["NQ"]` |
| YM         | 1.0       | `ConfigManager.tick_size["YM"]` |
| CL         | 0.01      | `ConfigManager.tick_size["CL"]` |
| NG         | 0.001     | `ConfigManager.tick_size["NG"]` |
| GC         | 0.1       | `ConfigManager.tick_size["GC"]` |
| RTY        | 0.10      | `ConfigManager.tick_size["RTY"]` |
| MES        | 0.25      | `ConfigManager.tick_size["MES"]` |
| MNQ        | 0.25      | `ConfigManager.tick_size["MNQ"]` |
| MYM        | 1.0       | `ConfigManager.tick_size["MYM"]` |
| MCL        | 0.01      | `ConfigManager.tick_size["MCL"]` |
| MNG        | 0.001     | `ConfigManager.tick_size["MNG"]` |
| MGC        | 0.1       | `ConfigManager.tick_size["MGC"]` |
| M2K        | 0.10      | `ConfigManager.tick_size["M2K"]` |

**Source**: `ConfigManager.tick_size` dictionary (lines 34-37)

**Usage**: 
- Breakout trigger levels: `brk_long = range_high + tick_size`, `brk_short = range_low - tick_size`
- All price calculations must use these exact tick sizes
- Tick rounding must match Analyzer's `UtilityManager.round_to_tick()` method

---

## 4. Market Close Time (Chicago)

**Value**: `"16:00"`

**Source**: `ConfigManager.market_close_time` (line 32)

**Usage**: 
- Entry cutoff: No new entries allowed if `current_time >= market_close_time`
- If no entry has filled by market close, stream is marked `NO_TRADE` and `DONE`
- Existing positions continue to be managed until stop/target or forced flatten

**CRITICAL**: This MUST match Analyzer configuration exactly. Entry cutoff is always market close time.

---

## 5. Forced Flatten Time Per Instrument/Session (Chicago)

**Status**: Not explicitly configured in Analyzer (Robot-specific execution constraint)

**Recommended Default**: `"15:55"` (5 minutes before market close)

**Rationale**: 
- Blueprint Section 10 states: "Behavior at flatten time (e.g., 5 minutes before close; configurable)"
- Market close is 16:00, so 15:55 provides a 5-minute buffer
- This is an execution constraint, not an Analyzer logic parameter

**Usage**:
- At flatten time, cancel all working orders and close all positions
- Log forced flatten action per stream impacted
- This is distinct from entry cutoff (entry cutoff prevents new entries; forced flatten closes existing positions)

**Configuration Note**: This should be configurable per instrument/session if needed, but a single default of 15:55 is likely sufficient for most cases.

---

## Implementation Checklist

- [ ] Configure S1 range start: `"02:00"` (Chicago)
- [ ] Configure S2 range start: `"08:00"` (Chicago)
- [ ] Configure tick sizes for all supported instruments (exact values from table above)
- [ ] Configure market close time: `"16:00"` (Chicago)
- [ ] Configure forced flatten time: `"15:55"` (Chicago) - or make configurable
- [ ] Verify all times are handled in America/Chicago timezone with DST-aware conversion
- [ ] Ensure tick rounding matches Analyzer's `UtilityManager.round_to_tick()` exactly

---

## Parity Verification

All values above match the Analyzer implementation in:
- `modules/analyzer/logic/config_logic.py`
- `configs/analyzer_robot_parity.json`

**NON-NEGOTIABLE INVARIANT**: Any deviation from these values requires an Analyzer change first, followed by updating this document and the Robot implementation.
