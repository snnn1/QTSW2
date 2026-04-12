# Daily audit reports (Robot + Watchdog)

Generated outputs from the daily audit / timeline pipeline go here:

- `YYYY-MM-DD.json` — machine-readable rollup + timeline
- `YYYY-MM-DD.md` — human-readable summary (optional)

This directory is **versioned** so you can keep a history of decision-grade daily snapshots if you choose to commit them.

Runtime-only or very large artifacts can alternatively be written under `logs/audit/` (gitignored with the rest of `logs/`).

See `docs/robot/audits/DAILY_AUDIT_FRAMEWORK_ROBOT_WATCHDOG.md`.
