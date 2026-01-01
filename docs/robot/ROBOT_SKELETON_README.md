## Robot Skeleton (Step-2) — wiring proof, no trading

This is a **non-trading** skeleton implementation intended to prove:
- timetable ingestion + validation
- Chicago ↔ UTC conversion (DST-aware)
- per-stream state machines (time-driven)
- timetable reactivity (poll + hash)
- restart journaling (per stream/day)
- deterministic JSONL logging

### Hard constraint

**The skeleton never submits orders.**

### Inputs (project-root paths)

- `configs/analyzer_robot_parity.json`
- `data/timetable/timetable_current.json`

### Outputs

- Logs: `logs/robot/robot_skeleton.jsonl`
- Journal: `logs/robot/journal/<trading_date>_<stream>.json`

### Run the non-NinjaTrader harness

From repo root:

```bash
dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --write-sample-timetable
dotnet run --project modules/robot/harness/Robot.Harness.csproj
```

### Notes

- If `timetable_current.json` is invalid/stale, the engine logs `TIMETABLE_INVALID` and stands down (fail closed).
- `slot_time` is validated against `sessions.<S1|S2>.slot_end_times` in the parity spec; invalid streams are skipped (fail closed per stream).

