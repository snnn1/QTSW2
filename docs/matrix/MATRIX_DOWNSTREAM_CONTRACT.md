# Master Matrix Downstream Contract

**Purpose:** Schema contract for the master matrix as the authoritative downstream artifact.  
**Principle:** Downstream consumers (timetable, API, eligibility, reporting) must not re-read analyzer parquet for RS, SCF, or other derived metrics.

## Required Columns (Minimum Contract)

| Column | Type | Source | Used By |
|--------|------|--------|---------|
| Stream | object | Analyzer | All |
| trade_date | datetime64 | DataLoader | All |
| Time | object | Sequencer | Timetable, eligibility |
| selected_time | object | = Time | Timetable (alias) |
| final_allowed | bool | Filter engine | Timetable, eligibility |
| Result | object | Analyzer | Reporting, points |
| Session | object | Analyzer | Timetable |
| Instrument | object | Analyzer | API, reporting |
| scf_s1 | float64 | Analyzer (optional) | Timetable SCF filter |
| scf_s2 | float64 | Analyzer (optional) | Timetable SCF filter |
| points | float64 | Sequencer ({Time} Points) | Reporting |
| rs_value | float64 | Sequencer ({Time} Rolling) | Reporting, diagnostics |
| filter_reasons | object | Filter engine | Eligibility, diagnostics |

## Per-Time-Slot Columns (from Sequencer)

- `{time} Rolling` – rolling sum for each canonical time (e.g. "07:30 Rolling")
- `{time} Points` – points for each canonical time (e.g. "07:30 Points")

## Eligibility / State Fields

- day_of_month, dow, dow_full – for DOW/DOM filtering
- actual_trade_time – original analyzer time (for exclude_times filter)
- Time Change – next time slot (for time-change display)

## Naming Conventions

- Use current names; no versioned column names.
- `Time` = sequencer's selected slot (authoritative).
- `selected_time` = alias for Time (timetable compatibility).
