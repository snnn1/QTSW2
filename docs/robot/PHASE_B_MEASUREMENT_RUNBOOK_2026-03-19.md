# Phase B — Measurement Runbook

**Date:** 2026-03-19  
**Purpose:** Collect hard, comparable numbers before Phase C. Answer the decision gate.

---

## B1 — Snapshot Metrics (Instrumented)

**Status:** Implemented. Metrics emit automatically every 60 seconds.

**Event type:** `SNAPSHOT_METRICS`

**Fields:**
| Field | Description |
|-------|--------------|
| calls_per_sec | Snapshot calls per second (60s window) |
| p50_duration_ms | Median duration |
| p95_duration_ms | 95th percentile duration |
| p99_duration_ms | 99th percentile duration |
| max_burst_calls_in_50ms_window | Max concurrent calls in any 50ms window |
| sample_count | Number of calls in window |

**Where to find:** `logs/robot/*.jsonl` — grep for `SNAPSHOT_METRICS`

**Example extraction:**
```powershell
Select-String -Path "logs/robot/*.jsonl" -Pattern "SNAPSHOT_METRICS" | Select-Object -Last 10
```

---

## B2 — CPU Measurement (Manual)

**Tool:** Task Manager, Process Explorer, or `Get-Process` (PowerShell)

**Steps:**
1. Identify NinjaTrader process (e.g. `NinjaTrader.exe` or `NinjaTrader64.exe`)
2. Run during **active market period** (e.g. 9:30–11:00 AM ET)
3. Capture over **5–10 minutes**
4. Record:
   - `avg_cpu` — average CPU %
   - `peak_cpu` — max CPU %
   - `std_dev_cpu` — if available (Process Explorer, perfmon)

**PowerShell sample (run in loop, log to file):**
```powershell
$proc = Get-Process -Name "NinjaTrader*" -ErrorAction SilentlyContinue
if ($proc) { $proc.CPU; $proc.WorkingSet64 }
```

**Better:** Use Process Explorer or perfmon for sustained sampling.

---

## B3 — Logging Metrics (Instrumented)

**Status:** Implemented. Metrics emit in `LOG_PIPELINE_METRIC` every 60 seconds.

**New fields (Phase B):**
| Field | Description |
|-------|--------------|
| avg_queue_depth | Running average of queue depth |
| peak_queue_depth | Max queue depth since start |
| events_per_sec | Events enqueued per second (last minute) |
| flush_time_ms | Duration of last flush batch |

**Where to find:** `logs/robot/*.jsonl` — grep for `LOG_PIPELINE_METRIC`

**Targets:**
- Queue stays far from 50k (max_queue_size)
- No backpressure events (`LOG_BACKPRESSURE_DROP`)
- Stable throughput

---

## B4 — System Scaling Check (Manual)

**Steps:**
1. **Run 1 engine:** Configure timetable to enable only one instrument. Run 5–10 min. Record `cpu_1`.
2. **Run 7 engines:** Full config. Run 5–10 min. Record `cpu_7`.
3. **Compute:** `cpu_ratio = cpu_7 / cpu_1`

**Interpretation:**
| Result | Meaning |
|--------|---------|
| ~7x | Linear scaling (acceptable) |
| >7x | Hidden contention still exists |
| <7x | Improved efficiency |

---

## Decision Gate (After Phase B)

**Question 1:** Is CPU now stable under live market conditions?

**Question 2:** Are there still visible stalls, lag, or disconnects?

**Question 3:** Are snapshot durations or bursts still high?

---

## Branching Logic

| Case | CPU | Action |
|------|-----|--------|
| **1 — Stable** | Stable | Proceed to Phase C as architecture hardening (not firefighting) |
| **2 — Improved but high** | Better but still high | Phase C becomes priority |
| **3 — Barely changed** | Same | Re-audit with runtime traces; likely NT locking or logging |

---

## Output Checklist

Before proceeding to Phase C, collect:

- [ ] `SNAPSHOT_METRICS` samples (calls_per_sec, p50/p95/p99, max_burst)
- [ ] `LOG_PIPELINE_METRIC` samples (avg/peak queue, events_per_sec, flush_time_ms)
- [ ] CPU stats (avg, peak) over 5–10 min during active market
- [ ] (Optional) Scaling: cpu_ratio from 1 vs 7 engines
