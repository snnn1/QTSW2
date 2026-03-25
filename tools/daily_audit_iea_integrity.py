"""
IEA integrity layer for daily_audit — additive analysis only.
"""
from __future__ import annotations

import re
from bisect import bisect_right
from collections import defaultdict, deque
from datetime import datetime
from typing import Any, Dict, List, Optional, Sequence, Tuple

from log_audit import NormEvent


def _data(ev: NormEvent) -> Dict[str, Any]:
    d = ev.data if isinstance(ev.data, dict) else {}
    return d


def _parse_summary_broker_local(summary: str) -> Tuple[Optional[int], Optional[int]]:
    """Parse broker_working=N local_working=M from summary string."""
    if not summary:
        return None, None
    bw = re.search(r"broker_working=(\d+)", summary)
    lw = re.search(r"local_working=(\d+)", summary)
    return (
        int(bw.group(1)) if bw else None,
        int(lw.group(1)) if lw else None,
    )


def _int_field(d: Dict[str, Any], *keys: str) -> Optional[int]:
    for k in keys:
        v = d.get(k)
        if v is None:
            continue
        try:
            return int(str(v).strip())
        except Exception:
            continue
    return None


def _str_field(d: Dict[str, Any], *keys: str) -> str:
    for k in keys:
        v = d.get(k)
        if v is not None and str(v).strip():
            return str(v).strip()
    return ""


def _mismatch_type(d: Dict[str, Any]) -> str:
    return str(d.get("mismatch_type") or d.get("mismatchType") or "").strip().upper()


def _instrument(ev: NormEvent) -> str:
    d = _data(ev)
    inst = (ev.instrument or "").strip()
    if inst:
        return inst
    v = d.get("instrument")
    if v is not None and str(v).strip():
        return str(v).strip()
    gate = d.get("gate")
    if isinstance(gate, dict):
        v2 = gate.get("instrument")
        if v2 is not None and str(v2).strip():
            return str(v2).strip()
    return ""


def _truthy(d: Dict[str, Any], *keys: str) -> bool:
    for k in keys:
        v = d.get(k)
        if v is True:
            return True
        if isinstance(v, str) and v.lower() in ("true", "1", "yes"):
            return True
    return False


def _duration_ms_from_gate_result(d: Dict[str, Any]) -> Optional[float]:
    ro = d.get("reconciliation_outcome")
    if isinstance(ro, dict):
        for key in ("DurationMs", "duration_ms", "durationMs"):
            v = ro.get(key)
            if v is not None:
                try:
                    return float(v)
                except Exception:
                    pass
    return None


def _tie_break_event_order(ev: NormEvent) -> int:
    """Stable ordering for same-timestamp events (gate reconciliation before progress observed)."""
    evn = ev.event or ""
    order = {
        "STATE_CONSISTENCY_GATE_ENGAGED": 5,
        "STATE_CONSISTENCY_GATE_RECONCILIATION_STARTED": 8,
        "STATE_CONSISTENCY_GATE_RECONCILIATION_RESULT": 10,
        "RECONCILIATION_THROTTLED": 15,
        "RECONCILIATION_NO_PROGRESS_DETECTED": 18,
        "RECONCILIATION_PROGRESS_OBSERVED": 20,
        "RECONCILIATION_MISMATCH_CLEARED": 30,
        "STATE_CONSISTENCY_GATE_RELEASED": 30,
        "RECONCILIATION_CONVERGED": 30,
    }
    return order.get(evn, 50)


def _sorted_events(evs: Sequence[NormEvent]) -> List[NormEvent]:
    return sorted(evs, key=lambda e: (e.ts_utc, _tie_break_event_order(e)))


def _coerce_int(v: Any) -> Optional[int]:
    if v is None:
        return None
    try:
        return int(str(v).strip())
    except Exception:
        return None


def _ro_pick(ro: Dict[str, Any], *names: str) -> Optional[int]:
    for n in names:
        for k in (n, n[0].upper() + n[1:] if len(n) > 1 else n):
            if k in ro:
                x = _coerce_int(ro[k])
                if x is not None:
                    return x
    return None


def _gate_phase_norm(gs: str) -> str:
    return re.sub(r"[^a-z0-9]", "", (gs or "").strip().lower())


def _mismatch_severity_rank(mt: str) -> int:
    t = (mt or "").strip().upper().replace(" ", "_")
    ranks = {
        "BROKER_AHEAD": 10,
        "JOURNAL_AHEAD": 20,
        "POSITION_QTY_MISMATCH": 30,
        "PROTECTIVE_STATE_DIVERGENCE": 35,
        "ORDER_REGISTRY_MISSING": 40,
        "LIFECYCLE_BROKER_DIVERGENCE": 42,
        "RESTART_RECONCILIATION_UNRESOLVED": 45,
        "UNKNOWN_EXECUTION_PERSISTENT": 50,
        "UNCLASSIFIED_CRITICAL_MISMATCH": 60,
    }
    return ranks.get(t, 55)


def _is_forward_gate_phase(a_raw: str, b_raw: str) -> bool:
    a = _gate_phase_norm(a_raw)
    b = _gate_phase_norm(b_raw)
    if not a or not b or a == b:
        return False
    if b in ("persistentmismatch", "failclosed"):
        return False
    if b == "reconciling" and a == "detectedblocked":
        return True
    if b == "stablependingrelease" and a in ("reconciling", "detectedblocked"):
        return True
    return False


def _extract_gate_signature(d: Dict[str, Any]) -> Dict[str, Any]:
    """Mirror GateProgressEvaluator.BuildSignature + GateProgressSignature fields from log payload."""
    ro = d.get("reconciliation_outcome") if isinstance(d.get("reconciliation_outcome"), dict) else {}
    gs = str(d.get("gate_state") or d.get("gateState") or "")
    mt = str(d.get("mismatch_type") or d.get("mismatchType") or "").strip().upper()
    bw = _int_field(d, "broker_working_count", "broker_working", "brokerWorkingCount")
    lw = _int_field(d, "iea_owned_count", "iea_owned", "local_working", "ieaOwnedCount")
    if isinstance(ro, dict) and ro:
        bw2 = _ro_pick(ro, "BrokerWorkingCountAfter", "broker_working_count_after")
        lw2 = _ro_pick(ro, "IeaOwnedCountAfter", "iea_owned_count_after")
        if bw2 is not None:
            bw = bw2
        if lw2 is not None:
            lw = lw2
    if bw is None:
        bw = 0
    if lw is None:
        lw = 0
    rr_v = d.get("release_ready")
    if rr_v is None and isinstance(ro, dict):
        rr_v = ro.get("ReleaseReadyAfter") or ro.get("release_ready_after")
    rr = bool(rr_v) if rr_v is not None else False
    gap = max(0, int(bw) - int(lw))
    uwc = _int_field(d, "unexplained_working_count", "unexplainedWorkingCount")
    if uwc is None and isinstance(ro, dict):
        uwc = _ro_pick(ro, "UnexplainedWorkingCountAfter", "unexplained_working_count_after")
    return {
        "gate_state": gs,
        "phase_norm": _gate_phase_norm(gs),
        "mismatch_type": mt,
        "broker_working": int(bw),
        "local_owned_working": int(lw),
        "release_ready": rr,
        "gap": gap,
        "unexplained_working_count": int(uwc) if uwc is not None else None,
    }


def _is_measurable_progress(prev: Optional[Dict[str, Any]], cur: Dict[str, Any]) -> bool:
    if prev is None:
        return False
    if cur.get("release_ready") and not prev.get("release_ready"):
        return True
    if _is_forward_gate_phase(str(prev.get("gate_state") or ""), str(cur.get("gate_state") or "")):
        return True
    if _mismatch_severity_rank(str(cur.get("mismatch_type") or "")) < _mismatch_severity_rank(
        str(prev.get("mismatch_type") or "")
    ):
        return True
    if int(cur.get("gap", 0)) < int(prev.get("gap", 0)):
        return True
    cbw, clw = int(cur.get("broker_working", 0)), int(cur.get("local_owned_working", 0))
    pbw, plw = int(prev.get("broker_working", 0)), int(prev.get("local_owned_working", 0))
    if clw > plw and cbw <= pbw + 1:
        return True
    if cbw < pbw and int(cur.get("gap", 0)) <= int(prev.get("gap", 0)):
        return True
    return False


