# QTSW2 Workspace

This root is the operating surface for the stack, not just source code. The intent is:

- `system/` holds product code, engines, modules, tests, and packaged add-ons.
- `tools/`, `launch/`, `automation/`, and `batch/` are operator entry points and maintenance scripts.
- `configs/`, `docs/`, `research/`, and `decisions/` hold controlled inputs and reference material.
- `runs/`, `data/`, `logs/`, `reports/`, `events/`, `derived/`, and `state/` hold runtime outputs and audit trails.
- `tmp/` is the scratch zone for temporary local artifacts, pytest workdirs, and short-lived diagnostics.

Compatibility notes:

- `runs/LATEST_RUN.txt` is the active run pointer used by watchdog and audit tooling.
- Root-level `KEY_EVENTS.jsonl`, `summary.json`, and `AUDIT_MANIFEST.json` are compatibility/runtime artifacts for root-scoped runs. They should stay addressable at the workspace root when present.

Hygiene rule:

- New temporary or test-only artifacts should land under `tmp/`, not at the workspace root.
