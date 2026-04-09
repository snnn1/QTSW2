#!/usr/bin/env python3
"""
Phase-2 forensic reduction: collapse high-signal clusters into distinct incidents,
classify outcomes from raw JSONL evidence, journal integrity triage, top-N severity.

Usage:
  python tools/reconciliation_forensic_reduction.py --log-dir "C:/Users/jakej/QTSW2/logs/robot" \\
    --trading-date 2026-04-08 --out docs/robot/audits/RECONCILIATION_FORENSIC_2026-04-08.md
"""
from __future__ import annotations

import argparse
import json
import re
import sys
from collections import defaultdict
from dataclasses import dataclass, field
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Dict, Iterable, List, Optional, Set, Tuple

# Import sibling module without package
_TOOLS = Path(__file__).resolve().parent
if str(_TOOLS) not in sys.path:
    sys.path.insert(0, str(_TOOLS))
import reconciliation_day_audit as rea  # noqa: E402

CHICAGO = rea.CHICAGO

MERGE_GAP_MINUTES = 45.0
SIG_GAP_MINUTES = 12.0
JOURNAL_EVENTS = frozenset({"JOURNAL_INTEGRITY_FAILED", "RECONCILIATION_JOURNAL_INTEGRITY_FAILED"})
QTY_MISMATCH_EVENTS = frozenset(
    {
        "RECONCILIATION_QTY_MISMATCH",
        "RECONCILIATION_QTY_MISMATCH_STILL_OPEN",
        "RECONCILIATION_CONTEXT",
    }
)
FORCED_EVENTS = frozenset(
    {
        "RECONCILIATION_FORCED_CONVERGENCE",
    }
)
RESOLUTION_LIKE = frozenset(
    {
        "RECONCILIATION_PASS_SUMMARY",
        "RECONCILIATION_RESOLVED",
        "RECONCILIATION_CONVERGED",
        "RECONCILIATION_MISMATCH_CLEARED",
        "JOURNAL_INTEGRITY_RECOVERED",
    }
)
REGRESSION_LIKE = frozenset(
    {
        "RECONCILIATION_QTY_MISMATCH",
        "RECONCILIATION_QTY_MISMATCH_STILL_OPEN",
        "RECONCILIATION_STUCK",
    }
)
STALL_GATE = (
    "GATE_STALL",
    "STATE_CONSISTENCY_GATE",
    "NO_PROGRESS",
    "STALL",
)


def inst_set(cl: List[Dict[str, Any]]) -> Set[str]:
    return {r["_instrument"] for r in cl if r.get("_instrument")}


def run_set(cl: List[Dict[str, Any]]) -> Set[str]:
    return {r["_run_id"] for r in cl if r.get("_run_id")}


def cluster_time_bounds(cl: List[Dict[str, Any]]) -> Tuple[Optional[datetime], Optional[datetime]]:
    ts = [r["_ts"] for r in cl if r.get("_ts")]
    if not ts:
        return None, None
    return min(ts), max(ts)


def can_merge_clusters(a: List[Dict[str, Any]], b: List[Dict[str, Any]], max_gap_min: float) -> bool:
    ia, ib = inst_set(a), inst_set(b)
    ra, rb = run_set(a), run_set(b)
    if ia and ib and not (ia & ib):
        return False
    if ra and rb and not (ra & rb):
        return False
    t_end_a, _ = cluster_time_bounds(a)
    _, t_start_b = cluster_time_bounds(b)
    if t_end_a is None or t_start_b is None:
        return False
    gap_min = (t_start_b - t_end_a).total_seconds() / 60.0
    return gap_min <= max_gap_min


def merge_signal_clusters(
    clusters: List[List[Dict[str, Any]]], max_gap_min: float
) -> List[List[Dict[str, Any]]]:
    if not clusters:
        return []
    out: List[List[Dict[str, Any]]] = []
    cur = list(clusters[0])
    for nxt in clusters[1:]:
        if can_merge_clusters(cur, nxt, max_gap_min):
            cur = cur + nxt
        else:
            out.append(cur)
            cur = list(nxt)
    out.append(cur)
    return out


def sort_rows(rows: List[Dict[str, Any]]) -> List[Dict[str, Any]]:
    return sorted(
        rows,
        key=lambda r: (r["_ts"] or datetime.min.replace(tzinfo=timezone.utc), r.get("_file", "")),
    )


