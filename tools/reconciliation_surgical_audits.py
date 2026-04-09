#!/usr/bin/env python3
"""
Surgical follow-ups to RECONCILIATION_FORENSIC_* (same incident merge + classification):

1) False-resolution: each SUSPICIOUS_FALSE_RESOLUTION — first resolution event, first regression,
   lines between, qty/gate snapshots at resolution / pre-regression / regression.
2) Unresolved tail: each UNRESOLVED — log end vs handoff vs stall/gate tail, end qty state.
3) MNG deep dive: qty-mismatch episodes, protectives, JI lines, run_id regression counts.

Usage:
  python tools/reconciliation_surgical_audits.py --log-dir "C:/Users/jakej/QTSW2/logs/robot" \\
    --trading-date 2026-04-08 --out docs/robot/audits/RECONCILIATION_SURGICAL_2026-04-08.md
"""
from __future__ import annotations

import argparse
import json
import re
import sys
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Any, Dict, List, Optional, Set, Tuple

_TOOLS = Path(__file__).resolve().parent
if str(_TOOLS) not in sys.path:
    sys.path.insert(0, str(_TOOLS))
import reconciliation_day_audit as rea  # noqa: E402
import reconciliation_forensic_reduction as fr  # noqa: E402

RESOLUTION_LIKE = fr.RESOLUTION_LIKE
REGRESSION_LIKE = fr.REGRESSION_LIKE
JOURNAL_FAIL = fr.JOURNAL_EVENTS  # JOURNAL_INTEGRITY_FAILED + RECONCILIATION_JOURNAL_INTEGRITY_FAILED
STALL_GATE = fr.STALL_GATE
sort_rows = fr.sort_rows

QTY_GATE_KEYS = (
    "broker_position_qty",
    "journal_open_qty",
    "journal_qty",
    "account_qty",
    "broker_qty",
    "local_qty",
    "engine_qty",
    "gate_state",
    "release_ready",
    "mismatch_type",
)


def qty_gate_snapshot(r: Dict[str, Any]) -> Dict[str, Any]:
    o = r.get("raw") or {}
    if not isinstance(o, dict):
        return {}
    data = o.get("data") if isinstance(o.get("data"), dict) else {}
    out: Dict[str, Any] = {}
    for k in QTY_GATE_KEYS:
        v = o.get(k)
        if v is None:
            v = data.get(k)
        if v is not None:
            out[k] = v
    return out


def row_ref(r: Dict[str, Any]) -> str:
    ts = r.get("_ts")
    ts_s = ts.isoformat() if ts else "?"
    return f"`{ts_s}` **{r.get('_event')}** inst=`{r.get('_instrument')}` file=`{r.get('_file')}`"


def rle_between(rows: List[Dict[str, Any]], i0: int, i1: int, cap: int = 60) -> str:
    """i0 exclusive, i1 exclusive — events between first resolution and first regression."""
    seg = rows[i0 + 1 : i1]
    if not seg:
        return "*(nothing between)*"
    out: List[str] = []
    last_e = None
    run = 0
    for r in seg:
        e = r["_event"]
        if e == last_e:
            run += 1
        else:
            if last_e is not None:
                out.append(f"{last_e} (×{run})" if run > 1 else last_e)
            last_e = e
            run = 1
    if last_e is not None:
        out.append(f"{last_e} (×{run})" if run > 1 else last_e)
    if len(out) > cap:
        return " → ".join(out[:cap]) + f" → … (**{len(out) - cap}** more distinct event runs)"
    return " → ".join(out)


def snapshot_line(label: str, r: Optional[Dict[str, Any]]) -> str:
    if r is None:
        return f"- **{label}:** *(none)*\n"
    snap = qty_gate_snapshot(r)
    return f"- **{label}:** {row_ref(r)}\n  - payload fields: `{snap}`\n"


def compare_snap(a: Dict[str, Any], b: Dict[str, Any]) -> str:
    keys = sorted(set(a) | set(b))
    parts = []
    for k in keys:
        va, vb = a.get(k), b.get(k)
        if va == vb:
            parts.append(f"{k}: unchanged ({va!r})")
        else:
            parts.append(f"{k}: {va!r} → {vb!r}")
    return "; ".join(parts) if parts else "no qty/gate fields in payloads"


