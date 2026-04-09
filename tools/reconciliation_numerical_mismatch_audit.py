#!/usr/bin/env python3
"""
Numerical reconciliation mismatch audit: broker vs journal deltas from JSONL (log-backed only).

Usage:
  python tools/reconciliation_numerical_mismatch_audit.py --log-dir "C:/Users/jakej/QTSW2/logs/robot" \\
    --trading-date 2026-04-08 --tsv-out docs/robot/audits/RECONCILIATION_NUMERICAL_MISMATCH_2026-04-08.tsv \\
    --md-out docs/robot/audits/RECONCILIATION_NUMERICAL_MISMATCH_2026-04-08.md
"""
from __future__ import annotations

import argparse
import json
import math
import sys
from collections import defaultdict
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple

_TOOLS = Path(__file__).resolve().parent
if str(_TOOLS) not in sys.path:
    sys.path.insert(0, str(_TOOLS))
import reconciliation_day_audit as rea  # noqa: E402

EPISODE_GAP_SEC = 120.0
OUTCOME_HORIZON_SEC = 3600.0  # look for CONVERGED/STUCK within 1h after last mismatch row

QTY_KEYS_ACCOUNT = ("account_qty", "broker_qty", "broker_position_qty")
QTY_KEYS_JOURNAL = ("journal_qty", "journal_open_qty")


def parse_int_qty(v: Any) -> Optional[int]:
    if v is None:
        return None
    if isinstance(v, bool):
        return None
    if isinstance(v, int):
        return v
    try:
        s = str(v).strip()
        if not s:
            return None
        return int(float(s))
    except (ValueError, TypeError):
        return None


def merge_payload(obj: Dict[str, Any]) -> Dict[str, Any]:
    data = obj.get("data")
    if isinstance(data, dict):
        m = {**data}
        for k, v in obj.items():
            if k not in m and k not in ("data",):
                m[k] = v
        return m
    return dict(obj)


def extract_account_journal(m: Dict[str, Any]) -> Tuple[Optional[int], Optional[int]]:
    jq = None
    aq = None
    for k in QTY_KEYS_JOURNAL:
        if k in m and m[k] is not None:
            jq = parse_int_qty(m[k])
            break
    for k in QTY_KEYS_ACCOUNT:
        if k in m and m[k] is not None:
            aq = parse_int_qty(m[k])
            break
    return aq, jq


def event_name(obj: Dict[str, Any]) -> str:
    return str(obj.get("event") or obj.get("event_type") or "")


def is_mismatch_row(obj: Dict[str, Any]) -> bool:
    ev = event_name(obj).upper()
    if "RECONCILIATION_QTY_MISMATCH" in ev:
        return True
    m = merge_payload(obj)
    aq, jq = extract_account_journal(m)
    if aq is None or jq is None:
        return False
    return True


def row_matches_line_criteria(obj: Dict[str, Any]) -> bool:
    """Explicit QTY_MISMATCH events, or both qty fields with account != journal (numerical inequality)."""
    ev = event_name(obj).upper()
    if "RECONCILIATION_QTY_MISMATCH" in ev or "QTY_MISMATCH_STILL_OPEN" in ev:
        return True
    m = merge_payload(obj)
    aq, jq = extract_account_journal(m)
    if aq is None or jq is None:
        return False
    return aq != jq


def iter_engine_lines(log_dir: Path, trading_date: str) -> List[Dict[str, Any]]:
    rows: List[Dict[str, Any]] = []
    for fp in rea.engine_files(log_dir):
        try:
            with fp.open("r", encoding="utf-8", errors="replace") as f:
                for line in f:
                    if "journal" not in line.lower() and "QTY_MISMATCH" not in line and "RECONCILIATION" not in line:
                        continue
                    line = line.strip()
                    if not line:
                        continue
                    try:
                        obj = json.loads(line)
                    except json.JSONDecodeError:
                        continue
                    if not isinstance(obj, dict):
                        continue
                    ts = rea.parse_ts(obj.get("ts_utc") or obj.get("ts") or obj.get("timestamp"))
                    if not rea.row_matches_trading_day(obj, trading_date, ts):
                        continue
                    rows.append({"_ts": ts, "_file": fp.name, "raw": obj})
        except OSError:
            continue
    rows.sort(key=lambda r: (r["_ts"] or datetime.min.replace(tzinfo=timezone.utc), r["_file"]))
    return rows


