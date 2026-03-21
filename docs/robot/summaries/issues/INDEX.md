# Issues index (incidents catalog)

**One-line summaries** with links to full investigations. Sort: **newest first** (approximate by filename date).

---

## 2026-03 — execution / timetable / connectivity (high priority)

| ID | Summary | Doc |
|----|---------|-----|
| ES1/ES2 adoption | Restart adoption failed or fired false UNOWNED; ES1 “forgot” broker orders; ES2 blocked via shared MES. | [2026-03-19_ES1_ES2_ADOPTION_AND_SUBMISSION_INVESTIGATION.md](../incidents/2026-03-19_ES1_ES2_ADOPTION_AND_SUBMISSION_INVESTIGATION.md) |
| S1 orders cancelled | New `run_id` → registry empty → UNOWNED_ENTRY_RESTART → flatten ES1 brackets. | [2026-03-19_S1_ORDERS_CANCELLED_INVESTIGATION.md](../incidents/2026-03-19_S1_ORDERS_CANCELLED_INVESTIGATION.md) |
| Timetable 07:30 NO_TRADE | Bad `timetable_current.json` revision set ES1/NG1 S1 slot to **07:30** mid range-build → `NO_TRADE_MATERIALLY_DELAYED_INITIAL_SUBMISSION`. | [2026-03-20_S1_TIMETABLE_SLOT_CORRUPTION_NO_TRADE_INVESTIGATION.md](../incidents/2026-03-20_S1_TIMETABLE_SLOT_CORRUPTION_NO_TRADE_INVESTIGATION.md) |
| 07:30 disconnect | Connection loss / strategy behavior around 07:30 CT. | [2026-03-19_INCIDENT_REPORT_0730_DISCONNECT.md](../incidents/2026-03-19_INCIDENT_REPORT_0730_DISCONNECT.md) |
| Disconnect deep dive | Extended disconnect analysis. | [2026-03-19_DISCONNECT_DEEP_INVESTIGATION_COMPLETE.md](../incidents/2026-03-19_DISCONNECT_DEEP_INVESTIGATION_COMPLETE.md), [2026-03-19_DISCONNECT_INVESTIGATION_FULL.md](../incidents/2026-03-19_DISCONNECT_INVESTIGATION_FULL.md) |
| Connection loss restart | Restart deleted / lost order context. | [2026-03-17_INCIDENT_REPORT_CONNECTION_LOSS_RESTART_ORDERS_DELETED.md](../incidents/2026-03-17_INCIDENT_REPORT_CONNECTION_LOSS_RESTART_ORDERS_DELETED.md) |
| Full system 2026-03-17 | Multi-topic investigation summary. | [2026-03-17_FULL_SYSTEM_INVESTIGATION_SUMMARY.md](../incidents/2026-03-17_FULL_SYSTEM_INVESTIGATION_SUMMARY.md) |
| YM1 reconciliation qty | `journal_qty` vs broker mismatch; partial exit semantics. | [2026-03-17_YM1_RECONCILIATION_QTY_MISMATCH_INVESTIGATION.md](../incidents/2026-03-17_YM1_RECONCILIATION_QTY_MISMATCH_INVESTIGATION.md) |
| M2K adopted fill | Ownership / fill routing after adoption. | [2026-03-17_M2K_ADOPTED_ORDER_FILL_FIX.md](../incidents/2026-03-17_M2K_ADOPTED_ORDER_FILL_FIX.md) |
| Strategy disconnect assessment | Platform disconnect impact. | [2026-03-18_STRATEGY_DISCONNECT_ASSESSMENT.md](../incidents/2026-03-18_STRATEGY_DISCONNECT_ASSESSMENT.md) |

---

## 2026-03 — reconciliation / journals / streams

| Summary | Doc |
|---------|-----|
| ORDER_REGISTRY_MISSING | [2026-03-16_ORDER_REGISTRY_MISSING_INVESTIGATION.md](../incidents/2026-03-16_ORDER_REGISTRY_MISSING_INVESTIGATION.md) |
| ES2 missing limit | [2026-03-06_ES2_MISSING_LIMIT_ORDER_INVESTIGATION.md](../incidents/2026-03-06_ES2_MISSING_LIMIT_ORDER_INVESTIGATION.md) |
| MES reconciliation qty | [2026-03-06_MES_RECONCILIATION_QTY_MISMATCH_INVESTIGATION.md](../incidents/2026-03-06_MES_RECONCILIATION_QTY_MISMATCH_INVESTIGATION.md) |
| MYM / MNG / NG / MES qty mismatches | [2026-03-11_MYM_…](../incidents/2026-03-11_MYM_RECONCILIATION_QTY_MISMATCH_INVESTIGATION.md), [2026-03-13_MES_…](../incidents/2026-03-13_MES_RECONCILIATION_QTY_MISMATCH_INVESTIGATION.md), [2026-03-13_NG_…](../incidents/2026-03-13_NG_QTY_MISMATCH_INVESTIGATION.md), [2026-03-14_MNG_…](../incidents/2026-03-14_MNG_RECONCILIATION_QTY_MISMATCH_INVESTIGATION.md) |
| Daily journal no streams | [2026-03-13_DAILY_JOURNAL_NO_STREAMS_INVESTIGATION.md](../incidents/2026-03-13_DAILY_JOURNAL_NO_STREAMS_INVESTIGATION.md) |
| Timetable override | [2026-03-04_TIMETABLE_OVERRIDE_INVESTIGATION.md](../incidents/2026-03-04_TIMETABLE_OVERRIDE_INVESTIGATION.md) |

---

## Older packs & meta

| Summary | Doc |
|---------|-----|
| Full issues+fixes session (2026-03-12) | [2026-03-12_FULL_ISSUES_AND_FIXES_SUMMARY.md](../incidents/2026-03-12_FULL_ISSUES_AND_FIXES_SUMMARY.md) |
| Daily summary 2026-03-12 | [2026-03-12_DAILY_SUMMARY.md](../incidents/2026-03-12_DAILY_SUMMARY.md) |
| Incident replay packs (IEA) | [INCIDENT_PACKS_SUMMARY.md](../incidents/INCIDENT_PACKS_SUMMARY.md) |
| Error catalog ↔ packs | [ERROR_CATALOG_INCIDENT_PACKS.md](../incidents/ERROR_CATALOG_INCIDENT_PACKS.md) |

---

*Path note: links use `../incidents/` because this file lives in `summaries/issues/`.*
