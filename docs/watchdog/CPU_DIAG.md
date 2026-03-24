# Watchdog CPU diagnostics (`WATCHDOG_CPU_DIAG`)

When overall machine CPU is high (e.g. ~80%) and you suspect the **Python watchdog** or **heavy robot JSONL + feed tail** work, enable structured timing logs.

## Enable

Set before starting the watchdog backend (PowerShell example):

```powershell
$env:WATCHDOG_CPU_DIAG = "1"
$env:WATCHDOG_CPU_DIAG_INTERVAL = "5"   # seconds between lines; default 5
# then start watchdog (your usual command)
```

Restart the watchdog after changing env vars (values are read at import).

## What gets logged

Every `WATCHDOG_CPU_DIAG_INTERVAL` seconds, one **INFO** line:

`WATCHDOG_CPU_DIAG: { ... json ... }`

Payload includes:

| Field | Meaning |
|--------|---------|
| `phase_ms_raw_robot_logs` | Wall time in the worker thread to run `EventFeedGenerator.process_new_events()` (read new bytes from `logs/robot/robot_*.jsonl`, merge, filter, append to `frontend_feed.jsonl`). |
| `phase_ms_feed_tail_ingest` | Wall time to tail-read `TAIL_LINE_COUNT` lines from `frontend_feed.jsonl`, JSON-parse, and run `process_event` for new cursor events. |
| `event_feed_last_cycle` | Last raw-log phase: `raw_events_read`, `events_written_to_feed`, `duration_ms`, and (when diag on) `order_related_raw_events`, `engine_tick_diagnostic_raw_events`, `top_raw_event_types`. |
| `feed_tail_last_cycle` | Last tail phase: `tail_line_count`, `parsed_event_count`, `cursor_events_built`, `process_event_calls`, `ingest_degraded`, optional `tail_top_event_types`, `tail_order_related_count`. |
| `frontend_feed_size_mb` | Current `frontend_feed.jsonl` size. |
| `watchdog_process_cpu_percent` | Sampled via `psutil` (~50ms sample). |
| `watchdog_rss_mb` | Resident set size of the watchdog process. |

## Interpreting

- **High `phase_ms_raw_robot_logs`** + large `order_related_raw_events` or huge `raw_events_read` → robot is emitting many live-critical lines; consider reducing **per-tick** `ENGINE_TICK_*` diagnostics in C# if enabled.
- **High `phase_ms_feed_tail_ingest`** + large `process_event_calls` or `tail_order_related_count` → tail replay/processing is hot; correlate with `WATCHDOG_INGESTION_STATS` / degraded mode.
- If these phases are **low** but machine CPU is still **high**, the bottleneck is likely **NinjaTrader / strategy** (not the watchdog) — use NT profiling or Performance Monitor.

## Related

- Existing **`WATCHDOG_INGESTION_STATS`** (every ~10s) remains the rolling average view.
- `ENGINE_TICK_CALLSITE` batch writes to the feed are logged at **DEBUG** only (reduces log I/O noise).