def expand_with_noise(
    all_rows: List[Dict[str, Any]], signal_rows: List[Dict[str, Any]], t0: datetime, t1: datetime
) -> List[Dict[str, Any]]:
    """Attach same-window noise rows loosely matching instrument/run_id sets from signal_rows."""
    inst_s = inst_set(signal_rows)
    run_s = run_set(signal_rows)
    extra: List[Dict[str, Any]] = []
    for r in all_rows:
        if r["_event"] not in rea.NOISE_EVENTS:
            continue
        ts = r.get("_ts")
        if ts is None or ts < t0 or ts > t1:
            continue
        ri, rr = r.get("_instrument"), r.get("_run_id")
        if run_s and rr and rr not in run_s:
            continue
        if inst_s:
            if ri and ri not in inst_s:
                continue
        extra.append(r)
    return sort_rows(signal_rows + extra)


def raw_blob(o: Dict[str, Any]) -> str:
    try:
        return json.dumps(o, default=str).upper()
    except Exception:
        return str(o).upper()


def incident_stream_guess(rows: List[Dict[str, Any]]) -> str:
    for r in rows:
        o = r.get("raw") or {}
        if not isinstance(o, dict):
            continue
        data = o.get("data") if isinstance(o.get("data"), dict) else {}
        for k in ("stream", "StreamName", "log_stream", "engine_stream"):
            v = o.get(k) or data.get(k)
            if v and str(v).strip().lower() not in ("__engine__", "engine", ""):
                return str(v)[:120]
    return "—"


def first_qty_snapshot(rows: List[Dict[str, Any]]) -> Dict[str, Any]:
    q = rea.extract_qty_signals(rows)
    return q if q.get("fields") else {}


def forward_fill_instrument(rows: List[Dict[str, Any]]) -> None:
    """Mutates rows: sets `_ff_inst` from last seen non-empty instrument (engine lines often omit instrument)."""
    last = ""
    for r in rows:
        ins = (r.get("_instrument") or "").strip()
        if ins:
            last = ins
        r["_ff_inst"] = ins or last or "__UNK__"


def is_suspicious_false_resolution_episode(rows: List[Dict[str, Any]]) -> bool:
    """
    Within this merged incident window only: a resolution-like event occurs,
    then a regression-like event (evidence of pass/resolution then mismatch again).
    """
    seen_resolution = False
    for r in sort_rows(rows):
        e = r["_event"]
        if e in RESOLUTION_LIKE:
            seen_resolution = True
        elif e in REGRESSION_LIKE and seen_resolution:
            return True
    return False


