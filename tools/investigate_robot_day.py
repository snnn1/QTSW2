#!/usr/bin/env python3
"""
Deterministic investigation runner for robot JSONL + watchdog feed (post-fix validation).

Outputs a structured markdown report under reports/ by default.

Usage (from repo root):
  python tools/investigate_robot_day.py --date 2026-03-25 --tz chicago
  python tools/investigate_robot_day.py --date 2026-03-25 --md-out reports/investigation_2026-03-25.md
  python tools/investigate_robot_day.py --date 2026-03-24 --compare-date 2026-03-25 \\
      --compare-md-out reports/investigation_compare_2026-03-24_vs_2026-03-25.md
"""

from __future__ import annotations

import argparse
import bisect
import json
import os
import re
from collections import Counter, defaultdict
from datetime import date, datetime, timedelta, timezone
from pathlib import Path
from typing import Any, Dict, List, Optional, Set, Tuple

try:
    from zoneinfo import ZoneInfo
except Exception:
    ZoneInfo = None  # type: ignore

from daily_audit import (
    collect_robot_jsonl_paths,
    collect_watchdog_paths,
    day_window_utc,
    ingest_jsonl_file,
    iter_jsonl,
    load_thresholds,
    parse_audit_date,
    resolve_project_root,
    watchdog_crosscheck,
)
from log_audit import NormEvent, normalize_event, parse_ts, resolve_log_dir as resolve_robot_log_dir


# --- Normalization helpers (deterministic) ---

def _inst(e: NormEvent) -> str:
    d = e.data if isinstance(e.data, dict) else {}
    return str(e.instrument or d.get("instrument") or d.get("Instrument") or "").strip()


def _stream_key(e: NormEvent) -> str:
    d = e.data if isinstance(e.data, dict) else {}
    for k in ("stream", "stream_id", "Stream", "streamId"):
        v = d.get(k)
        if v is not None and str(v).strip():
            return str(v).strip()
    return ""


def _extract_reason(e: NormEvent) -> str:
    d = e.data or {}
    for k in (
        "reason",
        "RejectionReason",
        "rejection_reason",
        "failure_reason",
        "error",
        "message",
    ):
        v = d.get(k)
        if v:
            return str(v)[:500]
    if e.message and e.message != e.event:
        return str(e.message)[:500]
    return ""


def _event_data_subset(e: NormEvent, max_keys: int = 80) -> Dict[str, Any]:
    d = e.data if isinstance(e.data, dict) else {}
    out: Dict[str, Any] = {}
    for i, k in enumerate(sorted(d.keys())):
        if i >= max_keys:
            out["_truncated"] = True
            break
        try:
            json.dumps(d[k])
            out[k] = d[k]
        except Exception:
            out[k] = str(d[k])[:200]
    return out


def max_gap_minutes(ts: List[datetime]) -> Tuple[float, Optional[datetime], Optional[datetime]]:
    if len(ts) < 2:
        return 0.0, None, None
    ts = sorted(ts)
    mx = 0.0
    pair: Tuple[Optional[datetime], Optional[datetime]] = (ts[0], ts[1])
    for a, b in zip(ts, ts[1:]):
        g = (b - a).total_seconds() / 60.0
        if g > mx:
            mx = g
            pair = (a, b)
    return mx, pair[0], pair[1]


def scan_journals(project_root: Path, audit_date: date) -> List[Dict[str, Any]]:
    root = project_root / "data" / "execution_journals"
    if not root.is_dir():
        return []
    pat = re.compile(rf"^{re.escape(str(audit_date))}_")
    rows: List[Dict[str, Any]] = []
    for p in root.iterdir():
        if not p.is_file() or p.suffix.lower() != ".json":
            continue
        if not pat.match(p.name):
            continue
        try:
            obj = json.loads(p.read_text(encoding="utf-8", errors="replace"))
        except Exception:
            continue
        rows.append(
            {
                "file": p.name,
                "Rejected": obj.get("Rejected"),
                "RejectionReason": (obj.get("RejectionReason") or "")[:300],
                "RejectedAt": obj.get("RejectedAt"),
                "EntrySubmitted": obj.get("EntrySubmitted"),
            }
        )
    return rows


def bucket_counts(
    events: List[NormEvent], day_start: datetime, day_end: datetime, bucket_s: int = 5
) -> List[Tuple[datetime, int, Counter]]:
    buckets: Dict[int, Counter] = defaultdict(Counter)
    t0 = int(day_start.timestamp())
    for e in events:
        ts = int(e.ts_utc.timestamp())
        if ts < int(day_start.timestamp()) or ts >= int(day_end.timestamp()):
            continue
        b = (ts - t0) // bucket_s
        buckets[b][e.event or ""] += 1
    out: List[Tuple[datetime, int, Counter]] = []
    for b in sorted(buckets.keys()):
        c = buckets[b]
        start = datetime.fromtimestamp(t0 + b * bucket_s, tz=timezone.utc)
        out.append((start, sum(c.values()), c))
    out.sort(key=lambda x: -x[1])
    return out


def _is_order_submit_success(e: NormEvent) -> bool:
    ev = e.event or ""
    return ev == "ORDER_SUBMIT_SUCCESS" or "ORDER_SUBMIT_SUCCESS" in ev


def _is_order_submit_fail(e: NormEvent) -> bool:
    ev = e.event or ""
    return ev == "ORDER_SUBMIT_FAIL" or "ORDER_SUBMIT_FAIL" in ev


def _sim_verification_hit(reason: str, event_name: str) -> bool:
    u = (reason + " " + event_name).upper()
    keys = (
        "SIM_ACCOUNT_NOT_VERIFIED",
        "SIM ACCOUNT",
        "NOT SIM",
        "NON-SIM",
        "NONSIM",
        "SIM VERIFICATION",
        "SIM_VERIFICATION",
        "WRONG_ACCOUNT",
        "LIVE_ACCOUNT",
        "LIVE MODE",
    )
    return any(k in u for k in keys)


def execution_truth_section(
    events: List[NormEvent],
    journal_rows: List[Dict[str, Any]],
) -> Tuple[str, Dict[str, Any]]:
    ok = [e for e in events if _is_order_submit_success(e)]
    fail = [e for e in events if _is_order_submit_fail(e)]
    fail_reasons = Counter((_extract_reason(e) or "(empty)") for e in fail)

    # Per (instrument, stream)
    ok_ct: Dict[Tuple[str, str], int] = defaultdict(int)
    fail_ct: Dict[Tuple[str, str], int] = defaultdict(int)
    fail_reason_by_bucket: Dict[Tuple[str, str], Counter] = defaultdict(Counter)
    for e in ok:
        ok_ct[(_inst(e), _stream_key(e) or "(none)")] += 1
    for e in fail:
        k = (_inst(e), _stream_key(e) or "(none)")
        fail_ct[k] += 1
        fail_reason_by_bucket[k][_extract_reason(e) or "(empty)"] += 1

    all_keys = sorted(set(ok_ct.keys()) | set(fail_ct.keys()))
    first_fail_ts: Optional[datetime] = min((e.ts_utc for e in fail), default=None)  # type: ignore
    first_ok_ts: Optional[datetime] = min((e.ts_utc for e in ok), default=None)  # type: ignore

    total_sub = len(ok) + len(fail)
    ratio = (len(ok) / total_sub) if total_sub else None

    # Journals
    j_rej = [r for r in journal_rows if r.get("Rejected")]
    j_ok = [r for r in journal_rows if not r.get("Rejected")]
    j_missing = []  # no explicit state
    for r in journal_rows:
        if r.get("Rejected") is None and not r.get("EntrySubmitted"):
            j_missing.append(r)

    sim_fail = False
    for e in fail:
        if _sim_verification_hit(_extract_reason(e), e.event or ""):
            sim_fail = True
            break
    if not sim_fail:
        for r in j_rej:
            rr = str(r.get("RejectionReason") or "")
            if _sim_verification_hit(rr, ""):
                sim_fail = True
                break
    if not sim_fail:
        for e in events:
            if _sim_verification_hit("", e.event or ""):
                sim_fail = True
                break

    flags = {
        "SIM_VERIFICATION_FAILURE_PRESENT": sim_fail,
        "first_failure_ts_utc": first_fail_ts.isoformat() if first_fail_ts else None,
        "first_success_ts_utc": first_ok_ts.isoformat() if first_ok_ts else None,
        "submit_success": len(ok),
        "submit_fail": len(fail),
        "submit_ratio_success_over_total": round(ratio, 6) if ratio is not None else None,
        "journal_accepted": len(j_ok),
        "journal_rejected": len(j_rej),
        "journal_missing_state": len(j_missing),
    }

    lines: List[str] = []
    lines.append("## Execution Truth (post-fix validation)")
    lines.append("")
    lines.append(f"- **SIM_VERIFICATION_FAILURE_PRESENT:** `{flags['SIM_VERIFICATION_FAILURE_PRESENT']}`")
    lines.append(
        f"- **Submit outcomes:** success={len(ok)}, fail={len(fail)}, ratio(success/total)={flags['submit_ratio_success_over_total']}"
    )
    lines.append(
        f"- **First ORDER_SUBMIT_SUCCESS:** `{flags['first_success_ts_utc']}`  ·  **First ORDER_SUBMIT_FAIL:** `{flags['first_failure_ts_utc']}`"
    )
    lines.append(
        f"- **Execution journals:** accepted={len(j_ok)}, rejected={len(j_rej)}, ambiguous/missing flags={len(j_missing)} (same-day files only)"
    )
    lines.append("")
    lines.append("### Grouped rejection reasons (ORDER_SUBMIT_FAIL)")
    for reason, n in fail_reasons.most_common(25):
        lines.append(f"- {n}× `{reason[:220]}`")
    lines.append("")
    lines.append("### Execution summary by instrument × stream")
    lines.append("")
    lines.append("| instrument | stream | success | fail | top rejection_reason |")
    lines.append("|-----------|--------|--------:|-----:|----------------------|")
    for ins, st in all_keys:
        rc = fail_reason_by_bucket.get((ins, st), Counter()).most_common(1)
        top_r = rc[0][0][:80] if rc else ""
        lines.append(
            f"| `{ins or '(empty)'}` | `{st}` | {ok_ct.get((ins,st),0)} | {fail_ct.get((ins,st),0)} | `{top_r}` |"
        )
    lines.append("")
    return "\n".join(lines), flags