def global_day_bounds(all_sorted: List[Dict[str, Any]]) -> Tuple[Optional[datetime], Optional[datetime]]:
    ts = [r["_ts"] for r in all_sorted if r.get("_ts")]
    if not ts:
        return None, None
    return min(ts), max(ts)


def next_activity_after(
    all_sorted: List[Dict[str, Any]],
    t1: datetime,
    instruments: List[str],
    run_ids: List[str],
    within_min: float = 45.0,
) -> List[Dict[str, Any]]:
    ins_set = set(instruments)
    run_set = set(run_ids)
    end = t1 + timedelta(minutes=within_min)
    found: List[Dict[str, Any]] = []
    for r in all_sorted:
        ts = r.get("_ts")
        if ts is None or ts <= t1 or ts > end:
            continue
        ri, rr = r.get("_instrument"), r.get("_run_id")
        if run_set and rr and rr in run_set:
            found.append(r)
            continue
        if ins_set and ri and ri in ins_set:
            found.append(r)
            continue
    return found[:25]


def protective_hint(rows: List[Dict[str, Any]]) -> str:
    blob = " ".join(fr.raw_blob(r.get("raw") or {}) for r in rows[:200]).lower()
    hits = []
    if re.search(r"\bprotective\b|\bprotection\b|\boco\b|\bbracket\b|\bstop\b.*\btarget\b", blob):
        hits.append("payload mentions protective/OCO/bracket-style language (substring)")
    if not hits:
        return "no protective keywords in sampled payloads (not proof of absence)"
    return "; ".join(hits)


def run_false_resolution(
    incidents: List[Dict[str, Any]], lines: List[str]
) -> None:
    sus = [inc for inc in incidents if inc["outcome_class"] == "SUSPICIOUS_FALSE_RESOLUTION"]
    lines.append("## 1 — False-resolution audit\n\n")
    lines.append(
        f"**Population:** **{len(sus)}** merged incidents classified `SUSPICIOUS_FALSE_RESOLUTION` "
        f"(first resolution-class event before first regression-class event in the same window).\n\n"
    )
    for n, inc in enumerate(sus, 1):
        rows = sort_rows(inc["rows"])
        first_res_idx = None
        first_reg_idx = None
        for i, r in enumerate(rows):
            if r["_event"] in RESOLUTION_LIKE:
                first_res_idx = i
                break
        if first_res_idx is None:
            lines.append(f"### 1.{n} *(anomaly: no resolution event)*\n\n")
            continue
        for j in range(first_res_idx + 1, len(rows)):
            if rows[j]["_event"] in REGRESSION_LIKE:
                first_reg_idx = j
                break
        lines.append(f"### 1.{n}\n\n")
        lines.append(f"- **Window:** `{inc['t0']}` → `{inc['t1']}`\n")
        lines.append(f"- **instruments:** {inc.get('instruments') or '—'}\n")
        lines.append(f"- **run_ids:** {inc.get('run_ids') or '—'}\n\n")
        r_res = rows[first_res_idx]
        r_reg = rows[first_reg_idx] if first_reg_idx is not None else None
        lines.append("**Anchor events (evidence):**\n\n")
        lines.append(snapshot_line("First resolution-class", r_res))
        if r_reg is not None:
            lines.append(snapshot_line("First regression-class after it", r_reg))
        else:
            lines.append("- **First regression-class:** *(not found — classification mismatch)*\n")

        if first_reg_idx is not None:
            lines.append(f"- **Distinct event runs between (exclusive):** {rle_between(rows, first_res_idx, first_reg_idx)}\n\n")
            s0 = qty_gate_snapshot(r_res)
            s1 = qty_gate_snapshot(rows[first_reg_idx - 1]) if first_reg_idx > first_res_idx + 1 else s0
            s2 = qty_gate_snapshot(r_reg) if r_reg else {}
            lines.append("**Qty / gate (payload-only):**\n\n")
            lines.append(f"- At first resolution: `{s0}`\n")
            lines.append(f"- Last row before regression: `{s1}`\n")
            lines.append(f"- At regression: `{s2}`\n")
            lines.append(f"- **Delta resolution → regression:** {compare_snap(s0, s2)}\n")
            lines.append(
                f"- **Interpretation (conservative):** if resolution event is `JOURNAL_INTEGRITY_RECOVERED` or `PASS_SUMMARY` "
                f"without `CONVERGED`/`RESOLVED`, treat as **ambiguous semantics** unless product docs define success; "
                f"compare qty fields above for objective movement.\n\n"
            )
        else:
            lines.append("\n")


