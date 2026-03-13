# Trade Data Retention

This document describes the canonical trade data sources and retention policy for the QTSW2 robot system.

## Trade History Sources

### Execution journals

**Location:** `data/execution_journals/`

**Format:** `{trading_date}_{stream}_{intent_id}.json`

Canonical record of every trade. Contains entry, exit, PnL, fees, completion reason, bracket levels.

**Must never be automatically deleted.**

### Slot journals

**Location:** `logs/robot/journal/`

**Format:** `{trading_date}_{stream}.json`

Contains stream outcomes and no-trade states (e.g. NO_TRADE_MARKET_CLOSE). Required for Daily Journal to show non-trading streams (e.g. GC2 when range did not break).

**Must never be automatically deleted.**

### Robot logs

**Location:** `logs/robot/` and `logs/robot/archive/`

**Format:** `robot_{instrument}.jsonl` and `robot_{instrument}_{timestamp}.jsonl`

Contain EXECUTION_FILLED events used for diagnostics and fill reconstruction. Not canonical trade records; execution journals are authoritative. Ledger builder reads from both root and archive directories so historical trades remain retrievable after log rotation.

### Eligibility files

**Location:** `data/timetable/`

**Format:** `eligibility_{trading_date}.json`

Required for correct historical stream visibility in Daily Journal. Filters no-trade streams by timetable (excludes ES1/ES2 when not scheduled).

### Execution summaries

**Location:** `data/execution_summaries/`

**Format:** `{trading_date}.json`

Daily execution summary. Optional for trade history but useful for quick lookups.

---

## Retention Policy

### Never delete automatically

- `data/execution_journals/`
- `logs/robot/journal/`

### Archive but retain long term

- `logs/robot/archive/` — Robot logs are moved here after `archive_days` (default 7). Files older than `archive_cleanup_days` (default 30) may be deleted from archive. Ledger builder and fill metrics read from archive.

### Operational logs

Operational logs (frontend feed, event logs) may be cleaned up per their own retention rules. Trade journals must remain.

---

## Backup Recommendation

Back up the following directories for long-term trade history:

- `data/execution_journals/`
- `logs/robot/journal/`
- `logs/robot/archive/`
- `data/execution_summaries/`
- `data/timetable/eligibility_*.json` (for dates you care about)

---

## Long-Term Architecture Note

Trade history should ultimately rely on execution journals, not robot logs.

Execution journals must contain all fields required for:

- entry price
- exit price
- direction
- quantity
- realized PnL
- fees
- completion reason

Robot logs should only be required for diagnostics. Future improvements may remove the dependency on archived logs entirely by ensuring execution journals are self-sufficient for PnL and trade reconstruction.