def _same_counts(prev: Dict[str, Any], cur: Dict[str, Any]) -> bool:
    return (
        int(prev.get("gap", 0)) == int(cur.get("gap", 0))
        and int(prev.get("broker_working", 0)) == int(cur.get("broker_working", 0))
        and int(prev.get("local_owned_working", 0)) == int(cur.get("local_owned_working", 0))
        and str(prev.get("mismatch_type") or "") == str(cur.get("mismatch_type") or "")
    )


def _classify_inferred_cycle(prev: Optional[Dict[str, Any]], cur: Dict[str, Any]) -> Optional[str]:
    """
    Return one bucket: mismatch_cleared | mismatch_reduced | ownership_improved | state_only_progress | None.
    """
    if prev is None:
        return None
    if (not prev.get("release_ready")) and cur.get("release_ready"):
        return "mismatch_cleared"
    if int(prev.get("gap", 0)) > 0 and int(cur.get("gap", 0)) == 0:
        return "mismatch_cleared"
    if _same_counts(prev, cur) and _is_forward_gate_phase(
        str(prev.get("gate_state") or ""), str(cur.get("gate_state") or "")
    ):
        return "state_only_progress"
    if not _is_measurable_progress(prev, cur):
        return None
    if _mismatch_severity_rank(str(cur.get("mismatch_type") or "")) < _mismatch_severity_rank(
        str(prev.get("mismatch_type") or "")
    ):
        return "mismatch_reduced"
    if int(cur.get("gap", 0)) < int(prev.get("gap", 0)):
        return "mismatch_reduced"
    cbw, clw = int(cur.get("broker_working", 0)), int(cur.get("local_owned_working", 0))
    pbw, plw = int(prev.get("broker_working", 0)), int(prev.get("local_owned_working", 0))
    if (clw > plw and cbw <= pbw + 1) or (cbw < pbw and int(cur.get("gap", 0)) <= int(prev.get("gap", 0))):
        return "ownership_improved"
    return "mismatch_reduced"


def _is_worsened(prev: Optional[Dict[str, Any]], cur: Dict[str, Any]) -> bool:
    if prev is None:
        return False
    if _is_measurable_progress(prev, cur):
        return False
    if int(cur.get("gap", 0)) > int(prev.get("gap", 0)):
        return True
    if _mismatch_severity_rank(str(cur.get("mismatch_type") or "")) > _mismatch_severity_rank(
        str(prev.get("mismatch_type") or "")
    ):
        return True
    if prev.get("release_ready") and (not cur.get("release_ready")):
        return True
    return False


def _classify_explicit_kind(kind: str) -> str:
    k = (kind or "").strip().lower()
    if not k or k == "measurable":
        return "mismatch_reduced"
    if "clear" in k or "release" in k or "converge" in k:
        return "mismatch_cleared"
    if "owner" in k or "adopt" in k:
        return "ownership_improved"
    if "reduce" in k or "mismatch" in k:
        return "mismatch_reduced"
    return "mismatch_reduced"


def _throttle_progress_this_cycle(
    prev_sig: Optional[Dict[str, Any]],
    cur_sig: Dict[str, Any],
    same_ts_progress: bool,
) -> bool:
    if same_ts_progress:
        return True
    if prev_sig is None:
        return False
    if _same_counts(prev_sig, cur_sig) and _is_forward_gate_phase(
        str(prev_sig.get("gate_state") or ""), str(cur_sig.get("gate_state") or "")
    ):
        return False
    return _is_measurable_progress(prev_sig, cur_sig)


def analyze_throttle_correctness(
    evs: Sequence[NormEvent],
    thresholds: Dict[str, Any],
) -> Dict[str, Any]:
    cycle_thr = int(thresholds.get("iea_integrity_throttle_no_progress_cycle_threshold", 3))
    time_ms = float(thresholds.get("iea_integrity_throttle_no_progress_time_ms", 12_000))

    evs_sorted = _sorted_events(evs)
    by_ts_index: Dict[Tuple[datetime, str], List[str]] = defaultdict(list)
    for e in evs_sorted:
        instk = _instrument(e) or ""
        if e.event == "RECONCILIATION_PROGRESS_OBSERVED":
            kind = str(_data(e).get("kind") or "").strip().lower()
            if kind == "baseline_signature":
                continue
            by_ts_index[(e.ts_utc, instk)].append("progress")

    engagement: Dict[str, datetime] = {}
    for e in evs_sorted:
        inst = _instrument(e)
        if not inst:
            continue
        if e.event in ("STATE_CONSISTENCY_GATE_ENGAGED", "RECONCILIATION_MISMATCH_DETECTED", "MISMATCH_DETECTED"):
            engagement.setdefault(inst, e.ts_utc)

    last_prog_ts: Dict[str, datetime] = {}
    nopc: Dict[str, int] = defaultdict(int)
    prev_sig: Dict[str, Optional[Dict[str, Any]]] = {}

    day_exp = day_act = day_missed = 0
    per: Dict[str, Dict[str, int]] = defaultdict(lambda: {"e": 0, "a": 0, "m": 0})

    for e in evs_sorted:
        if e.event != "STATE_CONSISTENCY_GATE_RECONCILIATION_RESULT":
            continue
        d = _data(e)
        inst = _instrument(e) or "_global_"
        cur_sig = _extract_gate_signature(d)
        throttled = _truthy(d, "gate_reconciliation_throttled", "gateReconciliationThrottled")

        same_ts_progress = "progress" in by_ts_index.get((e.ts_utc, inst), [])

        prv = prev_sig.get(inst)
        progress_cycle = _throttle_progress_this_cycle(prv, cur_sig, same_ts_progress)

        if inst not in last_prog_ts:
            last_prog_ts[inst] = engagement.get(inst, e.ts_utc)

        should = (nopc[inst] >= cycle_thr) or (
            (e.ts_utc - last_prog_ts[inst]).total_seconds() * 1000.0 >= time_ms
        )

        if prv is not None:
            if should:
                day_exp += 1
                per[inst]["e"] += 1
                if throttled:
                    day_act += 1
                    per[inst]["a"] += 1
                else:
                    day_missed += 1
                    per[inst]["m"] += 1

        if prv is None:
            prev_sig[inst] = cur_sig
            continue

        if progress_cycle:
            last_prog_ts[inst] = e.ts_utc
            nopc[inst] = 0
        else:
            nopc[inst] += 1

        prev_sig[inst] = cur_sig

    def _ratio(actual: int, expected: int) -> float:
        return round(actual / float(max(expected, 1)), 8)

    all_inst = set(per.keys()) | set(nopc.keys()) | set(prev_sig.keys()) | set(engagement.keys())
    per_instrument: List[Dict[str, Any]] = []
    for ii in sorted(all_inst):
        if ii == "_global_" and not per.get(ii):
            continue
        ex = int(per.get(ii, {}).get("e", 0))
        ac = int(per.get(ii, {}).get("a", 0))
        mi = int(per.get(ii, {}).get("m", 0))
        per_instrument.append(
            {
                "instrument": ii,
                "expected_throttle_activation_count": ex,
                "actual_throttle_activation_count": ac,
                "missed_throttle_opportunities": mi,
                "throttle_effectiveness_ratio": _ratio(ac, ex),
            }
        )

    return {
        "expected_throttle_activation_count": day_exp,
        "actual_throttle_activation_count": day_act,
        "missed_throttle_opportunities": day_missed,
        "throttle_effectiveness_ratio": _ratio(day_act, day_exp),
        "per_instrument": per_instrument,
    }


