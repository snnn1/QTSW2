# Robot documentation — summaries hub

**Purpose:** One place to find **curated indexes** and **roll-up summaries** of incidents, fixes, and audits. Detailed forensics stay under [`../incidents/`](../incidents/) and topic docs under [`..`](../).

---

## Quick links

| Folder / doc | Use when you need… |
|--------------|-------------------|
| **[issues/INDEX.md](issues/INDEX.md)** | A **catalog of incidents** (what broke, link to full write-up). |
| **[fixes/INDEX.md](fixes/INDEX.md)** | **What we changed** (code, tools, config) mapped to problem areas. |
| **[MASTER_RECENT_ISSUES_AND_FIXES_2026-03-17_through_2026-03-20.md](MASTER_RECENT_ISSUES_AND_FIXES_2026-03-17_through_2026-03-20.md)** | **Large single-page** summary: recent cross-session issues + fixes (Mar 17–20, 2026 cluster). |
| [../incidents/2026-03-12_FULL_ISSUES_AND_FIXES_SUMMARY.md](../incidents/2026-03-12_FULL_ISSUES_AND_FIXES_SUMMARY.md) | Older **single-session** rollup (2026-03-12). |
| [../incidents/INCIDENT_PACKS_SUMMARY.md](../incidents/INCIDENT_PACKS_SUMMARY.md) | **Replay incident packs** (determinism / invariants), not live ops. |

---

## Naming convention

- **`MASTER_*`** — multi-issue rollups spanning days or themes.  
- **`issues/INDEX.md`** — living catalog (append new rows when incidents are filed).  
- **`fixes/INDEX.md`** — living catalog of remediations (point to PRs/commits when applicable).

---

## Adding a new incident

1. Add the full narrative under `docs/robot/incidents/YYYY-MM-DD_*.md`.  
2. Add one row to **[issues/INDEX.md](issues/INDEX.md)**.  
3. If there is a **code/tool fix**, add a row to **[fixes/INDEX.md](fixes/INDEX.md)**.  
4. Optionally extend the latest **MASTER_*** rollup or create a new dated master for the week.