def classify_incident_outcome(rows: List[Dict[str, Any]]) -> str:
    """Exactly one of CLEAN_TRANSIENT | RESOLVED_FORCED | UNRESOLVED | SUSPICIOUS_FALSE_RESOLUTION."""
    if is_suspicious_false_resolution_episode(rows):
        return "SUSPICIOUS_FALSE_RESOLUTION"
    evs = [r["_event"] for r in rows]
    evu = " ".join(evs).upper()
    blob = " ".join(raw_blob(r.get("raw") or {}) for r in rows)

    if "RECONCILIATION_FORCED_CONVERGENCE" in evs or "FORCED_CONVERGENCE" in blob:
        return "RESOLVED_FORCED"
    if "FAIL_CLOSED" in evu or "MISMATCH_FAIL_CLOSED" in evu:
        return "RESOLVED_FORCED"

    # Hard gate / flatten language in payload (evidence-only)
    if re.search(r"\bFLATTEN\b|\bEMERGENCY\b|HARD_GATE|STATE_CONSISTENCY_GATE", blob):
        if any(x in evs for x in ("RECONCILIATION_CONVERGED", "RECONCILIATION_RESOLVED", "JOURNAL_INTEGRITY_RECOVERED")):
            return "RESOLVED_FORCED"

    last_sig = None
    for r in reversed(rows):
        e = r["_event"]
        if e in rea.NOISE_EVENTS and last_sig is None:
            continue
        if e not in rea.NOISE_EVENTS:
            last_sig = e
            break
    if last_sig is None and rows:
        last_sig = rows[-1]["_event"]

    if last_sig in (
        "RECONCILIATION_QTY_MISMATCH_STILL_OPEN",
        "RECONCILIATION_STUCK",
    ):
        return "UNRESOLVED"
    if last_sig == "RECONCILIATION_QTY_MISMATCH":
        return "UNRESOLVED"

    if last_sig in ("RECONCILIATION_CONVERGED", "RECONCILIATION_RESOLVED", "RECONCILIATION_MISMATCH_CLEARED"):
        return "CLEAN_TRANSIENT"

    # Ambiguous tail events: compare last resolution vs last regression in time order
    ambiguous_tail = {
        "RECONCILIATION_ORDER_SOURCE_BREAKDOWN",
        "RECONCILIATION_CONTEXT",
        "RECOVERY_SCOPE_CLASSIFIED",
        "STARTUP_RECONCILIATION_REPORT",
        "RECONCILIATION_SECONDARY_INSTANCE_SKIPPED",
        "IEA_ADOPTION_SCAN_NO_PROGRESS_NOT_SKIPPED",
        "IEA_ADOPTION_SCAN_SKIPPED_NO_PROGRESS",
    }
    if last_sig in ambiguous_tail or last_sig is None:
        last_res_idx = -1
        last_reg_idx = -1
        for i, r in enumerate(rows):
            e = r["_event"]
            if e in RESOLUTION_LIKE:
                last_res_idx = i
            if e in REGRESSION_LIKE:
                last_reg_idx = i
        if last_res_idx >= 0 and last_res_idx > last_reg_idx:
            return "CLEAN_TRANSIENT"

    if "JOURNAL_INTEGRITY_RECOVERED" in evs and "RECONCILIATION_FORCED_CONVERGENCE" not in evs:
        ji_idxs = [i for i, r in enumerate(rows) if r["_event"] == "JOURNAL_INTEGRITY_RECOVERED"]
        last_ji = max(ji_idxs) if ji_idxs else -1
        reg_idxs = [i for i, r in enumerate(rows) if r["_event"] in REGRESSION_LIKE]
        last_reg = max(reg_idxs) if reg_idxs else -1
        if "RECONCILIATION_QTY_MISMATCH" in evs or "RECONCILIATION_STUCK" in evs:
            if last_ji > last_reg:
                return "CLEAN_TRANSIENT"
        else:
            return "CLEAN_TRANSIENT"

    # Startup / scope-only
    if set(evs) <= {
        "RECOVERY_SCOPE_CLASSIFIED",
        "STARTUP_RECONCILIATION_REPORT",
        "RECONCILIATION_ORDER_SOURCE_BREAKDOWN",
    } or (len(evs) <= 3 and "STARTUP_RECONCILIATION_REPORT" in evs):
        return "CLEAN_TRANSIENT"

    return "UNRESOLVED"


def journal_row_analysis(
    jr: Dict[str, Any],
    incidents: List[Dict[str, Any]],
    all_sorted: List[Dict[str, Any]],
) -> str:
    """primary_candidate | secondary_symptom | unclear"""
    ts = jr.get("_ts")
    if ts is None:
        return "unclear"
    inside = []
    for inc in incidents:
        t0, t1 = inc["t0"], inc["t1"]
        if t0 and t1 and t0 <= ts <= t1:
            inside.append(inc)
    in_incident = len(inside) > 0

    # nearest qty mismatch
    jidx = next((i for i, x in enumerate(all_sorted) if x is jr), None)
    if jidx is None:
        return "unclear"
    prev_mm = None
    next_mm = None
    for i in range(jidx - 1, -1, -1):
        if all_sorted[i]["_event"] in QTY_MISMATCH_EVENTS:
            prev_mm = all_sorted[i]
            break
    for i in range(jidx + 1, len(all_sorted)):
        if all_sorted[i]["_event"] in QTY_MISMATCH_EVENTS:
            next_mm = all_sorted[i]
            break

    evb = raw_blob(jr.get("raw") or {})

    churn_markers = sum(
        1
        for k in (
            "RECONCILIATION",
            "CYCLE",
            "INVARIANT",
            "ASSEMBLE",
        )
        if k in evb
    )

    if prev_mm is None and next_mm is None:
        if in_incident:
            return "primary_candidate"
        return "unclear"

    if prev_mm and ts and prev_mm.get("_ts"):
        dt = (ts - prev_mm["_ts"]).total_seconds()
        if 0 <= dt < 120:
            return "secondary_symptom"
    if next_mm and ts and next_mm.get("_ts"):
        dt = (next_mm["_ts"] - ts).total_seconds()
        if 0 <= dt < 120:
            return "secondary_symptom"

    if churn_markers >= 2 and in_incident:
        return "secondary_symptom"
    return "unclear"