def analyze_progress_quality(
    evs: Sequence[NormEvent],
    thresholds: Dict[str, Any],
) -> Dict[str, Any]:
    revert_ms = float(thresholds.get("iea_integrity_progress_revert_window_ms", 10_000))
    evs_sorted = _sorted_events(evs)

    engagement_ts: Dict[str, datetime] = {}
    explicit_by_inst: Dict[str, List[datetime]] = defaultdict(list)
    for e in evs_sorted:
        inst_e = _instrument(e)
        if inst_e and e.event in (
            "STATE_CONSISTENCY_GATE_ENGAGED",
            "RECONCILIATION_MISMATCH_DETECTED",
            "MISMATCH_DETECTED",
        ):
            engagement_ts.setdefault(inst_e, e.ts_utc)
        if e.event == "RECONCILIATION_PROGRESS_OBSERVED":
            d0 = _data(e)
            kind0 = str(d0.get("kind") or "").strip().lower()
            if kind0 == "baseline_signature":
                continue
            if not inst_e:
                continue
            explicit_by_inst[inst_e].append(e.ts_utc)
    for _ii in explicit_by_inst:
        explicit_by_inst[_ii].sort()

    def _has_explicit_between(prev_gate_ts: Optional[datetime], cur_ts: datetime, inst_key: str) -> bool:
        lst = explicit_by_inst.get(inst_key)
        if not lst:
            return False
        lo_bound = prev_gate_ts if prev_gate_ts is not None else engagement_ts.get(inst_key)
        lo = bisect_right(lst, lo_bound) if lo_bound is not None else 0
        hi = bisect_right(lst, cur_ts)
        return lo < hi

    qb = {
        "mismatch_reduced": 0,
        "mismatch_cleared": 0,
        "ownership_improved": 0,
        "state_only_progress": 0,
        "reverted_progress": 0,
    }
    explicit_n = 0
    inferred_n = 0
    prev_sig: Dict[str, Optional[Dict[str, Any]]] = {}
    prev_gate_ts: Dict[str, Optional[datetime]] = {}
    last_improve_ts: Dict[str, Optional[datetime]] = {}

    per_inst: Dict[str, Dict[str, Any]] = defaultdict(
        lambda: {
            "explicit_progress_events": 0,
            "inferred_progress_events": 0,
            "quality_breakdown": {
                "mismatch_reduced": 0,
                "mismatch_cleared": 0,
                "ownership_improved": 0,
                "state_only_progress": 0,
                "reverted_progress": 0,
            },
        }
    )

    def _score_from_qb(q: Dict[str, int]) -> float:
        good = (
            int(q.get("mismatch_cleared", 0))
            + int(q.get("mismatch_reduced", 0))
            + int(q.get("ownership_improved", 0))
        )
        weak = int(q.get("state_only_progress", 0))
        bad = int(q.get("reverted_progress", 0))
        denom = max(good + weak + bad, 1)
        s = (good - 0.5 * weak - bad) / float(denom)
        return float(max(0.0, min(1.0, s)))

    for e in evs_sorted:
        d = _data(e)
        evn = e.event or ""
        inst = _instrument(e) or ""

        if evn == "RECONCILIATION_PROGRESS_OBSERVED":
            kind = str(d.get("kind") or "").strip().lower()
            if kind == "baseline_signature":
                continue
            if not inst:
                continue
            explicit_n += 1
            per_inst[inst]["explicit_progress_events"] += 1
            bucket = _classify_explicit_kind(kind)
            qb[bucket] += 1  # type: ignore[index]
            per_inst[inst]["quality_breakdown"][bucket] += 1  # type: ignore[index]
            last_improve_ts[inst] = e.ts_utc
            continue

        if evn in ("RECONCILIATION_MISMATCH_CLEARED", "STATE_CONSISTENCY_GATE_RELEASED", "RECONCILIATION_CONVERGED"):
            if not inst:
                continue
            inferred_n += 1
            per_inst[inst]["inferred_progress_events"] += 1
            qb["mismatch_cleared"] += 1
            per_inst[inst]["quality_breakdown"]["mismatch_cleared"] += 1
            last_improve_ts[inst] = e.ts_utc
            continue

        if evn != "STATE_CONSISTENCY_GATE_RECONCILIATION_RESULT":
            continue
        if not inst:
            inst = "_global_"
        cur_sig = _extract_gate_signature(d)
        prv_ts = prev_gate_ts.get(inst)
        prv = prev_sig.get(inst)

        has_explicit = _has_explicit_between(prv_ts, e.ts_utc, inst)

        if prv is not None and _is_worsened(prv, cur_sig):
            lim = last_improve_ts.get(inst)
            if lim is not None and (e.ts_utc - lim).total_seconds() * 1000.0 <= revert_ms:
                qb["reverted_progress"] += 1
                per_inst[inst]["quality_breakdown"]["reverted_progress"] += 1

        if has_explicit:
            prev_gate_ts[inst] = e.ts_utc
            prev_sig[inst] = cur_sig
            continue

        if prv is None:
            prev_gate_ts[inst] = e.ts_utc
            prev_sig[inst] = cur_sig
            continue

        cls = _classify_inferred_cycle(prv, cur_sig)
        if cls == "state_only_progress":
            qb["state_only_progress"] += 1
            per_inst[inst]["quality_breakdown"]["state_only_progress"] += 1
        elif cls in ("mismatch_cleared", "mismatch_reduced", "ownership_improved"):
            inferred_n += 1
            per_inst[inst]["inferred_progress_events"] += 1
            qb[cls] += 1  # type: ignore[index]
            per_inst[inst]["quality_breakdown"][cls] += 1  # type: ignore[index]
            last_improve_ts[inst] = e.ts_utc

        prev_gate_ts[inst] = e.ts_utc
        prev_sig[inst] = cur_sig

    progress_quality_score = round(_score_from_qb(qb), 8)

    gate_instruments: set[str] = set()
    for e in evs_sorted:
        if e.event != "STATE_CONSISTENCY_GATE_RECONCILIATION_RESULT":
            continue
        gi = _instrument(e) or "_global_"
        gate_instruments.add(gi)

    per_instrument_out: List[Dict[str, Any]] = []
    for ii in sorted(per_inst.keys()):
        qbi = per_inst[ii]["quality_breakdown"]
        per_instrument_out.append(
            {
                "instrument": ii,
                "explicit_progress_events": int(per_inst[ii]["explicit_progress_events"]),
                "inferred_progress_events": int(per_inst[ii]["inferred_progress_events"]),
                "quality_breakdown": dict(qbi),
                "progress_quality_score": round(_score_from_qb(qbi), 8),
            }
        )

    have = {row["instrument"] for row in per_instrument_out}
    empty_qb = {
        "mismatch_reduced": 0,
        "mismatch_cleared": 0,
        "ownership_improved": 0,
        "state_only_progress": 0,
        "reverted_progress": 0,
    }
    for gi in sorted(gate_instruments):
        if gi in have:
            continue
        per_instrument_out.append(
            {
                "instrument": gi,
                "explicit_progress_events": 0,
                "inferred_progress_events": 0,
                "quality_breakdown": dict(empty_qb),
                "progress_quality_score": round(_score_from_qb(empty_qb), 8),
            }
        )
    per_instrument_out.sort(key=lambda x: str(x.get("instrument", "")))

    return {
        "explicit_progress_events": explicit_n,
        "inferred_progress_events": inferred_n,
        "quality_breakdown": dict(qb),
        "progress_quality_score": progress_quality_score,
        "per_instrument": per_instrument_out,
    }