def normalize_row(r: Dict[str, Any]) -> Optional[Dict[str, Any]]:
    obj = r["raw"]
    if not row_matches_line_criteria(obj):
        return None
    m = merge_payload(obj)
    aq, jq = extract_account_journal(m)
    if aq is None or jq is None:
        return None
    inst = str(obj.get("instrument") or m.get("instrument") or "").strip()
    rid = str(obj.get("run_id") or m.get("run_id") or "").strip()
    delta = aq - jq
    return {
        "ts": r["_ts"],
        "instrument": inst or "UNKNOWN",
        "run_id": rid,
        "account_qty": aq,
        "journal_qty": jq,
        "delta": delta,
        "event_type": event_name(obj),
        "file": r["_file"],
    }


def cluster_episodes(rows: List[Dict[str, Any]]) -> List[List[Dict[str, Any]]]:
    """Same instrument + run_id + gap <= 2 min between consecutive rows."""
    if not rows:
        return []
    clusters: List[List[Dict[str, Any]]] = []
    cur: List[Dict[str, Any]] = []
    last_ts = None
    last_inst = None
    last_rid = None

    for r in rows:
        ts = r["ts"]
        inst = r["instrument"]
        rid = r["run_id"]
        if not cur:
            cur = [r]
            last_ts, last_inst, last_rid = ts, inst, rid
            continue
        gap = (ts - last_ts).total_seconds() if ts and last_ts else 99999
        same_key = inst == last_inst and rid == last_rid
        if not same_key or gap > EPISODE_GAP_SEC:
            clusters.append(cur)
            cur = [r]
        else:
            cur.append(r)
        last_ts, last_inst, last_rid = ts, inst, rid
    if cur:
        clusters.append(cur)
    return clusters


def instrument_from_obj(obj: Dict[str, Any]) -> str:
    m = merge_payload(obj)
    return str(obj.get("instrument") or m.get("instrument") or "").strip()


def find_outcome_after(
    all_sorted: List[Dict[str, Any]],
    inst: str,
    after_ts: datetime,
    until_ts: datetime,
) -> str:
    """First RECONCILIATION_CONVERGED or RECONCILIATION_STUCK for instrument after after_ts."""
    for r in all_sorted:
        obj = r["raw"]
        ts = r["_ts"]
        if ts is None or ts <= after_ts or ts > until_ts:
            continue
        ev = event_name(obj).upper()
        ins = instrument_from_obj(obj)
        if inst and inst != "UNKNOWN":
            if not ins or ins.upper() != inst.upper():
                continue
        if ev == "RECONCILIATION_CONVERGED":
            return "CONVERGED"
        if ev == "RECONCILIATION_STUCK":
            return "STUCK"
    return "UNRESOLVED"