def severity_score(inc: Dict[str, Any]) -> Tuple[int, int, int, int, int, int]:
    """Higher = worse. Returns tuple for sorting."""
    oc = inc["outcome_class"]
    order = {
        "UNRESOLVED": 4,
        "SUSPICIOUS_FALSE_RESOLUTION": 3,
        "RESOLVED_FORCED": 2,
        "CLEAN_TRANSIENT": 1,
    }
    o = order.get(oc, 0)
    rs = inc["rows"]
    qm = sum(
        1
        for r in rs
        if r["_event"] in ("RECONCILIATION_QTY_MISMATCH", "RECONCILIATION_QTY_MISMATCH_STILL_OPEN")
    )
    stuck = sum(1 for r in rs if r["_event"] == "RECONCILIATION_STUCK")
    multi = 1 if inc["event_count"] > 8 else 0
    ji = 1 if inc["journal_integrity_any"] else 0
    stall = 1 if inc["stall_gate_hits"] > 0 else 0
    esc = min(3, inc["distinct_event_types"])
    has_inst = 1 if inc.get("instruments") else 0
    # Weight journal integrity and qty/stuck heavily for “worst incident” ranking
    return (
        o,
        qm * 2 + stuck * 2 + ji * 3,
        multi + esc + stall,
        inc["stall_gate_hits"],
        has_inst,
        inc["event_count"],
    )


def key_event_chain(rows: List[Dict[str, Any]], max_events: int = 18) -> str:
    seen: List[str] = []
    for r in rows:
        e = r["_event"]
        if e in rea.NOISE_EVENTS:
            continue
        if not seen or seen[-1] != e:
            seen.append(e)
        if len(seen) >= max_events:
            break
    if len(seen) >= max_events:
        return " → ".join(seen) + " → …"
    return " → ".join(seen) if seen else "(no high-signal events)"


def root_cause_line(rows: List[Dict[str, Any]]) -> str:
    """Payload keyword buckets only — default unknown (no inference)."""
    blob = " ".join(raw_blob(r.get("raw") or {}) for r in rows[:400])
    low = blob.lower()
    if re.search(r"\bpartial[_ ]fill\b|\bpartialfill\b", low):
        return "transient fill timing"
    if re.search(r"\bprotective\b|\bprotection\b.*\b(order|sequence)\b|\boco\b|\bbracket\b", low):
        return "protective sequencing"
    if re.search(r"\bjournal[_ ]drift\b|\bjournal[_ ]lag\b|\bparity\b.*\b(fail|mismatch)\b|\breconstruct", low):
        return "journal drift"
    if re.search(r"\biea_adoption\b|\brecovery[_ ]adopt\b|\badoption[_ ]scan\b", low):
        return "recovery sequencing"
    if re.search(r"\bduplicate\b.*\bfill\b|\bexcess fill\b|\boverfill\b|\bdouble[_ ]fill\b", low):
        return "duplicate/excess fill behavior"
    return "unknown"