_RECON_TYPES = ("RECONCILIATION_QTY_MISMATCH", "RECONCILIATION_QTY_MISMATCH_STILL_OPEN")


def _recon_row(e: NormEvent) -> Dict[str, Any]:
    d = e.data if isinstance(e.data, dict) else {}
    inst = _inst(e)
    stream = _stream_key(e) or str(d.get("stream") or "")
    intent = str(d.get("intent_id") or d.get("intentId") or "")
    exp_q = d.get("expected_position_qty")
    act_q = d.get("actual_position_qty")
    if exp_q is None:
        exp_q = d.get("journal_qty")
    if act_q is None:
        act_q = d.get("account_qty")
    exp_o = d.get("expected_orders")
    act_o = d.get("actual_orders")
    mtype = d.get("mismatch_type") or d.get("mismatch_taxonomy") or d.get("drift_class") or ""
    note = str(d.get("note") or "")
    state = "open" if e.event == "RECONCILIATION_QTY_MISMATCH_STILL_OPEN" else (
        "persistent" if "persistent" in note.lower() or "still_open" in (e.event or "").lower() else "fail_closed"
    )
    if e.event == "RECONCILIATION_QTY_MISMATCH_STILL_OPEN":
        state = "still_open"
    return {
        "instrument": inst,
        "stream": stream,
        "intent_id": intent,
        "expected_position_qty": exp_q,
        "actual_position_qty": act_q,
        "expected_orders": exp_o,
        "actual_orders": act_o,
        "mismatch_type": mtype,
        "state": state,
        "ts_utc": e.ts_utc.isoformat(),
        "event": e.event,
        "_full": _event_data_subset(e, max_keys=120),
    }


def reconciliation_truth_section(events: List[NormEvent], sample_per_bucket: int = 5) -> str:
    rows = [e for e in events if (e.event or "") in _RECON_TYPES]
    parsed = [_recon_row(e) for e in rows]
    by_type = Counter(r["event"] for r in parsed)
    first_by_stream: Dict[str, str] = {}
    for r in sorted(parsed, key=lambda x: x["ts_utc"]):
        key = f'{r["instrument"]}|{r["stream"] or "(none)"}'
        if key not in first_by_stream:
            first_by_stream[key] = r["ts_utc"]

    transient_vs_persist = Counter()
    for r in parsed:
        transient_vs_persist[r["state"]] += 1

    # sample per instrument/stream
    buckets: Dict[Tuple[str, str], List[Dict[str, Any]]] = defaultdict(list)
    for r in parsed:
        buckets[(str(r["instrument"]), str(r["stream"] or "(none)"))].append(r)

    lines: List[str] = []
    lines.append("## Reconciliation Truth")
    lines.append("")
    lines.append(f"- **Events:** {dict(by_type)}")
    lines.append(f"- **Count by state heuristic:** {dict(transient_vs_persist)}")
    lines.append("")
    lines.append("### First occurrence per stream key (instrument|stream)")
    for k, ts in sorted(first_by_stream.items(), key=lambda x: x[0]):
        lines.append(f"- `{k}` → `{ts}`")
    lines.append("")
    lines.append("### Mismatch table (sampled full payloads per instrument × stream)")
    lines.append("")
    lines.append("| instrument | stream | intent_id | expected | actual | state | type | ts_utc |")
    lines.append("|-----------|--------|-----------|----------|--------|-------|------|--------|")
    for (ins, st) in sorted(buckets.keys()):
        for r in buckets[(ins, st)][:sample_per_bucket]:
            lines.append(
                "| `{0}` | `{1}` | `{2}` | `{3}` | `{4}` | `{5}` | `{6}` | `{7}` |".format(
                    ins,
                    st,
                    r.get("intent_id", "")[:32],
                    r.get("expected_position_qty"),
                    r.get("actual_position_qty"),
                    r.get("state"),
                    str(r.get("mismatch_type", ""))[:40],
                    r.get("ts_utc"),
                )
            )
    lines.append("")
    lines.append("### Full payload samples (first 3 events, any bucket)")
    for r in parsed[:3]:
        lines.append(f"#### `{r['event']}` @ `{r['ts_utc']}`")
        lines.append("")
        lines.append("```json")
        lines.append(json.dumps(r["_full"], indent=2, default=str))
        lines.append("```")
        lines.append("")
    return "\n".join(lines)


_PROTECTIVE_TRIGGERS = {
    "PROTECTIVE_MISSING_STOP",
    "PROTECTIVE_ESCALATE_TO_FLATTEN",
    "PROTECTIVE_EMERGENCY_FLATTEN_TRIGGERED",
}


def _intent_from_event(e: NormEvent) -> str:
    d = e.data if isinstance(e.data, dict) else {}
    for k in ("intent_id", "intentId", "IntentId"):
        v = d.get(k)
        if v is not None and str(v).strip():
            return str(v).strip()
    return ""


def _data_upper(e: NormEvent) -> str:
    try:
        return json.dumps(e.data if isinstance(e.data, dict) else {}, default=str).upper()
    except Exception:
        return ""