def run_unresolved_tail(
    incidents: List[Dict[str, Any]], all_sorted: List[Dict[str, Any]], lines: List[str]
) -> None:
    unr = [inc for inc in incidents if inc["outcome_class"] == "UNRESOLVED"]
    day_t0, day_t1 = global_day_bounds(all_sorted)
    lines.append("## 2 — Unresolved-tail audit\n\n")
    lines.append(f"**Population:** **{len(unr)}** merged incidents classified `UNRESOLVED`.\n\n")
    gap_end = timedelta(minutes=3)
    for n, inc in enumerate(unr, 1):
        rows = sort_rows(inc["rows"])
        last_non_noise = None
        for r in reversed(rows):
            if r["_event"] not in rea.NOISE_EVENTS:
                last_non_noise = r
                break
        if last_non_noise is None and rows:
            last_non_noise = rows[-1]

        lines.append(f"### 2.{n}\n\n")
        lines.append(f"- **Window:** `{inc['t0']}` → `{inc['t1']}`\n")
        lines.append(f"- **instruments:** {inc.get('instruments') or '—'}\n")
        lines.append(f"- **run_ids:** {inc.get('run_ids') or '—'}\n\n")

        if last_non_noise:
            lines.append(f"- **Last non-noise event in window:** {row_ref(last_non_noise)}\n")
            lines.append(f"  - payload: `{qty_gate_snapshot(last_non_noise)}`\n")
        stall_tail = False
        if last_non_noise:
            evu = last_non_noise["_event"].upper()
            stall_tail = any(s in evu for s in STALL_GATE)
        lines.append(
            f"- **Tail looks like stall/gate/no-progress (event name substring):** {'yes' if stall_tail else 'no'}\n"
        )

        # Log ended soon after?
        near_end = False
        if day_t1 and inc.get("t1") and (day_t1 - inc["t1"]) <= gap_end:
            near_end = True
        lines.append(
            f"- **Incident end within ~3 min of last event in day extract:** {'yes (audit may end before closure)' if near_end else 'no'} "
            f"(day last_ts=`{day_t1}`)\n"
        )

        nxt = next_activity_after(
            all_sorted,
            inc["t1"],
            list(inc.get("instruments") or []),
            list(inc.get("run_ids") or []),
            within_min=45.0,
        )
        if nxt:
            lines.append(
                f"- **Activity within 45 min after window (same inst or run_id):** yes — first: {row_ref(nxt[0])}\n"
            )
            if len(nxt) > 1:
                lines.append(f"  - (+{len(nxt) - 1} more lines in window)\n")
        else:
            lines.append(
                "- **Activity within 45 min after window (same inst or run_id):** no lines found (handoff not visible in-band or gap)\n"
            )

        mm_end = qty_gate_snapshot(last_non_noise) if last_non_noise else {}
        has_mm = any(
            k in mm_end for k in ("journal_qty", "account_qty", "broker_qty", "local_qty", "broker_position_qty")
        )
        lines.append(
            f"- **Broker/journal fields at last non-noise row:** {'present' if has_mm else 'missing/empty'} `{mm_end}`\n\n"
        )