def load_incidents_for_day(
    log_dir: Path, td: str
) -> Tuple[List[Dict[str, Any]], List[Dict[str, Any]], List[Dict[str, Any]], int]:
    """
    Same incident model as this module’s main report: merged high-signal clusters + expanded noise.
    Returns (incidents, all_sorted, raw_rows, n_sig_clusters).
    """
    rows = rea.iter_reconciliation_lines(log_dir, td)
    signal_rows = [r for r in rows if r["_event"] not in rea.NOISE_EVENTS]
    sig_clusters = rea.cluster_incidents(signal_rows, gap_minutes=SIG_GAP_MINUTES)
    n_sig_clusters = len(sig_clusters)

    distinct = merge_signal_clusters(sig_clusters, MERGE_GAP_MINUTES)

    all_sorted = sort_rows(rows)
    forward_fill_instrument(all_sorted)

    incidents: List[Dict[str, Any]] = []
    for cl in distinct:
        t0, t1 = cluster_time_bounds(cl)
        if t0 is None:
            t0 = datetime.min.replace(tzinfo=timezone.utc)
        if t1 is None:
            t1 = t0
        expanded = expand_with_noise(rows, cl, t0, t1)

        evs = [r["_event"] for r in expanded]
        ji_any = any(e in JOURNAL_EVENTS for e in evs)
        stall_hits = sum(1 for r in expanded for s in STALL_GATE if s in r["_event"].upper())
        distinct_ev = len({e for e in evs if e not in rea.NOISE_EVENTS})

        oc = classify_incident_outcome(expanded)

        incidents.append(
            {
                "rows": expanded,
                "signal_rows": cl,
                "t0": t0,
                "t1": t1,
                "instruments": sorted(inst_set(cl)),
                "run_ids": sorted(run_set(cl)),
                "event_count": len(expanded),
                "outcome_class": oc,
                "journal_integrity_any": ji_any,
                "stall_gate_hits": stall_hits,
                "distinct_event_types": distinct_ev,
            }
        )

    return incidents, all_sorted, rows, n_sig_clusters


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--log-dir", type=str, default=None)
    ap.add_argument("--trading-date", type=str, required=True)
    ap.add_argument("--out", type=str, required=True)
    args = ap.parse_args()

    log_dir = rea.resolve_log_dir(args.log_dir)
    td = args.trading_date.strip()[:10]

    incidents, all_sorted, rows, n_sig_clusters = load_incidents_for_day(log_dir, td)

    # Journal integrity per-row
    ji_rows = [r for r in all_sorted if r["_event"] in JOURNAL_EVENTS]
    ji_primary = ji_secondary = ji_unclear = 0
    ji_detail: List[str] = []
    for jr in ji_rows:
        cat = journal_row_analysis(jr, incidents, all_sorted)
        if cat == "primary_candidate":
            ji_primary += 1
        elif cat == "secondary_symptom":
            ji_secondary += 1
        else:
            ji_unclear += 1
        ji_detail.append(cat)

    # Counts
    by_class: Dict[str, int] = defaultdict(int)
    for inc in incidents:
        by_class[inc["outcome_class"]] += 1

    def primary_instrument(inc: Dict[str, Any]) -> str:
        for r in inc.get("signal_rows") or []:
            if r.get("_instrument"):
                return str(r["_instrument"])
        return "__no_instrument__"

    by_inst: Dict[str, int] = defaultdict(int)
    for inc in incidents:
        by_inst[primary_instrument(inc)] += 1

    by_run: Dict[str, int] = defaultdict(int)
    for inc in incidents:
        for rid in inc["run_ids"] or ["__no_run_id__"]:
            by_run[rid] += 1

    # Top 20
    ranked = sorted(incidents, key=lambda x: severity_score(x), reverse=True)[:20]

    # Section 5 stats
    inst_mismatch_counts: Dict[str, int] = defaultdict(int)
    for r in all_sorted:
        if r["_event"] in ("RECONCILIATION_QTY_MISMATCH", "RECONCILIATION_QTY_MISMATCH_STILL_OPEN"):
            ins = r.get("_instrument") or "__no_instrument__"
            inst_mismatch_counts[ins] += 1

    stall_loops = sum(1 for inc in incidents if inc["stall_gate_hits"] >= 2)

    lines: List[str] = []
    lines.append("# Phase 2 forensic reduction — reconciliation audit\n\n")
    lines.append(f"- **Source logs:** `{log_dir.resolve()}`\n")
    lines.append(f"- **Trading date:** `{td}`\n")
    lines.append(
        f"- **Reference:** `docs/robot/audits/RECONCILIATION_DAY_AUDIT_AUTO.md` (broad extraction); "
        f"cluster count there (~600–650) varies with **log churn** and optional `--max-incidents`; "
        f"this run rescans **all** JSONL for `{td}`.\n"
    )
    lines.append(
        f"- **Method:** High-signal clusters (`gap>{SIG_GAP_MINUTES} min` or instrument change) = **{n_sig_clusters}**; "
        f"merged when same instrument set overlap, same run_id overlap where present, and inter-cluster gap ≤ **{MERGE_GAP_MINUTES} min**.\n\n"
    )

    lines.append("## SECTION 1 — Distinct incident count\n\n")
    lines.append(f"- **Total distinct incidents (merged):** **{len(incidents)}**\n")
    lines.append(f"- **High-signal clusters before merge:** **{n_sig_clusters}**\n\n")
    lines.append("### Distinct incidents by instrument\n\n")
    for k, v in sorted(by_inst.items(), key=lambda x: (-x[1], x[0])):
        lines.append(f"- `{k}`: **{v}**\n")
    lines.append("\n### Distinct incidents by run_id (incident may span multiple; counts are per-incident attribution)\n\n")
    for k, v in sorted(by_run.items(), key=lambda x: (-x[1], x[0]))[:40]:
        lines.append(f"- `{k[:16]}…` **{v}**\n" if len(str(k)) > 20 else f"- `{k}`: **{v}**\n")
    if len(by_run) > 40:
        lines.append(f"- … **{len(by_run) - 40}** more run_ids\n")

    lines.append("\n## SECTION 2 — Outcome classification\n\n")
    for c in ("CLEAN_TRANSIENT", "RESOLVED_FORCED", "UNRESOLVED", "SUSPICIOUS_FALSE_RESOLUTION"):
        lines.append(f"- **{c}:** **{by_class.get(c, 0)}**\n")
    lines.append(
        "\n**Evidence rules (event names + payload substrings only):** "
        "`SUSPICIOUS_FALSE_RESOLUTION` = within the **same merged incident**, a `PASS_SUMMARY` / `RESOLVED` / `CONVERGED` / "
        "`MISMATCH_CLEARED` / `JOURNAL_INTEGRITY_RECOVERED` line occurs **before** a later `RECONCILIATION_QTY_MISMATCH` / "
        "`STILL_OPEN` / `STUCK` in time order. "
        "`RESOLVED_FORCED` includes `RECONCILIATION_FORCED_CONVERGENCE`, fail-closed strings, or flatten/emergency/hard-gate language in payloads. "
        "`UNRESOLVED` = last non-noise event in the merged window is mismatch/stuck/still_open, or no clean resolution event.\n"
    )

    lines.append("\n## SECTION 3 — Journal integrity failure analysis\n\n")
    lines.append(
        f"- **JOURNAL_INTEGRITY_FAILED + RECONCILIATION_JOURNAL_INTEGRITY_FAILED row count:** **{len(ji_rows)}**\n"
    )
    lines.append(f"- **Primary cause candidates:** **{ji_primary}**\n")
    lines.append(f"- **Secondary / symptomatic (near qty mismatch or churn markers):** **{ji_secondary}**\n")
    lines.append(f"- **Unclear:** **{ji_unclear}**\n\n")
    lines.append(
        "**Triage rules:** `secondary_symptom` if a qty-mismatch-class event occurs within **120s** before/after the journal line "
        "on the global timeline, or churn markers with incident overlap; `primary_candidate` if inside an active incident window "
        "but no nearby mismatch; else `unclear`.\n"
    )

    lines.append("\n## SECTION 4 — Top 20 worst incidents\n\n")
    for rank, inc in enumerate(ranked, 1):
        rs = inc["rows"]
        qty = first_qty_snapshot(rs)
        oc = inc["outcome_class"]
        forced = any(r["_event"] == "RECONCILIATION_FORCED_CONVERGENCE" for r in rs) or (
            "FORCED_CONVERGENCE" in " ".join(raw_blob(r.get("raw") or {}) for r in rs)
        )
        if inc.get("instruments"):
            disp_inst = inc["instruments"]
        else:
            pi = primary_instrument(inc)
            disp_inst = "—" if pi == "__no_instrument__" else pi
        lines.append(f"### {rank}. Instrument `{disp_inst}`\n\n")
        lines.append(f"- **Stream:** {incident_stream_guess(rs)}\n")
        lines.append(f"- **run_id(s):** {inc['run_ids'] or '—'}\n")
        lines.append(f"- **start_ts_utc:** `{inc['t0'].isoformat() if inc['t0'] else '—'}`\n")
        lines.append(f"- **end_ts_utc:** `{inc['t1'].isoformat() if inc['t1'] else '—'}`\n")
        lines.append(f"- **First qty fields (if present):** `{qty}`\n")
        lines.append(f"- **Key event chain (noise-stripped, de-duped):** {key_event_chain(rs)}\n")
        lines.append(f"- **Journal integrity failed in window:** {'yes' if inc['journal_integrity_any'] else 'no'}\n")
        lines.append(f"- **Forced action (event or payload):** {'yes' if forced else 'no'}\n")
        lines.append(f"- **Classification:** `{oc}`\n")
        lines.append(f"- **One-sentence root-cause bucket:** {root_cause_line(rs)}\n\n")

    lines.append("## SECTION 5 — Systemic pattern test\n\n")
    top_mech = sorted(by_class.items(), key=lambda x: -x[1])[0][0] if by_class else "n/a"
    lines.append(
        f"1. **Mechanism clustering:** Outcome totals by class: {dict(by_class)} — "
        f"largest class **`{top_mech}`** (evidence: per-incident classification above).\n"
    )
    lines.append(
        f"2. **Journal integrity driver vs symptom:** **{ji_secondary}** / **{len(ji_rows)}** rows classified secondary/symptomatic vs "
        f"**{ji_primary}** primary-candidate (evidence: §3 triage on timed adjacency to qty-mismatch events).\n"
    )
    cleanish = by_class.get("CLEAN_TRANSIENT", 0) + by_class.get("RESOLVED_FORCED", 0)
    lines.append(
        f"3. **Qty mismatches resolving cleanly:** **{by_class.get('CLEAN_TRANSIENT', 0)}** `CLEAN_TRANSIENT` vs "
        f"**{by_class.get('UNRESOLVED', 0)}** `UNRESOLVED` + **{by_class.get('SUSPICIOUS_FALSE_RESOLUTION', 0)}** suspicious "
        f"(evidence: §2 counts; `RESOLVED_FORCED` = **{by_class.get('RESOLVED_FORCED', 0)}**).\n"
    )
    lines.append(
        f"4. **Stall-loop evidence:** **{stall_loops}** merged incidents with ≥2 stall/gate/no-progress substring hits in expanded window "
        f"(evidence: event names in `STALL_GATE` list).\n"
    )
    lines.append(
        f"5. **Early success:** **{by_class.get('SUSPICIOUS_FALSE_RESOLUTION', 0)}** merged incidents with resolution-class then "
        f"regression-class ordering in-window (evidence: §2 rule).\n"
    )
    top_inst = sorted(inst_mismatch_counts.items(), key=lambda x: -x[1])[:5]
    lines.append(
        f"6. **Instrument concentration (qty mismatch event lines):** "
        f"{', '.join(f'`{k}`={v}' for k, v in top_inst) or 'n/a'} "
        f"(evidence: raw line counts for `RECONCILIATION_QTY_MISMATCH` / `STILL_OPEN`).\n"
    )

    lines.append("\n## SECTION 6 — Executive summary\n\n")
    total = len(incidents) or 1
    unr = by_class.get("UNRESOLVED", 0) + by_class.get("SUSPICIOUS_FALSE_RESOLUTION", 0)
    if unr / total < 0.15 and by_class.get("CLEAN_TRANSIENT", 0) / total > 0.5:
        health = "mostly healthy-but-noisy"
    elif unr / total > 0.35:
        health = "structurally unstable"
    else:
        health = "mixed"
    lines.append(
        f"- **Day characterization:** **{health}** — derived from outcome mix: "
        f"UNRESOLVED+SUSPICIOUS = **{unr}** / **{total}**, CLEAN_TRANSIENT = **{by_class.get('CLEAN_TRANSIENT', 0)}**.\n"
    )
    lines.append(
        "- **Top 3 engineering attention items (evidence-only):**\n"
        f"  1. Journal integrity row volume (**{len(ji_rows)}**) vs secondary classification (**{ji_secondary}** symptomatic) — see §3.\n"
        f"  2. Forced or hard interventions: **{by_class.get('RESOLVED_FORCED', 0)}** `RESOLVED_FORCED` incidents (§2).\n"
        f"  3. Unresolved / suspicious outcomes: **{by_class.get('UNRESOLVED', 0)}** + **{by_class.get('SUSPICIOUS_FALSE_RESOLUTION', 0)}** (§2, §4).\n"
    )
    lines.append(
        "- **Audit next before reconciliation logic changes:** Payload-level review of "
        "`RECONCILIATION_QTY_MISMATCH` / `RECONCILIATION_STUCK` / `RECONCILIATION_FORCED_CONVERGENCE` "
        "lines for the top instruments above; confirm stall-loop hypothesis with gate telemetry fields; "
        "cross-check `SUSPICIOUS_FALSE_RESOLUTION` cases against order/fill IDs in non-JSONL sources if available.\n"
    )

    outp = Path(args.out)
    outp.write_text("".join(lines), encoding="utf-8")
    print(f"Wrote {outp.resolve()} incidents={len(incidents)} ji_rows={len(ji_rows)}", flush=True)


if __name__ == "__main__":
    main()