def _reconstruct_protective_episode(
    trigger: NormEvent,
    ev_sorted: List[NormEvent],
    ts_list: List[datetime],
    window_sec: int = 900,
) -> Dict[str, Any]:
    """
    Evidence-backed protective episode reconstruction (deterministic).
    Returns causal bucket + structured fields for the report.
    """
    inst = _inst(trigger)
    t0 = trigger.ts_utc - timedelta(seconds=window_sec)
    lo = bisect.bisect_left(ts_list, t0)
    hi = bisect.bisect_right(ts_list, trigger.ts_utc)
    window = [ev_sorted[i] for i in range(lo, hi)]
    if inst:
        window = [
            e
            for e in window
            if (not _inst(e)) or _inst(e).upper() == inst.upper()
        ]

    evidence: List[str] = []
    nearest_intent = _intent_from_event(trigger)
    if nearest_intent:
        evidence.append(f"trigger payload intent_id={nearest_intent}")
    for e in reversed(window):
        iid = _intent_from_event(e)
        if iid:
            if not nearest_intent:
                nearest_intent = iid
            evidence.append(f"nearest intent trace: `{e.event}` @ {e.ts_utc.isoformat()} intent={iid}")
            break
    if not any("nearest intent trace" in x for x in evidence):
        evidence.append("no prior intent_id in window (check trigger-only payload)")

    tag_seen: Set[str] = set()
    for e in window:
        ev = e.event or ""
        du = _data_upper(e)
        if _is_order_submit_fail(e) and ("ENTRY" in du or "entry" in _extract_reason(e).lower()):
            tag_seen.add("entry_reject_sig")
            evidence.append(f"entry-side submit fail `{ev}` @ {e.ts_utc.isoformat()}")
        if ev in ("ENTRY_FILLED", "REENTRY_FILLED") or "ENTRY_FILLED" in ev:
            tag_seen.add("entry_filled")
            evidence.append(f"entry filled `{ev}` @ {e.ts_utc.isoformat()}")
        if ev == "EXECUTION_FILLED" or (ev and "EXECUTION_FILLED" in ev):
            tag_seen.add("execution_filled")
            d = e.data if isinstance(e.data, dict) else {}
            fq = d.get("fill_quantity") or d.get("filled_quantity") or d.get("quantity") or d.get("qty")
            if fq not in (None, 0, "0"):
                tag_seen.add("partial_or_full_fill")
                evidence.append(f"execution fill qty={fq} @ {e.ts_utc.isoformat()}")
        if ev == "ORDER_SUBMITTED" and "ENTRY" in du:
            tag_seen.add("entry_submitted")
        if _is_order_submit_success(e) and "ENTRY" in du:
            tag_seen.add("entry_accepted")
        if "WORKING" in ev or ("WORKING" in du and "ORDER" in ev):
            tag_seen.add("working")

    entry_state_before_episode = "unknown"
    if "entry_reject_sig" in tag_seen and "entry_filled" not in tag_seen:
        entry_state_before_episode = "rejected"
    elif "entry_filled" in tag_seen:
        entry_state_before_episode = "filled"
    elif "partial_or_full_fill" in tag_seen:
        entry_state_before_episode = "partially_filled"
    elif "working" in tag_seen:
        entry_state_before_episode = "working"
    elif "entry_accepted" in tag_seen:
        entry_state_before_episode = "accepted"
    elif "entry_submitted" in tag_seen:
        entry_state_before_episode = "submitted"

    stop_was_confirmed: Optional[bool] = None
    target_was_confirmed: Optional[bool] = None
    for e in window:
        du = _data_upper(e)
        ev = e.event or ""
        if not _is_order_submit_success(e):
            continue
        if "STOP" in du or "PROTECTIVE_STOP" in du or "STOP" in ev:
            stop_was_confirmed = True
            evidence.append(f"protective stop submit success @ {e.ts_utc.isoformat()}")
        if "TARGET" in du or "PROTECTIVE_TARGET" in du or "LIMIT" in du:
            target_was_confirmed = True
            evidence.append(f"target submit success @ {e.ts_utc.isoformat()}")
    if stop_was_confirmed is None:
        stop_was_confirmed = False
    if target_was_confirmed is None:
        target_was_confirmed = False

    reconciliation_already_open = False
    journal_qty: Any = None
    account_qty: Any = None
    for e in window:
        evn = e.event or ""
        if evn in _RECON_TYPES or "RECONCILIATION_QTY_MISMATCH" in evn:
            reconciliation_already_open = True
            d = e.data if isinstance(e.data, dict) else {}
            if journal_qty is None and d.get("journal_qty") is not None:
                journal_qty = d.get("journal_qty")
            if account_qty is None and d.get("account_qty") is not None:
                account_qty = d.get("account_qty")
            evidence.append(
                f"reconciliation signal `{evn}` journal_qty={d.get('journal_qty')} account_qty={d.get('account_qty')}"
            )

    journal_account_matched: Optional[bool] = None
    if journal_qty is not None and account_qty is not None:
        try:
            journal_account_matched = int(journal_qty) == int(account_qty)
        except Exception:
            journal_account_matched = None

    real_position_likely: Optional[bool] = None
    flatten_target_kind = "unknown"
    try:
        aq_int = int(account_qty) if account_qty is not None else None
        jq_int = int(journal_qty) if journal_qty is not None else None
    except Exception:
        aq_int = jq_int = None
    if aq_int is not None:
        real_position_likely = abs(aq_int) > 0
    if real_position_likely is None:
        for e in reversed(window):
            if (e.event or "") in ("POSITION_DRIFT_DETECTED", "EXPOSURE_INTEGRITY_VIOLATION"):
                real_position_likely = True
                evidence.append(f"exposure/drift `{e.event}` implies broker-side signal")
                break

    tev = trigger.event or ""
    if "FLATTEN" in tev or "FLATTEN" in _data_upper(trigger):
        if aq_int is not None and jq_int is not None:
            if aq_int != 0 and jq_int == 0:
                flatten_target_kind = "broker_position_real_journal_flat"
            elif aq_int == jq_int and aq_int != 0:
                flatten_target_kind = "aligned_open_exposure"
            elif aq_int != jq_int:
                flatten_target_kind = "stale_believed_exposure_or_split"
            else:
                flatten_target_kind = "flat_both_sides"
        else:
            flatten_target_kind = "insufficient_recon_snapshot"

    entry_filled = "entry_filled" in tag_seen
    entry_reject = "entry_reject_sig" in tag_seen and not entry_filled
    recon_mismatch = reconciliation_already_open
    position_hint = real_position_likely is True or "execution_filled" in tag_seen

    causal = "UNKNOWN"
    if entry_reject:
        causal = "UPSTREAM_REJECTED_ENTRY"
    elif entry_filled and not stop_was_confirmed:
        causal = "STOP_SUBMIT_FAILED"
    elif recon_mismatch and entry_filled:
        causal = "STALE_RUNTIME_POSITION"
    elif entry_filled and position_hint and not stop_was_confirmed:
        causal = "POSITION_REAL_STOP_LOST"
    elif not entry_filled and position_hint:
        causal = "BROKER_ORPHAN"

    signal_count = sum(
        1
        for x in (
            nearest_intent,
            entry_state_before_episode != "unknown",
            stop_was_confirmed,
            target_was_confirmed,
            reconciliation_already_open,
            journal_qty is not None,
            account_qty is not None,
            journal_account_matched is not None,
            real_position_likely is not None,
        )
        if x
    )
    if signal_count >= 6:
        classification_confidence = "HIGH"
    elif signal_count >= 3:
        classification_confidence = "MEDIUM"
    else:
        classification_confidence = "LOW"

    return {
        "causal_bucket": causal,
        "nearest_intent_id": nearest_intent or "",
        "entry_state_before_episode": entry_state_before_episode,
        "stop_was_confirmed": stop_was_confirmed,
        "target_was_confirmed": target_was_confirmed,
        "reconciliation_already_open": reconciliation_already_open,
        "journal_qty": journal_qty,
        "account_qty": account_qty,
        "journal_account_matched_at_recon": journal_account_matched,
        "real_position_likely": real_position_likely,
        "flatten_target_kind": flatten_target_kind,
        "classification_confidence": classification_confidence,
        "evidence_chain": evidence[:40],
    }