def classify_episode(
    first: Dict[str, Any],
    last: Dict[str, Any],
    outcome: str,
) -> str:
    """Log-backed rules; UNKNOWN when ambiguous."""
    fa, fj = first["account_qty"], first["journal_qty"]
    la, lj = last["account_qty"], last["journal_qty"]
    fd = first["delta"]
    ld = last["delta"]
    if fa == fj and la == lj and fd == 0 and ld == 0:
        return "NON_QTY_MISMATCH"
    if outcome in ("STUCK", "UNRESOLVED"):
        if fj > fa:
            return "PERSISTENT_MISMATCH_JOURNAL_HIGH"
        if fj < fa:
            return "PERSISTENT_MISMATCH_JOURNAL_LOW"
        return "PERSISTENT_MISMATCH"
    if outcome == "CONVERGED":
        if la == lj:
            if abs(fd) <= 1 and abs(ld) <= 1:
                return "TRANSIENT_SYNC"
            if fj < fa:
                return "JOURNAL_LAG"
            if fj > fa:
                return "JOURNAL_OVERSHOOT"
        return "UNKNOWN"
    return "UNKNOWN"


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--log-dir", type=str, default=None)
    ap.add_argument("--trading-date", type=str, required=True)
    ap.add_argument("--tsv-out", type=str, required=True)
    ap.add_argument("--md-out", type=str, required=True)
    args = ap.parse_args()

    log_dir = rea.resolve_log_dir(args.log_dir)
    td = args.trading_date.strip()[:10]
    raw_lines = iter_engine_lines(log_dir, td)

    # Timeline for outcomes: converged/stuck rows with instrument
    timeline_for_outcome: List[Dict[str, Any]] = []
    mismatch_rows: List[Dict[str, Any]] = []
    for r in raw_lines:
        obj = r["raw"]
        ts = r["_ts"]
        ev = event_name(obj).upper()
        ins = instrument_from_obj(obj)
        if ev in ("RECONCILIATION_CONVERGED", "RECONCILIATION_STUCK") and ins:
            timeline_for_outcome.append({"_ts": ts, "raw": obj})
        nr = normalize_row(r)
        if nr:
            mismatch_rows.append(nr)

    timeline_for_outcome.sort(
        key=lambda x: (x["_ts"] or datetime.min.replace(tzinfo=timezone.utc),)
    )

    episodes = cluster_episodes(mismatch_rows)
    tsv_lines: List[str] = []
    tsv_lines.append(
        "timestamp\tinstrument\trun_id\taccount_qty\tjournal_qty\tdelta\tclassification\toutcome"
    )

    classifications: List[str] = []
    instruments: List[str] = []
    deltas_abs: List[float] = []
    outcomes: List[str] = []
    oscillation_hints: List[str] = []

    for ep in episodes:
        if not ep:
            continue
        first = ep[0]
        last = ep[-1]
        inst = first["instrument"]
        after_last = last["ts"] or datetime.min.replace(tzinfo=timezone.utc)
        horizon_end = after_last + timedelta(seconds=OUTCOME_HORIZON_SEC)
        outcome = find_outcome_after(timeline_for_outcome, inst, after_last, horizon_end)
        cls = classify_episode(first, last, outcome)
        classifications.append(cls)
        instruments.append(inst)
        deltas_abs.append(abs(first["delta"]))
        outcomes.append(outcome)

        ts_s = first["ts"].isoformat() if first["ts"] else "UNKNOWN"
        rid = first["run_id"] or "UNKNOWN"
        tsv_lines.append(
            f"{ts_s}\t{inst}\t{rid}\t{first['account_qty']}\t{first['journal_qty']}\t{first['delta']}\t{cls}\t{outcome}"
        )

        # Oscillation hint: same run_id multiple episodes (count separately in §7)
        if first["run_id"]:
            oscillation_hints.append(first["run_id"])

    Path(args.tsv_out).parent.mkdir(parents=True, exist_ok=True)
    Path(args.tsv_out).write_text("\n".join(tsv_lines) + "\n", encoding="utf-8")

    # Aggregates
    by_cls = defaultdict(int)
    for c in classifications:
        by_cls[c] += 1
    by_inst = defaultdict(int)
    for i in instruments:
        by_inst[i] += 1
    avg_mag = (
        sum(deltas_abs) / len(deltas_abs)
        if deltas_abs
        else float("nan")
    )
    oc = defaultdict(int)
    for o in outcomes:
        oc[o] += 1
    total = len(episodes)

    run_id_episode_counts = defaultdict(int)
    for ep in episodes:
        if ep and ep[0].get("run_id"):
            run_id_episode_counts[ep[0]["run_id"]] += 1

    md: List[str] = []
    md.append("# Numerical reconciliation mismatch audit\n\n")
    md.append(f"- **log_dir:** `{log_dir.resolve()}`\n")
    md.append(f"- **trading_date:** `{td}` (field or Chicago `ts_utc` per reconciliation_day_audit)\n")
    md.append(
        f"- **Episode rule:** same `instrument` + `run_id`, consecutive mismatch rows within **{EPISODE_GAP_SEC:.0f}s**.\n"
    )
    md.append(
        "- **Row rule:** `RECONCILIATION_QTY_MISMATCH` / `STILL_OPEN`, **or** both qty fields with **account ≠ journal**.\n"
    )
    md.append(
        f"- **Outcome:** first `RECONCILIATION_CONVERGED` or `RECONCILIATION_STUCK` for same instrument after last row, "
        f"within **{OUTCOME_HORIZON_SEC:.0f}s**; else `UNRESOLVED`.\n\n"
    )

    md.append("## SECTION 1 — Scope\n\n")
    md.append("Engine JSONL only; numerical delta analysis (not full event audit).\n\n")

    md.append("## SECTION 2 — Extract\n\n")
    md.append(f"- Mismatch-derived rows used for episodes: **{len(mismatch_rows)}**\n")
    md.append(f"- Distinct episodes (groups): **{total}**\n\n")

    md.append("## SECTION 3 — Group\n\n")
    md.append("Per episode table uses **first** row in group (initial snapshot); classification uses first vs last + outcome.\n\n")

    md.append("## SECTION 4 — Classification\n\n")
    md.append(
        "- **NON_QTY_MISMATCH:** first/last deltas zero (rare).\n"
        "- **TRANSIENT_SYNC:** CONVERGED and |delta|≤1 on first and last with equality at end.\n"
        "- **JOURNAL_LAG / JOURNAL_OVERSHOOT:** CONVERGED, journal<account / journal>account on first snapshot, equal at last.\n"
        "- **PERSISTENT_MISMATCH***:** STUCK or UNRESOLVED; sub-tags by first delta sign.\n"
        "- **UNKNOWN:** otherwise.\n\n"
    )

    md.append("## SECTION 5 — TSV\n\n")
    md.append(f"File: `{args.tsv_out}` (tab-separated).\n\n")

    md.append("## SECTION 6 — Aggregate\n\n")
    md.append(f"- **total episodes:** {total}\n")
    for k, v in sorted(by_cls.items(), key=lambda x: (-x[1], x[0])):
        md.append(f"- **{k}:** {v}\n")
    md.append("\n**per instrument (episodes):**\n\n")
    for k, v in sorted(by_inst.items(), key=lambda x: (-x[1], x[0])):
        md.append(f"- `{k}`: {v}\n")
    md.append(f"\n- **average |delta| (first row):** {avg_mag if not math.isnan(avg_mag) else 'UNKNOWN'}\n")
    md.append("\n**outcome (within horizon after episode end):**\n\n")
    for k in ("CONVERGED", "STUCK", "UNRESOLVED"):
        n = oc.get(k, 0)
        pct = (100.0 * n / total) if total else 0.0
        md.append(f"- **{k}:** {n} ({pct:.1f}%)\n")

    md.append("\n## SECTION 7 — Patterns (log-backed only)\n\n")
    j_low = sum(1 for ep in episodes if ep and ep[0]["journal_qty"] < ep[0]["account_qty"])
    j_high = sum(1 for ep in episodes if ep and ep[0]["journal_qty"] > ep[0]["account_qty"])
    md.append(f"- **First-row journal vs account:** journal &lt; account: **{j_low}** episodes; journal &gt; account: **{j_high}**.\n")
    delta_counts = defaultdict(int)
    for ep in episodes:
        if ep:
            delta_counts[ep[0]["delta"]] += 1
    top_d = sorted(delta_counts.items(), key=lambda x: -x[1])[:8]
    md.append(f"- **Dominant first deltas:** {', '.join(f'{d}→{c}' for d, c in top_d) or 'UNKNOWN'}\n")
    top_i = sorted(by_inst.items(), key=lambda x: -x[1])[:3]
    md.append(f"- **Top instruments by episode count:** {', '.join(f'{i}={n}' for i, n in top_i) or 'UNKNOWN'}\n")
    md.append(
        f"- **CONVERGED vs STUCK/UNRESOLVED:** see §6 (CONVERGED **{oc.get('CONVERGED', 0)}**, "
        f"STUCK **{oc.get('STUCK', 0)}**, UNRESOLVED **{oc.get('UNRESOLVED', 0)}**).\n"
    )
    multi_run = [rid for rid, c in run_id_episode_counts.items() if c >= 2]
    md.append(
        f"- **Same run_id with ≥2 episodes (possible repeated mismatch windows):** "
        f"**{len(multi_run)}** run_ids — {multi_run[:15]}{' …' if len(multi_run) > 15 else ''}\n"
    )

    md.append("\n## SECTION 8 — Constraints\n\n")
    md.append("Classifications are rule-based on extracted numerics and outcome events; ambiguous rows marked UNKNOWN.\n")

    Path(args.md_out).write_text("".join(md), encoding="utf-8")
    print(f"Wrote {args.tsv_out} episodes={total} mismatch_rows={len(mismatch_rows)}", flush=True)


if __name__ == "__main__":
    main()