def analyze_progress_reconciliation(
    evs: Sequence[NormEvent],
    thresholds: Dict[str, Any],
) -> Dict[str, Any]:
    """
    Derives progress/throttle/waste/stuck KPIs from existing gate + progress control events
    (no new event types required beyond those already emitted by the engine).
    """
    stuck_dur_ms = float(thresholds.get("iea_integrity_stuck_no_progress_duration_ms", 120_000))
    stuck_min_cycles = int(thresholds.get("iea_integrity_stuck_min_cycles", 30))

    total_cycles = 0
    throttled_cycles = 0
    measurable_progress_events = 0
    no_progress_signal_events = 0
    duration_samples: List[float] = []

    per_inst_cycles: Dict[str, int] = defaultdict(int)
    per_inst_throttled: Dict[str, int] = defaultdict(int)
    per_inst_progress: Dict[str, int] = defaultdict(int)
    per_inst_no_progress_emit: Dict[str, int] = defaultdict(int)

    # Episodes: inst -> state
    active: Dict[str, Dict[str, Any]] = {}
    episodes: List[Dict[str, Any]] = []

    def close_episode(inst: str, end_ts: datetime, resolved: bool) -> None:
        st = active.pop(inst, None)
        if not st:
            return
        start = st["start"]
        fp = st.get("first_progress_ts")
        cycles = int(st.get("cycles", 0))
        mp = int(st.get("measurable_progress", 0))
        tfp = None
        if fp is not None:
            tfp = (fp - start).total_seconds() * 1000.0
        tres = None
        if resolved:
            tres = (end_ts - start).total_seconds() * 1000.0
        episodes.append(
            {
                "instrument": inst,
                "started_ts_utc": start.isoformat().replace("+00:00", "Z"),
                "ended_ts_utc": end_ts.isoformat().replace("+00:00", "Z"),
                "resolved": resolved,
                "reconciliation_cycles": cycles,
                "measurable_progress_events": mp,
                "time_to_first_progress_ms": round(tfp, 2) if tfp is not None else None,
                "time_to_resolution_ms": round(tres, 2) if tres is not None else None,
            }
        )

    for e in evs:
        d = _data(e)
        evn = e.event or ""
        inst = _instrument(e)

        if evn == "STATE_CONSISTENCY_GATE_RECONCILIATION_RESULT":
            total_cycles += 1
            if inst:
                per_inst_cycles[inst] += 1
            if _truthy(d, "gate_reconciliation_throttled", "gateReconciliationThrottled"):
                throttled_cycles += 1
                if inst:
                    per_inst_throttled[inst] += 1
            dm = _duration_ms_from_gate_result(d)
            if dm is not None:
                duration_samples.append(dm)
            if inst and inst in active:
                active[inst]["cycles"] = int(active[inst].get("cycles", 0)) + 1
                active[inst]["last_ts"] = e.ts_utc

        if evn == "RECONCILIATION_PROGRESS_OBSERVED":
            kind = str(d.get("kind") or "").strip().lower()
            if kind == "baseline_signature":
                continue
            measurable_progress_events += 1
            if inst:
                per_inst_progress[inst] += 1
            if inst and inst in active:
                if active[inst].get("first_progress_ts") is None:
                    active[inst]["first_progress_ts"] = e.ts_utc
                active[inst]["measurable_progress"] = int(active[inst].get("measurable_progress", 0)) + 1

        if evn == "RECONCILIATION_NO_PROGRESS_DETECTED":
            no_progress_signal_events += 1
            if inst:
                per_inst_no_progress_emit[inst] += 1

        if evn in ("STATE_CONSISTENCY_GATE_RELEASED", "RECONCILIATION_MISMATCH_CLEARED"):
            if inst:
                close_episode(inst, e.ts_utc, resolved=True)
            continue

        if evn in ("STATE_CONSISTENCY_GATE_ENGAGED", "RECONCILIATION_MISMATCH_DETECTED"):
            if inst and inst not in active:
                active[inst] = {
                    "start": e.ts_utc,
                    "cycles": 0,
                    "measurable_progress": 0,
                    "first_progress_ts": None,
                    "last_ts": e.ts_utc,
                }

    # Close open episodes at last activity for that instrument
    for inst in list(active.keys()):
        st = active.get(inst)
        if not st:
            continue
        end_ts = st.get("last_ts") or st["start"]
        close_episode(inst, end_ts, resolved=False)

    wasted_cycles = max(0, total_cycles - measurable_progress_events)
    if measurable_progress_events > total_cycles:
        wasted_cycles = 0

    avg_cycle_ms = round(sum(duration_samples) / len(duration_samples), 4) if duration_samples else None
    est_cpu_waste_ms = None
    if avg_cycle_ms is not None and wasted_cycles > 0:
        est_cpu_waste_ms = round(wasted_cycles * avg_cycle_ms, 2)

    progress_efficiency_ratio = None
    if total_cycles > 0:
        progress_efficiency_ratio = round(measurable_progress_events / total_cycles, 8)

    wasted_work_ratio = round(wasted_cycles / total_cycles, 8) if total_cycles > 0 else None

    per_instrument: List[Dict[str, Any]] = []
    all_inst = set(per_inst_cycles) | set(per_inst_progress) | set(per_inst_throttled)
    for ii in sorted(all_inst):
        cyc = per_inst_cycles.get(ii, 0)
        pe = per_inst_progress.get(ii, 0)
        thr = per_inst_throttled.get(ii, 0)
        npe = max(0, cyc - pe) if cyc >= pe else 0
        per_instrument.append(
            {
                "instrument": ii,
                "total_reconciliation_cycles": cyc,
                "progress_events": pe,
                "no_progress_cycles": npe,
                "throttled_cycles": thr,
                "progress_efficiency_ratio": round(pe / cyc, 8) if cyc > 0 else None,
                "wasted_reconciliation_cycles": npe,
                "wasted_work_ratio": round(npe / cyc, 8) if cyc > 0 else None,
            }
        )

    # Stuck: zero measurable progress in episode, enough cycles, long enough duration
    stuck_state_detected = False
    stuck_duration_ms: Optional[float] = None
    stuck_reasons: List[str] = []
    episode_stuck_hit = False
    for ep in episodes:
        if ep.get("measurable_progress_events", 0) != 0:
            continue
        cyc = int(ep.get("reconciliation_cycles", 0))
        if cyc < stuck_min_cycles:
            continue
        t0 = ep.get("started_ts_utc")
        t1 = ep.get("ended_ts_utc")
        if not t0 or not t1:
            continue
        try:
            a = datetime.fromisoformat(t0.replace("Z", "+00:00"))
            b = datetime.fromisoformat(t1.replace("Z", "+00:00"))
            dur = (b - a).total_seconds() * 1000.0
        except Exception:
            continue
        if dur < stuck_dur_ms:
            continue
        stuck_state_detected = True
        stuck_duration_ms = max(stuck_duration_ms or 0.0, dur)
        episode_stuck_hit = True
    if episode_stuck_hit:
        stuck_reasons.append("episode_zero_progress_long")

    first_gate_ts: Optional[datetime] = None
    last_gate_ts: Optional[datetime] = None
    for e in evs:
        if e.event == "STATE_CONSISTENCY_GATE_RECONCILIATION_RESULT":
            if first_gate_ts is None:
                first_gate_ts = e.ts_utc
            last_gate_ts = e.ts_utc

    if (
        measurable_progress_events == 0
        and total_cycles >= stuck_min_cycles
        and first_gate_ts is not None
        and last_gate_ts is not None
    ):
        span_ms = (last_gate_ts - first_gate_ts).total_seconds() * 1000.0
        if span_ms >= stuck_dur_ms:
            stuck_state_detected = True
            stuck_duration_ms = max(stuck_duration_ms or 0.0, span_ms)
            stuck_reasons.append("day_span_zero_progress")

    day_totals = {
        "total_reconciliation_cycles": total_cycles,
        "progress_events": measurable_progress_events,
        "no_progress_cycles": wasted_cycles,
        "throttled_cycles": throttled_cycles,
        "progress_efficiency_ratio": progress_efficiency_ratio,
        "wasted_reconciliation_cycles": wasted_cycles,
        "wasted_work_ratio": wasted_work_ratio,
        "no_progress_signal_events": no_progress_signal_events,
        "avg_gate_reconciliation_duration_ms": avg_cycle_ms,
        "estimated_cpu_waste_ms": est_cpu_waste_ms,
    }

    return {
        "day_totals": day_totals,
        "per_instrument": sorted(per_instrument, key=lambda x: str(x.get("instrument", ""))),
        "mismatch_episodes": episodes,
        "stuck_state": {
            "stuck_state_detected": stuck_state_detected,
            "stuck_duration_ms": round(stuck_duration_ms, 2) if stuck_duration_ms is not None else None,
            "stuck_reasons": stuck_reasons,
            "thresholds": {
                "min_cycles": stuck_min_cycles,
                "no_progress_duration_ms": stuck_dur_ms,
            },
        },
    }