def protective_classification_section(events: List[NormEvent], max_episode_rows: int = 35) -> Tuple[str, Counter]:
    ev_sorted = sorted(events, key=lambda e: e.ts_utc)
    ts_list = [e.ts_utc for e in ev_sorted]
    triggers = [e for e in ev_sorted if (e.event or "") in _PROTECTIVE_TRIGGERS]
    episodes: List[Tuple[NormEvent, Dict[str, Any]]] = []
    summary = Counter()
    for tr in triggers:
        ep = _reconstruct_protective_episode(tr, ev_sorted, ts_list)
        summary[ep["causal_bucket"]] += 1
        episodes.append((tr, ep))

    lines: List[str] = []
    lines.append("## Protective Episode Classification (evidence-backed)")
    lines.append("")
    lines.append(
        f"- **Episodes:** {len(triggers)} triggers, lookback **900s**, instrument-scoped when possible."
    )
    lines.append("")
    lines.append("| causal_bucket | count |")
    lines.append("|--------------|------:|")
    for name, n in summary.most_common():
        lines.append(f"| `{name}` | {n} |")
    lines.append("")
    lines.append("### Episode detail (sample)")
    lines.append("")
    lines.append(
        "| ts_utc | trigger | instrument | causal | conf | entry_state | stop_ok | tgt_ok | recon_open | jq | aq | jq==aq | real_pos | flatten_kind | intent |"
    )
    lines.append(
        "|--------|---------|------------|--------|------|-------------|---------|--------|------------|----|----|--------|----------|--------------|--------|"
    )
    for tr, ep in episodes[:max_episode_rows]:
        jm = ep["journal_account_matched_at_recon"]
        lines.append(
            "| `{0}` | `{1}` | `{2}` | `{3}` | `{4}` | `{5}` | `{6}` | `{7}` | `{8}` | `{9}` | `{10}` | `{11}` | `{12}` | `{13}` | `{14}` |".format(
                tr.ts_utc.isoformat(),
                tr.event or "",
                _inst(tr) or "(empty)",
                ep["causal_bucket"],
                ep["classification_confidence"],
                ep["entry_state_before_episode"],
                ep["stop_was_confirmed"],
                ep["target_was_confirmed"],
                ep["reconciliation_already_open"],
                ep["journal_qty"],
                ep["account_qty"],
                jm,
                ep["real_position_likely"],
                ep["flatten_target_kind"],
                (ep["nearest_intent_id"] or "")[:20],
            )
        )
    lines.append("")
    lines.append("### Evidence chains (first 5 episodes)")
    for idx, (tr, ep) in enumerate(episodes[:5], 1):
        lines.append(f"#### Episode {idx}: `{tr.event}` @ `{tr.ts_utc.isoformat()}` inst=`{_inst(tr)}`")
        for ex in ep["evidence_chain"]:
            lines.append(f"- {ex}")
        lines.append("")
    return "\n".join(lines), summary


def _storm_event_bucket(ev: str) -> Optional[str]:
    if ev.startswith("BAR_ADMISSION_"):
        return ev
    for st in ("BAR_RECEIVED_NO_STREAMS", "DATA_FEED_OUTSIDE_WINDOW"):
        if ev == st or st in ev:
            return st
    return None