def run_mng_deep_dive(incidents: List[Dict[str, Any]], all_sorted: List[Dict[str, Any]], lines: List[str]) -> None:
    lines.append("## 3 — MNG deep dive\n\n")
    mng_inc = [
        inc
        for inc in incidents
        if "MNG" in (inc.get("instruments") or [])
        or any((r.get("_instrument") == "MNG") for r in inc.get("rows") or [])
    ]
    lines.append(f"**Merged incidents touching MNG:** **{len(mng_inc)}**\n\n")

    qm_inc = [
        inc
        for inc in mng_inc
        if any(
            r["_event"] in ("RECONCILIATION_QTY_MISMATCH", "RECONCILIATION_QTY_MISMATCH_STILL_OPEN")
            for r in inc.get("rows") or []
        )
    ]
    lines.append(f"**Of those, with at least one `RECONCILIATION_QTY_MISMATCH` / `STILL_OPEN`:** **{len(qm_inc)}**\n\n")

    # First mismatch row per incident
    for n, inc in enumerate(qm_inc, 1):
        rows = sort_rows(inc["rows"])
        first_mm = next(
            (
                r
                for r in rows
                if r["_event"] in ("RECONCILIATION_QTY_MISMATCH", "RECONCILIATION_QTY_MISMATCH_STILL_OPEN")
            ),
            None,
        )
        ji = any(r["_event"] in JOURNAL_FAIL for r in rows)
        lines.append(f"### 3.{n}\n\n")
        lines.append(f"- **Window:** `{inc['t0']}` → `{inc['t1']}` | **outcome:** `{inc['outcome_class']}`\n")
        if first_mm:
            lines.append(f"- **First qty mismatch row:** {row_ref(first_mm)}\n")
            lines.append(f"  - payload: `{qty_gate_snapshot(first_mm)}`\n")
        lines.append(f"- **Protectives (payload keyword scan):** {protective_hint(rows)}\n")
        lines.append(f"- **Journal integrity failure lines present:** {'yes' if ji else 'no'}\n\n")

    # Per run_id: ordered REGRESSION_LIKE for MNG instrument rows
    lines.append("### Per `run_id` — MNG regression-like events (global day, instrument=MNG)\n\n")
    mng_rows = [r for r in all_sorted if (r.get("_instrument") == "MNG")]
    by_run: Dict[str, List[Dict[str, Any]]] = {}
    for r in mng_rows:
        rid = r.get("_run_id") or ""
        if not rid:
            continue
        by_run.setdefault(rid, []).append(r)
    reg_counts: List[Tuple[str, int]] = []
    for rid, lst in by_run.items():
        lst.sort(key=lambda x: (x.get("_ts"), x.get("_file")))
        cnt = sum(1 for r in lst if r["_event"] in REGRESSION_LIKE)
        if cnt > 0:
            reg_counts.append((rid, cnt))
    reg_counts.sort(key=lambda x: -x[1])
    for rid, cnt in reg_counts[:15]:
        lines.append(f"- `{rid}`: **{cnt}** regression-class lines (`QTY_MISMATCH` / `STILL_OPEN` / `STUCK`)\n")
    lines.append(
        "\n*Repeated high counts suggest the same run context saw multiple regression signals (not necessarily separate reconciliation failures).* \n"
    )


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--log-dir", type=str, default=None)
    ap.add_argument("--trading-date", type=str, required=True)
    ap.add_argument("--out", type=str, required=True)
    args = ap.parse_args()

    log_dir = rea.resolve_log_dir(args.log_dir)
    td = args.trading_date.strip()[:10]
    incidents, all_sorted, _rows, n_sig = fr.load_incidents_for_day(log_dir, td)

    lines: List[str] = []
    lines.append("# Surgical reconciliation audits\n\n")
    lines.append(f"- **Logs:** `{log_dir.resolve()}`\n")
    lines.append(f"- **Trading date:** `{td}`\n")
    lines.append(
        f"- **Incident model:** same as `reconciliation_forensic_reduction.load_incidents_for_day` "
        f"(**{n_sig}** high-signal clusters before merge).\n\n"
    )

    run_false_resolution(incidents, lines)
    run_unresolved_tail(incidents, all_sorted, lines)
    run_mng_deep_dive(incidents, all_sorted, lines)

    outp = Path(args.out)
    outp.write_text("".join(lines), encoding="utf-8")
    print(
        f"Wrote {outp.resolve()} suspicious={sum(1 for i in incidents if i['outcome_class']=='SUSPICIOUS_FALSE_RESOLUTION')} "
        f"unresolved={sum(1 for i in incidents if i['outcome_class']=='UNRESOLVED')}",
        flush=True,
    )


if __name__ == "__main__":
    main()