def _count_events_by_name(evs: Sequence[NormEvent], names: Tuple[str, ...]) -> int:
    nset = set(names)
    return sum(1 for e in evs if (e.event or "") in nset)


def _engine_load_peak_high(engine_load: Optional[Dict[str, Any]]) -> bool:
    if not engine_load:
        return False
    lc = str(engine_load.get("load_classification") or "").upper()
    if lc in ("HIGH", "CRITICAL"):
        return True
    ct = engine_load.get("classification_thresholds") or {}
    peak_high = float(ct.get("peak_eps_high") or 0)
    psw = float(engine_load.get("peak_subwindow_eps") or 0)
    return peak_high > 0 and psw >= peak_high


def analyze_iea_root_cause(
    throttle_correctness: Dict[str, Any],
    pr_day: Dict[str, Any],
    pr_stuck: Dict[str, Any],
    lifecycle_wrong_adapter_count: int,
    iea_resolution_suspect_count: int,
    engine_load: Optional[Dict[str, Any]],
    thresholds: Dict[str, Any],
) -> Dict[str, Any]:
    """
    Rule-based mapping from existing KPIs to human-readable root-cause hypotheses.
    Multiple rules may match; `primary` is the highest-priority case.
    """
    min_exp_thr = int(thresholds.get("iea_root_cause_min_expected_throttle", 5))
    min_storms = int(thresholds.get("iea_root_cause_min_storms_for_throughput", 2))

    tc_exp = int(throttle_correctness.get("expected_throttle_activation_count") or 0)
    tc_act = int(throttle_correctness.get("actual_throttle_activation_count") or 0)
    per = pr_day.get("progress_efficiency_ratio")
    try:
        per_f = float(per) if per is not None else 0.0
    except Exception:
        per_f = 0.0
    stuck = bool(pr_stuck.get("stuck_state_detected"))

    matches: List[Dict[str, Any]] = []

    if tc_exp >= min_exp_thr and tc_act == 0 and per_f == 0.0 and stuck:
        matches.append(
            {
                "priority": 1,
                "case_id": "CASE_1_THROTTLE_NOT_ACTIVATING",
                "root_cause": "THROTTLE_NOT_ACTIVATING",
                "likely_reason": "cooldown reset or skipExpensive never true; gate_reconciliation_throttled not observed when expected",
                "confidence": "HIGH",
            }
        )

    if lifecycle_wrong_adapter_count > 0 and iea_resolution_suspect_count > 0:
        matches.append(
            {
                "priority": 2,
                "case_id": "CASE_2_ORDER_REGISTRY_TIMING_OR_MAPPING",
                "root_cause": "ORDER_REGISTRY_TIMING_OR_MAPPING",
                "likely_reason": "lifecycle routing errors co-occur with broker-before-IEA adoption suspect signals",
                "confidence": "MEDIUM",
            }
        )

    storms = (engine_load or {}).get("storms") or []
    n_storms = len(storms) if isinstance(storms, list) else 0
    peak_hi = _engine_load_peak_high(engine_load)
    if peak_hi and n_storms >= min_storms and not stuck:
        matches.append(
            {
                "priority": 3,
                "case_id": "CASE_3_DATA_THROUGHPUT_LIMIT",
                "root_cause": "DATA_THROUGHPUT_LIMIT",
                "likely_reason": "high engine event rate and storm bursts without stuck-state pattern — likely feed/throughput saturation",
                "confidence": "MEDIUM",
            }
        )

    matches.sort(key=lambda x: (int(x.get("priority", 99)), str(x.get("case_id", ""))))
    primary = matches[0] if matches else None
    return {
        "rules_version": "2026-03-24-v1",
        "thresholds_used": {
            "min_expected_throttle": min_exp_thr,
            "min_storms_for_throughput": min_storms,
        },
        "primary": primary,
        "all_matches": matches,
    }


def build_iea_expectation_vs_observed(
    throttle_correctness: Dict[str, Any],
    pr_day: Dict[str, Any],
    evs: Sequence[NormEvent],
) -> Dict[str, Any]:
    """Compact expected vs observed table for throttle, progress, and adoption."""
    tc_exp = int(throttle_correctness.get("expected_throttle_activation_count") or 0)
    tc_act = int(throttle_correctness.get("actual_throttle_activation_count") or 0)
    cycles = int(pr_day.get("total_reconciliation_cycles") or 0)
    prog_obs = int(pr_day.get("progress_events") or 0)

    adoption_started = _count_events_by_name(
        evs,
        ("IEA_ADOPTION_SCAN_EXECUTION_STARTED", "ADOPTION_SCAN_START"),
    )
    adoption_success = _count_events_by_name(
        evs,
        (
            "ORDER_REGISTRY_ADOPTED",
            "ADOPTION_SUCCESS",
            "RECONCILIATION_RECOVERY_ADOPTION_SUCCESS",
        ),
    )

    return {
        "throttle_activation": {
            "mechanism": "throttle",
            "expected": tc_exp,
            "observed": tc_act,
            "notes": "expected = cycles where no-progress threshold says throttle should activate; observed = gate_reconciliation_throttled true",
        },
        "progress_events": {
            "mechanism": "measurable_progress",
            "expected": cycles,
            "observed": prog_obs,
            "notes": "expected = total gate reconciliation cycles (ideal 1:1 progress signal per cycle); observed = RECONCILIATION_PROGRESS_OBSERVED (non-baseline)",
        },
        "adoption_success": {
            "mechanism": "adoption",
            "expected": adoption_started,
            "observed": adoption_success,
            "notes": "expected = adoption scan executions started; observed = successful adoption events",
        },
    }


def analyze_time_spent_in_states(
    evs: Sequence[NormEvent],
    window_start: datetime,
    window_end: datetime,
) -> Dict[str, Any]:
    """
    Approximate share of audit window spent in throttled vs reconciling-like states
    using STATE_CONSISTENCY_GATE_RECONCILIATION_RESULT segments.
    """
    total_sec = max(0.0, (window_end - window_start).total_seconds())
    if total_sec <= 0:
        return {
            "note": "invalid_or_empty_window",
            "total_window_seconds": 0.0,
            "seconds_by_state": {},
            "percentages": {},
        }

    gate_results = [e for e in evs if e.event == "STATE_CONSISTENCY_GATE_RECONCILIATION_RESULT"]
    gate_results.sort(key=lambda e: (e.ts_utc, _tie_break_event_order(e)))

    buckets: Dict[str, float] = defaultdict(float)

    prev = window_start
    for i, e in enumerate(gate_results):
        e_ts = e.ts_utc
        if e_ts > window_start and prev < e_ts:
            idle_lead = (min(e_ts, window_end) - max(prev, window_start)).total_seconds()
            if idle_lead > 0:
                buckets["idle_unobserved"] += idle_lead

        next_ts = gate_results[i + 1].ts_utc if i + 1 < len(gate_results) else window_end
        next_ts = min(next_ts, window_end)
        seg_start = max(e_ts, window_start)
        if next_ts <= seg_start:
            prev = max(prev, e_ts)
            continue
        dur = (next_ts - seg_start).total_seconds()
        if dur <= 0:
            prev = next_ts
            continue

        d = _data(e)
        if _truthy(d, "gate_reconciliation_throttled", "gateReconciliationThrottled"):
            key = "throttled"
        else:
            gs = str(d.get("gate_state") or "").strip().lower()
            if "reconcil" in gs:
                key = "reconciling"
            elif "persistent" in gs:
                key = "persistent_mismatch"
            elif "stable" in gs or "pending" in gs:
                key = "stable_pending_release"
            elif gs:
                key = f"gate_state_{gs[:48]}"
            else:
                key = "reconciling_unknown"
        buckets[key] += dur
        prev = next_ts

    if prev < window_end:
        buckets["idle_unobserved"] += (window_end - prev).total_seconds()

    ssum = sum(buckets.values())
    if ssum < total_sec - 1e-3:
        buckets["idle_unobserved"] += total_sec - ssum
    pct = {k: round(100.0 * v / total_sec, 2) for k, v in sorted(buckets.items(), key=lambda x: -x[1])}
    return {
        "total_window_seconds": round(total_sec, 3),
        "seconds_by_state": dict(sorted(buckets.items(), key=lambda x: -x[1])),
        "percentages": pct,
    }