def _analyze_storm_family(
    ev_name: str,
    family_events: List[NormEvent],
    total_day_events: int,
    global_peak_rate: float,
) -> Dict[str, str]:
    """Compressibility / CPU hints for one storm event family."""
    n = len(family_events)
    if n == 0:
        return {
            "count": "0",
            "peak_rate": "0",
            "repetition_pattern": "N_A",
            "compressibility": "N_A",
            "cpu_significance_guess": "N_A",
        }

    ts_sorted = sorted(e.ts_utc for e in family_events)
    by_sec = Counter(int(t.timestamp()) for t in ts_sorted)
    burst_secs = sum(1 for c in by_sec.values() if c >= 3)
    dup_collisions = sum(1 for c in by_sec.values() if c > 1)
    dup_ratio = dup_collisions / max(len(by_sec), 1)

    deltas: List[float] = []
    for i in range(1, len(ts_sorted)):
        deltas.append((ts_sorted[i] - ts_sorted[i - 1]).total_seconds())
    med_delta = sorted(deltas)[len(deltas) // 2] if deltas else 0.0

    by_inst = Counter(_inst(e) or "(none)" for e in family_events)
    top_inst_share = by_inst.most_common(1)[0][1] / n if by_inst else 0.0

    per_bar = dup_ratio >= 0.25 or (25.0 <= med_delta <= 95.0 and top_inst_share >= 0.5)
    state_trans = (
        ev_name.startswith("BAR_ADMISSION_")
        and dup_ratio < 0.2
        and med_delta > 2.0
    ) or (not per_bar and dup_ratio < 0.18 and med_delta > 1.0)

    if per_bar:
        repetition_pattern = "PER_BAR_REPETITIVE"
    elif state_trans:
        repetition_pattern = "STATE_TRANSITION_BASED"
    elif dup_ratio >= 0.15 or burst_secs >= max(3, n // 50):
        repetition_pattern = "BURSTY_OR_COLLIDING_TIMESTAMPS"
    else:
        repetition_pattern = "MIXED_OR_IRREGULAR"

    likely_compress = per_bar or dup_ratio >= 0.2 or repetition_pattern == "BURSTY_OR_COLLIDING_TIMESTAMPS"
    compressibility = "LIKELY_COMPRESSIBLE" if likely_compress else "LOW_REDUNDANCY"

    share = n / max(total_day_events, 1)
    cpu_hit = (n > 8000 and global_peak_rate > 15.0) or (share > 0.05 and not likely_compress and n > 5000)
    cpu_guess = "POSSIBLY_CPU_SIGNIFICANT" if cpu_hit else "PROBABLY_LOG_NOISE_DOMINANT"

    fam_peak = max(by_sec.values()) if by_sec else 0
    return {
        "count": str(n),
        "peak_rate": f"{fam_peak:.1f} evt/s (max same-second burst)",
        "repetition_pattern": repetition_pattern,
        "compressibility": compressibility,
        "cpu_significance_guess": cpu_guess,
    }


def storm_analysis_section(events: List[NormEvent], day_start: datetime, day_end: datetime) -> str:
    total = max(len(events), 1)
    dense = bucket_counts(events, day_start, day_end, bucket_s=5)
    global_peak_rate = 0.0
    peak_window = ""
    if dense:
        start, tot, _ctrb = dense[0]
        global_peak_rate = tot / 5.0
        peak_window = start.isoformat()

    families: Dict[str, List[NormEvent]] = defaultdict(list)
    for e in events:
        ev = e.event or ""
        b = _storm_event_bucket(ev)
        if b:
            families[b].append(e)

    lines: List[str] = []
    lines.append("## Event Storm Analysis")
    lines.append("")
    lines.append(
        f"- **Global peak mean rate (busiest 5s bucket):** {global_peak_rate:.2f} evt/s at `{peak_window}`"
    )
    lines.append("")
    ctr_total = Counter()
    for e in events:
        b = _storm_event_bucket(e.event or "")
        if b:
            ctr_total[b] += 1

    lines.append("| event_type | count | % of all events | peak_rate | repetition_pattern | compressibility | cpu_significance_guess |")
    lines.append("|------------|------:|----------------:|-----------|-------------------|----------------|------------------------|")
    for ev_name, c in ctr_total.most_common():
        pct = 100.0 * c / total
        meta = _analyze_storm_family(ev_name, families.get(ev_name, []), total, global_peak_rate)
        lines.append(
            "| `{0}` | {1} | {2:.4f} | {3} | `{4}` | `{5}` | `{6}` |".format(
                ev_name,
                c,
                pct,
                meta["peak_rate"],
                meta["repetition_pattern"],
                meta["compressibility"],
                meta["cpu_significance_guess"],
            )
        )
    lines.append("")
    lines.append(
        "- **Interpretation:** `LIKELY_COMPRESSIBLE` + `PER_BAR_REPETITIVE` favors cadence-aware sampling or dedupe; "
        "`STATE_TRANSITION_BASED` is often semantically sparse despite volume; `POSSIBLY_CPU_SIGNIFICANT` warrants CPU proof logs."
    )
    lines.append("")
    return "\n".join(lines)


def activity_gap_section(events: List[NormEvent], thresholds: Dict[str, Any]) -> str:
    t_silent_min = float(thresholds.get("silent_t_silent_min", 20))

    def is_activity(ev: str) -> bool:
        if not ev:
            return False
        if ev == "RECONCILIATION_PASS_SUMMARY":
            return True
        if ev == "ENGINE_TIMER_HEARTBEAT":
            return True
        if ev.startswith("ENTRY_") or ev.startswith("EXIT_"):
            return True
        if ev.startswith("EXECUTION_"):
            return True
        if ev.startswith("PROTECTIVE_"):
            return True
        if "RECONCILIATION" in ev and "PASS" in ev:
            return True
        return False

    activity_ts = sorted(e.ts_utc for e in events if is_activity(e.event or ""))
    gap_min, ga, gb = max_gap_minutes(activity_ts)
    is_real = gap_min >= t_silent_min

    lines: List[str] = []
    lines.append("## Activity Gap Validation")
    lines.append("")
    lines.append(
        "Expanded activity includes: `ENTRY_*`, `EXIT_*`, `EXECUTION_*`, `RECONCILIATION_PASS_SUMMARY`, "
        "`PROTECTIVE_*`, `ENGINE_TIMER_HEARTBEAT`."
    )
    lines.append(f"- **Largest gap:** **{gap_min:.3f} min** between `{ga}` and `{gb}`" if ga and gb else "- **Largest gap:** n/a")
    lines.append(
        f"- **silent_t_silent_min (reference):** {t_silent_min} min → **is_real_gap** (vs expanded activity): `{is_real}`"
    )
    lines.append(f"- **Activity timestamps:** {len(activity_ts)}")
    lines.append("")
    lines.append("| gap_start | gap_end | duration_min | is_real_gap |")
    lines.append("|-----------|---------|-------------:|------------:|")
    if ga and gb:
        lines.append(f"| `{ga.isoformat()}` | `{gb.isoformat()}` | {gap_min:.3f} | {is_real} |")
    lines.append("")
    return "\n".join(lines)


# Robot event_type -> equivalent feed event names (explicit, small; extend as needed).
ROBOT_TO_FEED_ALIASES: Dict[str, Tuple[str, ...]] = {
    "CONNECTION_LOST": ("CONNECTION_LOST", "connection_lost"),
    "CONNECTION_LOST_SUSTAINED": ("CONNECTION_LOST_SUSTAINED", "CONNECTION_LOST", "connection_lost"),
    "ENGINE_STALLED": ("ENGINE_STALLED", "engine_stalled", "Engine_Stalled"),
    "RECONCILIATION_QTY_MISMATCH": ("RECONCILIATION_QTY_MISMATCH", "reconciliation_qty_mismatch"),
    "RECONCILIATION_QTY_MISMATCH_STILL_OPEN": (
        "RECONCILIATION_QTY_MISMATCH_STILL_OPEN",
        "RECONCILIATION_QTY_MISMATCH",
    ),
    "ENGINE_STOP": ("ENGINE_STOP", "engine_stop"),
    "ENGINE_START": ("ENGINE_START", "engine_start"),
}


def _feed_total_for_robot_type(rc: Counter, fc: Counter, robot_type: str) -> Tuple[int, int, int]:
    """Returns (robot_count, feed_direct, feed_via_alias)."""
    r_n = rc.get(robot_type, 0)
    direct = fc.get(robot_type, 0)
    alias_sum = 0
    for alias in ROBOT_TO_FEED_ALIASES.get(robot_type, ()):
        if alias == robot_type:
            continue
        alias_sum += fc.get(alias, 0)
    return r_n, direct, alias_sum


def watchdog_alignment_section(
    events: List[NormEvent],
    feed_events: List[NormEvent],
    feed_path: Optional[Path],
) -> str:
    rc = Counter(e.event or "" for e in events)
    fc = Counter(e.event or "" for e in feed_events)
    keys = sorted(set(rc.keys()) | set(fc.keys()))

    def classify(et: str) -> str:
        r_n, direct, aliased = _feed_total_for_robot_type(rc, fc, et)
        feed_total = direct + aliased
        if r_n > 0 and direct > 0:
            return "SUPPORTED_IN_FEED"
        if r_n > 0 and direct == 0 and aliased > 0:
            return "ALIAS_REQUIRED"
        if r_n > 0 and feed_total == 0:
            return "ROBOT_ONLY_EXPECTED"
        if r_n == 0 and fc.get(et, 0) > 0:
            return "MISSING_UNEXPECTED"
        return "NEITHER"

    lines: List[str] = []
    lines.append("## Watchdog vs Robot Alignment (frontend_feed)")
    lines.append("")
    lines.append(f"- **feed path:** `{feed_path}`")
    lines.append("- **Alias map:** explicit robot→feed names in `ROBOT_TO_FEED_ALIASES` (taxonomy-aware).")
    lines.append("")
    lines.append("| event_type | robot_count | feed_direct | feed_via_alias | classification |")
    lines.append("|------------|------------:|------------:|---------------:|----------------|")
    interesting = [k for k in keys if rc[k] + fc[k] > 0]
    interesting.sort(key=lambda k: -(rc[k] + fc[k]))
    for et in interesting[:200]:
        r_n, direct, aliased = _feed_total_for_robot_type(rc, fc, et)
        lines.append(
            "| `{0}` | {1} | {2} | {3} | `{4}` |".format(et, r_n, direct, aliased, classify(et))
        )
    lines.append("")
    return "\n".join(lines)


_TS_EXTRACT = re.compile(r'"ts_utc"\s*:\s*"([^"]+)"')


def _line_quick_window(line: str, day_start: datetime, day_end: datetime) -> Optional[bool]:
    """If ts_utc appears as a JSON string and parses, return whether line is in day window; else None (need full parse)."""
    m = _TS_EXTRACT.search(line)
    if not m:
        return None
    dt = parse_ts(m.group(1))
    if dt is None:
        return None
    return day_start <= dt < day_end


def parse_integrity_section(
    paths: List[Path],
    day_start: datetime,
    day_end: datetime,
    project_root: Path,
    ingest_stats: Dict[str, Any],
    total_events_window: int,
    paths_with_window_hits: Optional[Set[str]] = None,
) -> str:
    """Malformed JSONL rows per file, scoped to the audit window (fast path skips most lines)."""
    per_file: List[Dict[str, Any]] = []
    bad_ts_in_window: List[datetime] = []
    for path in paths:
        if paths_with_window_hits is not None:
            try:
                rp = str(path.resolve())
            except Exception:
                rp = str(path)
            if rp not in paths_with_window_hits:
                continue
        bad = 0
        window_lines = 0
        total_considered = 0
        for _ln, line in iter_jsonl(path):
            if not line:
                continue
            q = _line_quick_window(line, day_start, day_end)
            if q is False:
                continue
            total_considered += 1
            try:
                obj = json.loads(line)
            except Exception:
                bad += 1
                m = _TS_EXTRACT.search(line)
                if m:
                    dt = parse_ts(m.group(1))
                    if dt and day_start <= dt < day_end:
                        bad_ts_in_window.append(dt)
                continue
            if not isinstance(obj, dict):
                bad += 1
                continue
            ev = normalize_event(obj, path.name)
            if ev is None:
                bad += 1
                continue
            if ev.ts_utc < day_start or ev.ts_utc >= day_end:
                continue
            window_lines += 1

        rel = str(path)
        try:
            rel = str(path.relative_to(project_root))
        except ValueError:
            pass
        denom = window_lines + bad
        pct = (100.0 * bad / denom) if denom else 0.0
        per_file.append(
            {
                "file": rel,
                "malformed_count": bad,
                "window_lines": window_lines,
                "percentage_of_window": pct,
                "lines_quick_scanned": total_considered,
            }
        )

    clustered = "n/a"
    if bad_ts_in_window:
        clustered = f"{len(bad_ts_in_window)} malformed rows had ts_utc in window"

    lines: List[str] = []
    lines.append("## JSONL Parse Integrity")
    lines.append("")
    lines.append(f"- **Total ingest parse_errors (windowed ingest):** {ingest_stats.get('parse_errors_count', 0)}")
    lines.append(f"- **Events successfully parsed in window:** {total_events_window}")
    lines.append("")
    lines.append("| file | malformed (window) | window OK lines | % malformed∕window | time_clustered |")
    lines.append("|------|-------------------:|----------------:|---------------------:|----------------|")
    for row in sorted(per_file, key=lambda x: -x["malformed_count"])[:40]:
        lines.append(
            "| `{0}` | {1} | {2} | {3:.4f} | {4} |".format(
                row["file"],
                row["malformed_count"],
                row["window_lines"],
                row["percentage_of_window"],
                clustered if row["malformed_count"] else "",
            )
        )
    lines.append("")
    return "\n".join(lines)


def compute_runtime_verdict(events: List[NormEvent]) -> Dict[str, Any]:
    fp = [e for e in events if (e.event or "") == "RUNTIME_FINGERPRINT"]
    dups = [e for e in events if (e.event or "") == "DUPLICATE_STRATEGY_INSTANCE_DETECTED"]
    rebinds = [e for e in events if (e.event or "") == "EXECUTOR_REBOUND"]
    iea_own = [e for e in events if (e.event or "") == "IEA_OWNERSHIP_CHANGED"]
    conflict_ev = [
        e
        for e in events
        if (e.event or "")
        in (
            "FLATTEN_SECONDARY_INSTANCE_SKIPPED",
            "RECONCILIATION_SECONDARY_INSTANCE_SKIPPED",
        )
    ]

    sids: List[str] = []
    for e in fp:
        d = e.data if isinstance(e.data, dict) else {}
        sid = str(d.get("strategy_instance_id") or "").strip()
        if sid:
            sids.append(sid)

    RUNTIME_FINGERPRINT_PRESENT = len(fp) > 0
    MULTIPLE_STRATEGY_INSTANCES_PRESENT = len(dups) > 0 or (len(set(sids)) > 1)
    EXECUTOR_REBOUND_OBSERVED = len(rebinds) > 0
    meaningful_rebound = False
    for e in rebinds:
        d = e.data if isinstance(e.data, dict) else {}
        old_e = d.get("old_executor_id")
        if old_e is not None and str(old_e).strip():
            meaningful_rebound = True
            break
    EXECUTOR_CHANGED_MID_SESSION = len(iea_own) > 0 or meaningful_rebound
    OWNERSHIP_CONFLICT_DETECTED = len(dups) > 0 or len(conflict_ev) > 0

    if (
        not RUNTIME_FINGERPRINT_PRESENT
        or MULTIPLE_STRATEGY_INSTANCES_PRESENT
        or OWNERSHIP_CONFLICT_DETECTED
    ):
        confidence = "LOW"
    elif EXECUTOR_CHANGED_MID_SESSION:
        confidence = "MEDIUM"
    else:
        confidence = "HIGH"

    return {
        "MULTIPLE_STRATEGY_INSTANCES_PRESENT": MULTIPLE_STRATEGY_INSTANCES_PRESENT,
        "EXECUTOR_REBOUND_OBSERVED": EXECUTOR_REBOUND_OBSERVED,
        "EXECUTOR_CHANGED_MID_SESSION": EXECUTOR_CHANGED_MID_SESSION,
        "OWNERSHIP_CONFLICT_DETECTED": OWNERSHIP_CONFLICT_DETECTED,
        "RUNTIME_FINGERPRINT_PRESENT": RUNTIME_FINGERPRINT_PRESENT,
        "RUNTIME_IDENTITY_CONFIDENCE": confidence,
    }


def _count_malformed_window_lines(
    paths: List[Path],
    day_start: datetime,
    day_end: datetime,
    paths_with_window_hits: Optional[Set[str]],
) -> int:
    bad_total = 0
    for path in paths:
        if paths_with_window_hits is not None:
            try:
                rp = str(path.resolve())
            except Exception:
                rp = str(path)
            if rp not in paths_with_window_hits:
                continue
        for _ln, line in iter_jsonl(path):
            if not line:
                continue
            q = _line_quick_window(line, day_start, day_end)
            if q is False:
                continue
            try:
                obj = json.loads(line)
            except Exception:
                bad_total += 1
                continue
            if not isinstance(obj, dict):
                bad_total += 1
                continue
            ev = normalize_event(obj, path.name)
            if ev is None:
                bad_total += 1
                continue
            if ev.ts_utc < day_start or ev.ts_utc >= day_end:
                continue
    return bad_total


def collect_operational_metrics(
    events: List[NormEvent],
    journal_rows: List[Dict[str, Any]],
    stats: Dict[str, Any],
    exec_flags: Dict[str, Any],
    verdict: Dict[str, Any],
    prot_summary: Counter,
    paths: List[Path],
    day_start: datetime,
    day_end: datetime,
    window_path_keys: Optional[Set[str]],
) -> Dict[str, Any]:
    fails = [e for e in events if _is_order_submit_fail(e)]
    oks = [e for e in events if _is_order_submit_success(e)]
    sim_fail_ev = [
        e for e in fails if _sim_verification_hit(_extract_reason(e), e.event or "")
    ]
    jr = sum(1 for r in journal_rows if r.get("Rejected"))
    recon_n = sum(1 for e in events if (e.event or "") in _RECON_TYPES)
    prot_n = sum(1 for e in events if (e.event or "") in _PROTECTIVE_TRIGGERS)
    storm: Dict[str, int] = {}
    for e in events:
        b = _storm_event_bucket(e.event or "")
        if b:
            storm[b] = storm.get(b, 0) + 1
    malformed = _count_malformed_window_lines(paths, day_start, day_end, window_path_keys)
    fail_reasons = Counter((_extract_reason(e) or "(empty)") for e in fails)
    return {
        "order_submit_fail": len(fails),
        "order_submit_success": len(oks),
        "sim_verification_failure_events": len(sim_fail_ev),
        "sim_verification_failure_flag": bool(exec_flags.get("SIM_VERIFICATION_FAILURE_PRESENT")),
        "rejection_reasons": dict(fail_reasons.most_common(50)),
        "journal_rejected": jr,
        "reconciliation_mismatch_total": recon_n,
        "protective_triggers_total": prot_n,
        "protective_causal_buckets": dict(prot_summary),
        "parse_errors_ingest": int(stats.get("parse_errors_count", 0)),
        "malformed_lines_window_scan": malformed,
        "runtime_verdict": dict(verdict),
        "storm_event_counts": storm,
        "events_in_window": len(events),
    }


def _trend_numeric(old: float, new: float, *, lower_is_better: bool) -> str:
    if new < old:
        return "IMPROVED" if lower_is_better else "REGRESSED"
    if new > old:
        return "REGRESSED" if lower_is_better else "IMPROVED"
    return "UNCHANGED"


def _trend_bool_bad_when_true(old: bool, new: bool) -> str:
    """True = bad condition (e.g. SIM failure present)."""
    if new and not old:
        return "REGRESSED"
    if old and not new:
        return "IMPROVED"
    return "UNCHANGED"


def write_comparison_report(
    path: Path,
    older: Dict[str, Any],
    newer: Dict[str, Any],
    older_date: date,
    newer_date: date,
) -> None:
    lines: List[str] = []
    lines.append(f"# Investigation comparison: **older** `{older_date}` → **newer** `{newer_date}`")
    lines.append("")
    lines.append("| category | older | newer | delta (newer−older) | verdict (newer day) |")
    lines.append("|----------|------:|------:|--------------------:|---------------------|")

    def row(cat: str, b: Any, o: Any, delta: Any, verdict: str) -> None:
        lines.append(f"| {cat} | {b} | {o} | {delta} | **{verdict}** |")

    b_f = older["order_submit_fail"]
    o_f = newer["order_submit_fail"]
    row("ORDER_SUBMIT_FAIL", b_f, o_f, o_f - b_f, _trend_numeric(b_f, o_f, lower_is_better=True))
    b_s = older["order_submit_success"]
    o_s = newer["order_submit_success"]
    row(
        "ORDER_SUBMIT_SUCCESS",
        b_s,
        o_s,
        o_s - b_s,
        _trend_numeric(float(b_s), float(o_s), lower_is_better=False),
    )
    b_sim = older["sim_verification_failure_flag"]
    o_sim = newer["sim_verification_failure_flag"]
    row(
        "SIM_VERIFICATION_FAILURE_PRESENT (flag)",
        b_sim,
        o_sim,
        f"{b_sim}->{o_sim}",
        _trend_bool_bad_when_true(bool(b_sim), bool(o_sim)),
    )
    b_se = older["sim_verification_failure_events"]
    o_se = newer["sim_verification_failure_events"]
    row(
        "SIM verification (fail events, heuristic count)",
        b_se,
        o_se,
        o_se - b_se,
        _trend_numeric(float(b_se), float(o_se), lower_is_better=True),
    )
    b_j = older["journal_rejected"]
    o_j = newer["journal_rejected"]
    row("rejected journals", b_j, o_j, o_j - b_j, _trend_numeric(float(b_j), float(o_j), lower_is_better=True))
    b_r = older["reconciliation_mismatch_total"]
    o_r = newer["reconciliation_mismatch_total"]
    row(
        "reconciliation mismatch events",
        b_r,
        o_r,
        o_r - b_r,
        _trend_numeric(float(b_r), float(o_r), lower_is_better=True),
    )
    b_p = older["protective_triggers_total"]
    o_p = newer["protective_triggers_total"]
    row(
        "protective triggers",
        b_p,
        o_p,
        o_p - b_p,
        _trend_numeric(float(b_p), float(o_p), lower_is_better=True),
    )
    b_m = older["malformed_lines_window_scan"]
    o_m = newer["malformed_lines_window_scan"]
    row(
        "malformed JSONL rows (window scan)",
        b_m,
        o_m,
        o_m - b_m,
        _trend_numeric(float(b_m), float(o_m), lower_is_better=True),
    )
    b_pi = older["parse_errors_ingest"]
    o_pi = newer["parse_errors_ingest"]
    row(
        "ingest parse_errors_count",
        b_pi,
        o_pi,
        o_pi - b_pi,
        _trend_numeric(float(b_pi), float(o_pi), lower_is_better=True),
    )

    rv_b = older["runtime_verdict"]
    rv_o = newer["runtime_verdict"]
    lines.append("")
    lines.append("## Runtime ownership anomaly deltas (verdict describes **newer** day vs older)")
    lines.append("")
    lines.append("| signal | older | newer | verdict |")
    lines.append("|--------|-------|-------|---------|")
    bad_when_true = {
        "MULTIPLE_STRATEGY_INSTANCES_PRESENT",
        "OWNERSHIP_CONFLICT_DETECTED",
        "EXECUTOR_CHANGED_MID_SESSION",
    }
    for k in (
        "MULTIPLE_STRATEGY_INSTANCES_PRESENT",
        "EXECUTOR_REBOUND_OBSERVED",
        "EXECUTOR_CHANGED_MID_SESSION",
        "OWNERSHIP_CONFLICT_DETECTED",
        "RUNTIME_FINGERPRINT_PRESENT",
        "RUNTIME_IDENTITY_CONFIDENCE",
    ):
        bv = rv_b.get(k)
        ov = rv_o.get(k)
        if k == "RUNTIME_IDENTITY_CONFIDENCE":
            rank = {"HIGH": 3, "MEDIUM": 2, "LOW": 1}
            rb, ro = rank.get(str(bv), 0), rank.get(str(ov), 0)
            vd = "UNCHANGED" if rb == ro else ("IMPROVED" if ro > rb else "REGRESSED")
        elif k == "RUNTIME_FINGERPRINT_PRESENT":
            if ov and not bv:
                vd = "IMPROVED"
            elif bv and not ov:
                vd = "REGRESSED"
            else:
                vd = "UNCHANGED"
        elif k in bad_when_true and isinstance(bv, bool) and isinstance(ov, bool):
            vd = _trend_bool_bad_when_true(bv, ov)
        elif isinstance(bv, bool) and isinstance(ov, bool):
            if bv == ov:
                vd = "UNCHANGED"
            else:
                vd = "CHANGED"
        else:
            vd = "UNCHANGED" if bv == ov else "CHANGED"
        lines.append(f"| `{k}` | `{bv}` | `{ov}` | **{vd}** |")

    lines.append("")
    lines.append("## Storm family deltas")
    lines.append("")
    all_storm = sorted(set(older["storm_event_counts"]) | set(newer["storm_event_counts"]))
    lines.append("| storm family | older | newer | delta | verdict |")
    lines.append("|--------------|------:|------:|------:|---------|")
    for k in all_storm:
        b_c = older["storm_event_counts"].get(k, 0)
        o_c = newer["storm_event_counts"].get(k, 0)
        vd = _trend_numeric(float(b_c), float(o_c), lower_is_better=True)
        lines.append(f"| `{k}` | {b_c} | {o_c} | {o_c - b_c} | **{vd}** |")

    lines.append("")
    lines.append("## Protective causal bucket deltas")
    lines.append("")
    all_p = sorted(
        set(older["protective_causal_buckets"]) | set(newer["protective_causal_buckets"])
    )
    lines.append("| bucket | older | newer | delta | verdict |")
    lines.append("|--------|------:|------:|------:|---------|")
    for k in all_p:
        b_c = older["protective_causal_buckets"].get(k, 0)
        o_c = newer["protective_causal_buckets"].get(k, 0)
        vd = _trend_numeric(float(b_c), float(o_c), lower_is_better=True)
        lines.append(f"| `{k}` | {b_c} | {o_c} | {o_c - b_c} | **{vd}** |")

    lines.append("")
    lines.append("## Rejection reason deltas (top movers)")
    lines.append("")
    br = Counter(older["rejection_reasons"])
    or_ = Counter(newer["rejection_reasons"])
    keys_r = set(br) | set(or_)
    deltas = [(k, or_[k] - br[k], or_[k], br[k]) for k in keys_r]
    deltas.sort(key=lambda x: -abs(x[1]))
    for k, d_, o_n, b_n in deltas[:20]:
        if d_ == 0:
            continue
        vd = "REGRESSED" if d_ > 0 else "IMPROVED"
        lines.append(f"- `{k[:120]}`: base={b_n} → compare={o_n} (Δ {d_}) **{vd}**")

    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text("\n".join(lines), encoding="utf-8")


def runtime_ownership_section(events: List[NormEvent], verdict: Dict[str, Any]) -> str:
    want = {
        "RUNTIME_FINGERPRINT",
        "EXECUTOR_REBOUND",
        "IEA_OWNERSHIP_CHANGED",
        "IEA_BINDING",
        "DUPLICATE_STRATEGY_INSTANCE_DETECTED",
        "FLATTEN_SECONDARY_INSTANCE_SKIPPED",
        "FLATTEN_OWNER_TAKEOVER",
        "RECONCILIATION_SECONDARY_INSTANCE_SKIPPED",
    }
    rows_ev = [e for e in events if (e.event or "") in want]

    lines: List[str] = []
    lines.append("## Runtime Identity Verdict")
    lines.append("")
    for k in (
        "MULTIPLE_STRATEGY_INSTANCES_PRESENT",
        "EXECUTOR_REBOUND_OBSERVED",
        "EXECUTOR_CHANGED_MID_SESSION",
        "OWNERSHIP_CONFLICT_DETECTED",
        "RUNTIME_FINGERPRINT_PRESENT",
        "RUNTIME_IDENTITY_CONFIDENCE",
    ):
        lines.append(f"- **`{k}`:** `{verdict.get(k)}`")
    lines.append("")
    lines.append("## Runtime Ownership Audit")
    lines.append("")
    lines.append("| timestamp | strategy_id | executor_id | iea_id | action |")
    lines.append("|-----------|-------------|-------------|--------|--------|")
    for e in sorted(rows_ev, key=lambda x: x.ts_utc)[:400]:
        d = e.data if isinstance(e.data, dict) else {}
        sid = str(d.get("strategy_instance_id") or d.get("instance_id") or "")
        exo = str(d.get("old_executor_id") or "")
        exn = str(d.get("new_executor_id") or d.get("executor_id") or "")
        iid = str(d.get("iea_instance_id") or d.get("iea_id") or "")
        if exo and exn:
            ex = f"{exo}->{exn}"
        else:
            ex = exn or exo
        lines.append(
            f"| `{e.ts_utc.isoformat()}` | `{sid[:24]}` | `{ex[:40]}` | `{iid}` | `{e.event}` |"
        )

    dup = [e for e in events if (e.event or "") == "DUPLICATE_STRATEGY_INSTANCE_DETECTED"]
    lines.append("")
    lines.append(f"- **DUPLICATE_STRATEGY_INSTANCE_DETECTED:** {len(dup)}")
    for e in dup[:5]:
        lines.append(f"  - `{e.ts_utc.isoformat()}` data={json.dumps(_event_data_subset(e), default=str)[:500]}")
    lines.append("")
    return "\n".join(lines)


def ingest_robot_day_window(
    project_root: Path,
    log_dir: Path,
    audit_date: date,
    tz: str,
) -> Tuple[
    List[NormEvent],
    List[NormEvent],
    Optional[Path],
    Dict[str, Any],
    List[Path],
    Set[str],
    List[Dict[str, Any]],
    datetime,
    datetime,
]:
    """Load robot + feed events for one trading date window."""
    day_start, day_end = day_window_utc(audit_date, tz)
    stats: Dict[str, Any] = {
        "files_read": 0,
        "lines_read": 0,
        "parse_errors_count": 0,
        "journal_files_used": 0,
    }
    paths = collect_robot_jsonl_paths(log_dir)
    raw: List[NormEvent] = []
    window_path_keys: Set[str] = set()
    for path in paths:
        before = len(raw)
        raw.extend(ingest_jsonl_file(path, day_start, day_end, stats))
        chunk = raw[before:]
        if chunk:
            try:
                window_path_keys.add(str(path.resolve()))
            except Exception:
                window_path_keys.add(str(path))
    events = raw
    wd = collect_watchdog_paths(log_dir)
    feed_path: Optional[Path] = wd[0] if wd else None
    feed_events: List[NormEvent] = []
    if feed_path and feed_path.is_file():
        st_feed: Dict[str, Any] = {}
        feed_events.extend(ingest_jsonl_file(feed_path, day_start, day_end, st_feed))
    journal_rows = scan_journals(project_root, audit_date)
    return (
        events,
        feed_events,
        feed_path,
        stats,
        paths,
        window_path_keys,
        journal_rows,
        day_start,
        day_end,
    )


def executive_summary(
    exec_flags: Dict[str, Any],
    events: List[NormEvent],
    wc: Dict[str, Any],
    verdict: Optional[Dict[str, Any]] = None,
) -> str:
    lines: List[str] = []
    lines.append("## Executive Summary")
    lines.append("")
    if verdict:
        lines.append(
            f"- **Runtime identity:** confidence=`{verdict.get('RUNTIME_IDENTITY_CONFIDENCE')}`, "
            f"fingerprint=`{verdict.get('RUNTIME_FINGERPRINT_PRESENT')}`, "
            f"ownership_conflict=`{verdict.get('OWNERSHIP_CONFLICT_DETECTED')}`."
        )
    lines.append(
        f"- **SIM / verification failures present:** `{exec_flags.get('SIM_VERIFICATION_FAILURE_PRESENT')}` "
        f"(ORDER_SUBMIT_FAIL + journal rejection reasons + event names)."
    )
    lines.append(
        f"- **Order submit ratio (success / total submits):** `{exec_flags.get('submit_ratio_success_over_total')}` "
        f"(`{exec_flags.get('submit_success')}` ok / `{exec_flags.get('submit_fail')}` fail)."
    )
    mm = sum(1 for e in events if (e.event or "") in _RECON_TYPES)
    lines.append(f"- **Reconciliation qty mismatch events (explicit types):** `{mm}`")
    lines.append(f"- **Watchdog crosscheck:** `{json.dumps(wc)}`")
    lines.append("")
    return "\n".join(lines)


def main() -> int:
    import sys

    if hasattr(sys.stdout, "reconfigure"):
        try:
            sys.stdout.reconfigure(encoding="utf-8", errors="replace")
        except Exception:
            pass

    ap = argparse.ArgumentParser()
    ap.add_argument("--date", required=True, help="Trading date YYYY-MM-DD")
    ap.add_argument("--tz", default="chicago", choices=("chicago", "utc", "local"))
    ap.add_argument("--log-dir", default=None)
    ap.add_argument(
        "--md-out",
        default=None,
        help="Markdown report path (default: reports/investigation_<date>.md under project root)",
    )
    ap.add_argument(
        "--compare-date",
        default=None,
        help="Second trading date (YYYY-MM-DD) for delta report vs --date",
    )
    ap.add_argument(
        "--compare-md-out",
        default=None,
        help="Comparison markdown path (default: reports/investigation_compare_<date>_vs_<compare-date>.md)",
    )
    args = ap.parse_args()

    project_root = resolve_project_root()
    thresholds = load_thresholds(project_root)
    log_dir = resolve_robot_log_dir(args.log_dir)
    audit_date = parse_audit_date(args.date)

    (
        events,
        feed_events,
        feed_path,
        stats,
        paths,
        window_path_keys,
        journal_rows,
        day_start,
        day_end,
    ) = ingest_robot_day_window(project_root, log_dir, audit_date, args.tz)

    def _rel(p: Path) -> str:
        try:
            return str(p.relative_to(project_root))
        except ValueError:
            return str(p)

    file_meta: List[Dict[str, Any]] = []
    for path in paths:
        chunk = [e for e in events if e.file == path.name]
        if chunk:
            file_meta.append(
                {
                    "path": _rel(path),
                    "lines_in_window": len(chunk),
                    "first_utc": chunk[0].ts_utc.isoformat(),
                    "last_utc": chunk[-1].ts_utc.isoformat(),
                }
            )
        else:
            file_meta.append({"path": _rel(path), "lines_in_window": 0})

    wc = watchdog_crosscheck(
        events,
        feed_path,
        day_start,
        day_end,
        int(thresholds.get("watchdog_max_count_delta", 2)),
    )

    verdict = compute_runtime_verdict(events)
    ex_text, ex_flags = execution_truth_section(events, journal_rows)
    recon_text = reconciliation_truth_section(events)
    prot_text, prot_summary = protective_classification_section(events)
    storm_text = storm_analysis_section(events, day_start, day_end)
    gap_text = activity_gap_section(events, thresholds)
    wf_text = watchdog_alignment_section(events, feed_events, feed_path)
    parse_text = parse_integrity_section(
        paths,
        day_start,
        day_end,
        project_root,
        stats,
        len(events),
        window_path_keys if window_path_keys else None,
    )
    own_text = runtime_ownership_section(events, verdict)

    metrics = collect_operational_metrics(
        events,
        journal_rows,
        stats,
        ex_flags,
        verdict,
        prot_summary,
        paths,
        day_start,
        day_end,
        window_path_keys if window_path_keys else None,
    )

    ev_hist = Counter(e.event or "" for e in events)
    top_events = ev_hist.most_common(40)
    dense = bucket_counts(
        events, day_start, day_end, bucket_s=int(thresholds.get("engine_load_bucket_seconds", 5))
    )
    top_buckets = dense[:12]

    header = [
        f"# Investigation report: {audit_date}",
        "",
        f"- **Window (UTC):** `{day_start.isoformat()}` … `{day_end.isoformat()}`",
        f"- **TZ mode:** `{args.tz}`",
        f"- **Log dir:** `{log_dir}`",
        f"- **Events in window:** {len(events)} (ingest parse_errors: {stats.get('parse_errors_count', 0)})",
        "",
    ]

    appendix = [
        "## Appendix: ingestion coverage",
        "",
    ]
    in_window = [m for m in file_meta if m.get("lines_in_window", 0) > 0]
    appendix.append(f"- Files with data in window: **{len(in_window)}** / **{len(file_meta)}**")
    for m in sorted(in_window, key=lambda x: -x.get("lines_in_window", 0))[:40]:
        appendix.append(f"  - `{m['path']}`: {m['lines_in_window']} lines")
    appendix.append("")
    appendix.append("## Appendix: top 40 event types")
    appendix.append("")
    for name, n in top_events:
        appendix.append(f"- {n}× `{name}`")
    appendix.append("")
    appendix.append("## Appendix: busiest 5s buckets")
    appendix.append("")
    for start, total, ctr in top_buckets:
        topn = ctr.most_common(12)
        names = ", ".join(f"{nm}:{c}" for nm, c in topn if nm)
        appendix.append(f"- **{start.isoformat()}** total={total} — {names}")

    report = "\n".join(
        header
        + [executive_summary(ex_flags, events, wc, verdict)]
        + [ex_text, recon_text, prot_text, storm_text, gap_text, wf_text, parse_text, own_text]
        + appendix
    )
    print(report)

    md_out = args.md_out
    if not md_out:
        md_out = str(project_root / "reports" / f"investigation_{audit_date}.md")
    outp = Path(md_out)
    outp.parent.mkdir(parents=True, exist_ok=True)
    outp.write_text(report, encoding="utf-8")
    print(f"\nWrote {outp}", file=__import__("sys").stderr)

    if args.compare_date:
        d_cmp = parse_audit_date(args.compare_date)
        (
            ev2,
            _fe2,
            _fp2,
            st2,
            paths2,
            wpk2,
            jr2,
            ds2,
            de2,
        ) = ingest_robot_day_window(project_root, log_dir, d_cmp, args.tz)
        ex2_flags = execution_truth_section(ev2, jr2)[1]
        v2 = compute_runtime_verdict(ev2)
        _pt2, ps2 = protective_classification_section(ev2)
        m2 = collect_operational_metrics(
            ev2,
            jr2,
            st2,
            ex2_flags,
            v2,
            ps2,
            paths2,
            ds2,
            de2,
            wpk2 if wpk2 else None,
        )
        d_a, d_b = audit_date, d_cmp
        if d_a <= d_b:
            older_d, newer_d = d_a, d_b
            older_m, newer_m = metrics, m2
        else:
            older_d, newer_d = d_b, d_a
            older_m, newer_m = m2, metrics
        cmp_out = args.compare_md_out
        if not cmp_out:
            cmp_out = str(
                project_root
                / "reports"
                / f"investigation_compare_{d_a}_vs_{d_b}.md"
            )
        cmp_path = Path(cmp_out)
        write_comparison_report(cmp_path, older_m, newer_m, older_d, newer_d)
        print(f"Wrote comparison {cmp_path}", file=__import__("sys").stderr)

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