def analyze_observability_integrity(
    evs: Sequence[NormEvent],
    thresholds: Dict[str, Any],
    missed_throttle_opportunities: int = 0,
) -> Dict[str, Any]:
    """Heuristic signal reliability: gaps, sequence anomalies, missing throttle when expected."""
    gap_warn = float(thresholds.get("iea_integrity_observability_gap_warn_seconds", 300))
    evs_sorted = _sorted_events(evs)

    max_gap_sec = 0.0
    prev_ts: Optional[datetime] = None
    for e in evs_sorted:
        if prev_ts is not None:
            max_gap_sec = max(max_gap_sec, (e.ts_utc - prev_ts).total_seconds())
        prev_ts = e.ts_utc

    has_gate_started = any(
        (e.event or "") == "STATE_CONSISTENCY_GATE_RECONCILIATION_STARTED" for e in evs_sorted
    )
    sequence_anomaly_count = 0
    saw_start: Dict[str, bool] = {}
    if has_gate_started:
        for e in evs_sorted:
            inst = _instrument(e)
            evn = e.event or ""
            if evn == "STATE_CONSISTENCY_GATE_RECONCILIATION_STARTED":
                if inst:
                    saw_start[inst] = True
            elif evn == "STATE_CONSISTENCY_GATE_RECONCILIATION_RESULT":
                if inst and not saw_start.get(inst):
                    sequence_anomaly_count += 1
                if inst:
                    saw_start[inst] = False

    missed_throttle = int(missed_throttle_opportunities)

    gap_flag = max_gap_sec >= gap_warn
    reliability = 1.0
    if gap_flag:
        reliability -= 0.15
    if sequence_anomaly_count > 0:
        reliability -= min(0.3, 0.05 * sequence_anomaly_count)
    if missed_throttle > 5:
        reliability -= min(0.3, 0.02 * missed_throttle)
    reliability = round(max(0.0, min(1.0, reliability)), 4)

    return {
        "max_inter_event_gap_seconds": round(max_gap_sec, 3),
        "gap_warn_threshold_seconds": gap_warn,
        "large_event_gap_flag": gap_flag,
        "gate_started_events_present": has_gate_started,
        "gate_result_without_prior_start_count": sequence_anomaly_count,
        "missed_throttle_opportunities": missed_throttle,
        "signal_reliability_score": reliability,
        "notes": "reliability_score is heuristic; sequence check runs only if STATE_CONSISTENCY_GATE_RECONCILIATION_STARTED appears in stream",
    }


def analyze_cross_layer_correlations(
    engine_load: Optional[Dict[str, Any]],
    pr_stuck: Dict[str, Any],
    evs: Sequence[NormEvent],
    thresholds: Dict[str, Any],
) -> Dict[str, Any]:
    """Lightweight cross-layer hints tying load, stuck state, disconnects, and mismatches."""
    stuck = bool(pr_stuck.get("stuck_state_detected"))
    recon_ratio = float((engine_load or {}).get("reconciliation_event_ratio") or 0)
    r_thr = float(
        ((engine_load or {}).get("classification_thresholds") or {}).get("reconciliation_ratio_upgrade") or 0.5
    )
    peak_sw = float((engine_load or {}).get("peak_subwindow_eps") or 0)
    peak_high = float(((engine_load or {}).get("classification_thresholds") or {}).get("peak_eps_high") or 0)
    high_eps = peak_high > 0 and peak_sw >= peak_high

    disconnect_n = sum(
        1
        for e in evs
        if "CONNECTION" in (e.event or "").upper() and "LOST" in (e.event or "").upper()
    )
    mismatch_n = sum(1 for e in evs if (e.event or "") == "RECONCILIATION_MISMATCH_DETECTED")
    mm_thr = int(thresholds.get("iea_integrity_correlation_mismatch_spike_min", 15))

    rules: List[Dict[str, Any]] = []

    if high_eps and stuck and recon_ratio >= r_thr:
        rules.append(
            {
                "id": "high_cpu_stuck_and_recon_ratio",
                "fired": True,
                "interpretation": "High event rate + stuck state + elevated reconciliation mix — reconciliation loop or thrash likely",
            }
        )
    else:
        rules.append({"id": "high_cpu_stuck_and_recon_ratio", "fired": False, "interpretation": ""})

    if disconnect_n > 0 and mismatch_n >= mm_thr:
        rules.append(
            {
                "id": "disconnect_and_mismatch_spike",
                "fired": True,
                "interpretation": "Disconnects co-occur with mismatch spike — possible data gap or stale book during reconnect",
            }
        )
    else:
        rules.append({"id": "disconnect_and_mismatch_spike", "fired": False, "interpretation": ""})

    if high_eps and not stuck and mismatch_n >= mm_thr:
        rules.append(
            {
                "id": "high_throughput_mismatch_without_stuck",
                "fired": True,
                "interpretation": "High throughput with many mismatches but no stuck flag — check feed burst vs mapping lag",
            }
        )
    else:
        rules.append({"id": "high_throughput_mismatch_without_stuck", "fired": False, "interpretation": ""})

    return {
        "disconnect_events": disconnect_n,
        "reconciliation_mismatch_detected_events": mismatch_n,
        "rules": [r for r in rules if r.get("fired")],
        "rules_evaluated": rules,
    }


def analyze_cpu_root_cause_link(
    engine_load: Optional[Dict[str, Any]],
    iea_root_cause: Dict[str, Any],
    thresholds: Dict[str, Any],
) -> Dict[str, Any]:
    """Link engine load shape to CPU hypothesis and optional alignment with IEA root cause."""
    if not engine_load:
        return {
            "cpu_cause_hypothesis": None,
            "evidence": [],
            "aligned_iea_root_cause": None,
        }

    peak_sw = float(engine_load.get("peak_subwindow_eps") or 0)
    recon_ratio = float(engine_load.get("reconciliation_event_ratio") or 0)
    ct = engine_load.get("classification_thresholds") or {}
    peak_high = float(ct.get("peak_eps_high") or 0)
    r_thr = float(ct.get("reconciliation_ratio_upgrade") or 0.5)

    loops = engine_load.get("loop_signatures") or []
    has_recon_loop = any(
        (lp.get("loop_classification") or "") == "RECONCILIATION_LOOP" for lp in (loops if isinstance(loops, list) else [])
    )

    evidence: List[str] = []
    hyp: Optional[str] = None
    if peak_high > 0 and peak_sw >= peak_high and recon_ratio >= r_thr:
        hyp = "RECONCILIATION_LOOP_OR_DOMINANT_RECONCILIATION_WORK"
        evidence.append(f"peak_subwindow_eps={peak_sw} >= peak_eps_high={peak_high}")
        evidence.append(f"reconciliation_event_ratio={recon_ratio} >= {r_thr}")
    if has_recon_loop:
        hyp = hyp or "RECONCILIATION_LOOP"
        evidence.append("loop_signatures includes RECONCILIATION_LOOP")

    primary_rc = (iea_root_cause.get("primary") or {}) if isinstance(iea_root_cause, dict) else None
    rc_name = (primary_rc or {}).get("root_cause") if isinstance(primary_rc, dict) else None
    aligned = None
    if hyp and rc_name == "THROTTLE_NOT_ACTIVATING":
        aligned = "THROTTLE_NOT_ACTIVATING"
        evidence.append("matches IEA primary root cause (throttle never activated under load)")

    return {
        "cpu_cause_hypothesis": hyp,
        "evidence": evidence,
        "aligned_iea_root_cause": aligned,
    }


def analyze_iea_integrity(
    events: Sequence[NormEvent],
    thresholds: Dict[str, Any],
    engine_load_analysis: Optional[Dict[str, Any]] = None,
    audit_window_start: Optional[datetime] = None,
    audit_window_end: Optional[datetime] = None,
) -> Dict[str, Any]:
    """
    Single-pass IEA integrity analysis on deduped NormEvent stream for the audit day window.
    """
    sus_window_ms = float(thresholds.get("iea_integrity_adoption_followup_window_ms", 2000))
    cross_window_ms = float(thresholds.get("iea_integrity_cross_iea_window_ms", 400))
    crit_res = int(thresholds.get("iea_integrity_resolution_suspect_critical", 25))
    crit_life = int(thresholds.get("iea_integrity_lifecycle_critical", 50))
    crit_dup = int(thresholds.get("iea_integrity_cross_iea_duplicate_critical", 30))
    crit_eff = float(thresholds.get("iea_integrity_efficiency_critical", 0.05))

    evs = sorted(events, key=lambda e: e.ts_utc)

    # --- A: resolution suspect (broker working, no local/IEA ownership) + adoption within window ---
    iea_resolution_suspect_count = 0
    pending_suspect: deque[Tuple[datetime, str]] = deque()

    for e in evs:
        while pending_suspect and (e.ts_utc - pending_suspect[0][0]).total_seconds() * 1000.0 > sus_window_ms:
            pending_suspect.popleft()

        d = _data(e)
        inst = (e.instrument or d.get("instrument") or "").strip()
        evn = e.event or ""

        if evn in ("ORDER_REGISTRY_ADOPTED", "ADOPTION_SUCCESS", "RECONCILIATION_RECOVERY_ADOPTION_SUCCESS"):
            i2 = (e.instrument or d.get("instrument") or "").strip()
            if i2:
                tmp: deque[Tuple[datetime, str]] = deque()
                matched = False
                while pending_suspect:
                    ts0, pi = pending_suspect.popleft()
                    if (
                        not matched
                        and pi.upper() == i2.upper()
                        and 0 < (e.ts_utc - ts0).total_seconds() * 1000.0 <= sus_window_ms
                    ):
                        iea_resolution_suspect_count += 1
                        matched = True
                    else:
                        tmp.append((ts0, pi))
                pending_suspect.extend(tmp)
            continue

        if evn not in ("RECONCILIATION_MISMATCH_DETECTED", "RECONCILIATION_ASSEMBLE_MISMATCH_DIAG"):
            continue

        bw = _int_field(d, "broker_working", "broker_working_count")
        loc = _int_field(d, "local_working", "iea_owned_count", "local_working_count")
        if bw is None or loc is None:
            summ = str(d.get("summary") or "")
            pb, pl = _parse_summary_broker_local(summ)
            if bw is None:
                bw = pb
            if loc is None:
                loc = pl
        if bw is None or loc is None:
            continue
        if bw > 0 and loc == 0 and inst:
            pending_suspect.append((e.ts_utc, inst))

    # --- B: registry timing gap — submit → ORM(ORDER_REGISTRY_MISSING) on instrument → adopt same intent ---
    intent_submit: Dict[str, Tuple[datetime, str]] = {}
    inst_orm_ts: Dict[str, List[datetime]] = defaultdict(list)
    delays_ms: List[float] = []

    for e in evs:
        d = _data(e)
        evn = e.event or ""
        intent = _str_field(d, "intent_id", "intentId")
        inst = (e.instrument or d.get("instrument") or "").strip()

        if evn == "ORDER_SUBMIT_SUCCESS" and intent and inst:
            intent_submit[intent] = (e.ts_utc, inst)

        if evn == "RECONCILIATION_MISMATCH_DETECTED" and _mismatch_type(d) == "ORDER_REGISTRY_MISSING" and inst:
            inst_orm_ts[inst].append(e.ts_utc)

        if evn == "ORDER_REGISTRY_ADOPTED" and intent:
            sub = intent_submit.get(intent)
            if not sub:
                continue
            t0, inst0 = sub
            if not inst0 or inst0.upper() != (inst or "").strip().upper():
                continue
            ts_list = inst_orm_ts.get(inst0, [])
            if not ts_list:
                continue
            lo = bisect_right(ts_list, t0)
            hi = bisect_right(ts_list, e.ts_utc)
            if lo < hi:
                delta = (e.ts_utc - t0).total_seconds() * 1000.0
                if delta >= 0:
                    delays_ms.append(delta)

    registry_timing_gap: Dict[str, Any] = {
        "count": len(delays_ms),
        "avg_delay_ms": round(sum(delays_ms) / len(delays_ms), 2) if delays_ms else None,
        "max_delay_ms": round(max(delays_ms), 2) if delays_ms else None,
    }

    # --- C: lifecycle wrong adapter (CREATED + ENTRY_ACCEPTED invalid) ---
    lifecycle_wrong_adapter_count = 0
    for e in evs:
        if e.event != "INTENT_LIFECYCLE_TRANSITION_INVALID":
            continue
        d = _data(e)
        cur = str(d.get("currentState") or d.get("current_state") or "").strip().upper()
        att = str(d.get("attemptedTransition") or d.get("attempted_transition") or "").strip().upper()
        if cur == "CREATED" and att == "ENTRY_ACCEPTED":
            lifecycle_wrong_adapter_count += 1

    # --- D: reconciliation efficiency per instrument ---
    gate_runs: Dict[str, int] = defaultdict(int)
    progress: Dict[str, int] = defaultdict(int)
    for e in evs:
        inst = (e.instrument or _data(e).get("instrument") or "").strip()
        if not inst:
            inst = "_global_"
        evn = e.event or ""
        if evn == "STATE_CONSISTENCY_GATE_RECONCILIATION_RESULT":
            gate_runs[inst] += 1
        if evn in (
            "STATE_CONSISTENCY_GATE_RELEASED",
            "RECONCILIATION_CONVERGED",
            "RECONCILIATION_MISMATCH_CLEARED",
        ):
            progress[inst] += 1

    eff_by_inst: Dict[str, float] = {}
    for inst, gr in gate_runs.items():
        pr = progress.get(inst, 0)
        eff_by_inst[inst] = round(pr / max(1, gr), 6)

    avg_eff: Optional[float] = None
    if eff_by_inst:
        avg_eff = round(sum(eff_by_inst.values()) / len(eff_by_inst), 6)

    total_gate = sum(gate_runs.values())
    low_efficiency_instruments: List[Dict[str, Any]] = sorted(
        [
            {"instrument": i, "efficiency": eff_by_inst[i], "gate_runs": gate_runs[i]}
            for i in eff_by_inst
            if eff_by_inst[i] < 0.15 and gate_runs.get(i, 0) >= 3
        ],
        key=lambda x: float(x["efficiency"]),
    )[:20]

    reconciliation_efficiency: Dict[str, Any] = {
        "avg_efficiency": avg_eff,
        "low_efficiency_instruments": low_efficiency_instruments,
    }

    # --- E: cross-IEA duplicate (same type + key, ~400ms slot, different run / iea id) ---
    cross_iea_duplicate_events = 0
    buckets: Dict[Tuple[str, str, str, int], List[Tuple[str, str]]] = defaultdict(list)
    for e in evs:
        d = _data(e)
        evn = e.event or ""
        if not evn:
            continue
        intent = _str_field(d, "intent_id", "intentId", "intent")
        bo = _str_field(d, "broker_order_id", "brokerOrderId")
        rid = str(d.get("run_id") or d.get("runId") or "")
        iid = str(d.get("iea_instance_id") or d.get("ieaInstanceId") or "")
        inst = (e.instrument or d.get("instrument") or "").strip()
        key_id = intent or bo
        if not key_id:
            continue
        slot = int(e.ts_utc.timestamp() * 1000 // cross_window_ms)
        k = (evn, inst, key_id, slot)
        buckets[k].append((rid, iid))

    for _k, rows in buckets.items():
        if len(rows) < 2:
            continue
        run_ids = {r[0] for r in rows if r[0]}
        iea_ids = {r[1] for r in rows if r[1]}
        if len(run_ids) > 1 or len(iea_ids) > 1:
            cross_iea_duplicate_events += len(rows) - 1

    # --- Progress reconciliation KPIs (gate + progress control events) ---
    progress_reconciliation = analyze_progress_reconciliation(evs, thresholds)
    pr_day = progress_reconciliation.get("day_totals") or {}
    pr_stuck = progress_reconciliation.get("stuck_state") or {}
    ep_list = progress_reconciliation.get("mismatch_episodes") or []
    max_episodes_export = int(thresholds.get("iea_integrity_max_episodes_in_report", 50))
    if len(ep_list) > max_episodes_export:
        progress_reconciliation = {
            **progress_reconciliation,
            "mismatch_episodes": ep_list[:max_episodes_export],
            "mismatch_episodes_truncated": True,
            "mismatch_episodes_total": len(ep_list),
        }
    else:
        progress_reconciliation = {**progress_reconciliation, "mismatch_episodes_truncated": False}

    throttle_correctness = analyze_throttle_correctness(evs, thresholds)
    progress_quality = analyze_progress_quality(evs, thresholds)

    # --- Status ---
    findings: List[str] = []
    status = "OK"

    if iea_resolution_suspect_count >= crit_res:
        findings.append(
            f"High iea_resolution_suspect_count ({iea_resolution_suspect_count}) — broker working before IEA ownership; "
            f"adoption followed within {sus_window_ms:g}ms"
        )
    if lifecycle_wrong_adapter_count >= crit_life:
        findings.append(
            f"High lifecycle_wrong_adapter_count ({lifecycle_wrong_adapter_count}) — CREATED→ENTRY_ACCEPTED rejected"
        )
    if cross_iea_duplicate_events >= crit_dup:
        findings.append(
            f"High cross_iea_duplicate_events ({cross_iea_duplicate_events}) — duplicate signals across run_id/iea_instance_id"
        )
    if avg_eff is not None and avg_eff < crit_eff and total_gate >= 10:
        findings.append(f"Low avg reconciliation efficiency ({avg_eff}) vs gate runs (total_gate_runs={total_gate})")

    per = pr_day.get("progress_efficiency_ratio")
    tot_c = int(pr_day.get("total_reconciliation_cycles") or 0)
    wasted_r = pr_day.get("wasted_work_ratio")
    if (
        per is not None
        and tot_c >= 20
        and per < float(thresholds.get("iea_integrity_progress_efficiency_warn", 0.05))
    ):
        findings.append(
            f"Low progress efficiency ({per}) over {tot_c} gate reconciliation cycles "
            f"(progress_events={pr_day.get('progress_events')}, throttled_cycles={pr_day.get('throttled_cycles')})"
        )
    if wasted_r is not None and tot_c >= 30 and wasted_r > float(thresholds.get("iea_integrity_wasted_work_warn", 0.8)):
        findings.append(
            f"High wasted reconciliation work: wasted_work_ratio={wasted_r} "
            f"(wasted_cycles={pr_day.get('wasted_reconciliation_cycles')})"
        )
    if pr_stuck.get("stuck_state_detected"):
        findings.append(
            f"Stuck state: no progress for ≥{pr_stuck.get('thresholds', {}).get('no_progress_duration_ms')}ms "
            f"with ≥{pr_stuck.get('thresholds', {}).get('min_cycles')} cycles (stuck_duration_ms={pr_stuck.get('stuck_duration_ms')})"
        )

    tc_exp = int(throttle_correctness.get("expected_throttle_activation_count") or 0)
    tc_act = int(throttle_correctness.get("actual_throttle_activation_count") or 0)
    tc_missed = int(throttle_correctness.get("missed_throttle_opportunities") or 0)
    warn_missed = int(thresholds.get("iea_integrity_throttle_warn_missed", 1))
    if warn_missed > 0 and tc_missed >= warn_missed:
        findings.append(
            f"Throttle correctness: missed_throttle_opportunities={tc_missed} "
            f"(expected={tc_exp}, actual={tc_act}, effectiveness={throttle_correctness.get('throttle_effectiveness_ratio')})"
        )
    if tc_exp > 0 and tc_act == 0:
        findings.append(
            f"Throttle control gap: expected_throttle_activation_count={tc_exp} but actual_throttle_activation_count=0 "
            f"(gate_reconciliation_throttled never true when audit expected throttle)"
        )

    pq_score = progress_quality.get("progress_quality_score")
    pq_warn = float(thresholds.get("iea_integrity_progress_quality_warn", 0.2))
    if pq_score is not None and pq_score < pq_warn:
        findings.append(
            f"Low progress quality score ({pq_score}) vs threshold ({pq_warn}) "
            f"(explicit={progress_quality.get('explicit_progress_events')}, inferred={progress_quality.get('inferred_progress_events')})"
        )

    iea_root_cause = analyze_iea_root_cause(
        throttle_correctness,
        pr_day,
        pr_stuck,
        lifecycle_wrong_adapter_count,
        iea_resolution_suspect_count,
        engine_load_analysis,
        thresholds,
    )
    iea_expectation_vs_observed = build_iea_expectation_vs_observed(throttle_correctness, pr_day, evs)
    if audit_window_start is not None and audit_window_end is not None:
        ws, we = audit_window_start, audit_window_end
    elif evs:
        ws, we = evs[0].ts_utc, evs[-1].ts_utc
    else:
        ws = we = None
    if ws is not None and we is not None:
        time_spent_in_states = analyze_time_spent_in_states(evs, ws, we)
    else:
        time_spent_in_states = {
            "note": "insufficient_window",
            "total_window_seconds": 0.0,
            "seconds_by_state": {},
            "percentages": {},
        }
    observability_integrity = analyze_observability_integrity(evs, thresholds, tc_missed)
    correlations = analyze_cross_layer_correlations(engine_load_analysis, pr_stuck, evs, thresholds)
    cpu_root_cause_link = analyze_cpu_root_cause_link(engine_load_analysis, iea_root_cause, thresholds)

    rc_primary = iea_root_cause.get("primary")
    if isinstance(rc_primary, dict) and rc_primary.get("root_cause"):
        findings.append(
            f"IEA root cause hypothesis [{rc_primary.get('case_id')}]: {rc_primary.get('root_cause')} — "
            f"{rc_primary.get('likely_reason')}"
        )

    if (
        iea_resolution_suspect_count >= crit_res
        or lifecycle_wrong_adapter_count >= crit_life
        or cross_iea_duplicate_events >= crit_dup
        or (avg_eff is not None and avg_eff < crit_eff and total_gate >= 10)
    ):
        status = "CRITICAL"
    elif (
        iea_resolution_suspect_count > 0
        or lifecycle_wrong_adapter_count > 0
        or cross_iea_duplicate_events > 0
        or (registry_timing_gap["count"] or 0) > 0
        or (avg_eff is not None and avg_eff < 0.15 and total_gate >= 5)
    ):
        status = "WARNING"

    if pr_stuck.get("stuck_state_detected") and status == "OK":
        status = "WARNING"

    if status == "OK" and not findings:
        findings.append("No IEA integrity alerts for configured thresholds (or insufficient signals in window).")

    return {
        "status": status,
        "metrics": {
            "iea_resolution_suspect_count": iea_resolution_suspect_count,
            "registry_timing_gap": registry_timing_gap,
            "lifecycle_wrong_adapter_count": lifecycle_wrong_adapter_count,
            "reconciliation_efficiency": reconciliation_efficiency,
            "cross_iea_duplicate_events": cross_iea_duplicate_events,
            "progress_reconciliation": progress_reconciliation,
            "throttle_correctness": throttle_correctness,
            "progress_quality": progress_quality,
            "iea_root_cause": iea_root_cause,
            "iea_expectation_vs_observed": iea_expectation_vs_observed,
            "time_spent_in_states": time_spent_in_states,
            "correlations": correlations,
            "observability_integrity": observability_integrity,
            "cpu_root_cause_link": cpu_root_cause_link,
        },
        "findings": findings,
    }
