"""
[ENGINE LOAD ANALYSIS] — additive metrics for daily_audit.py (5s buckets, storms, loops).
Does not alter domain / trade_readiness / chain logic.
"""
from __future__ import annotations

import math
from collections import deque
from datetime import datetime, timedelta, timezone
from typing import Any, Callable, Dict, List, Optional, Sequence, Tuple

try:
    from zoneinfo import ZoneInfo
except Exception:  # pragma: no cover
    ZoneInfo = None  # type: ignore

from log_audit import NormEvent

CHICAGO = "America/Chicago"

_SEVERITY_ORDER = ["LOW", "MODERATE", "HIGH", "CRITICAL"]


def _fmt_audit_ts(dt: datetime, tz_arg: str) -> str:
    if tz_arg == "utc":
        return dt.astimezone(timezone.utc).strftime("%Y-%m-%d %H:%M:%S UTC")
    if ZoneInfo is not None:
        z = ZoneInfo(CHICAGO)
        return dt.astimezone(z).strftime("%Y-%m-%d %H:%M:%S %Z")
    return dt.isoformat()


def _bucket_idx(ts: datetime, day_start: datetime, day_end: datetime, bucket_sec: float) -> Optional[int]:
    if ts < day_start or ts >= day_end:
        return None
    sec = (ts - day_start).total_seconds()
    return int(sec // bucket_sec)


def _is_reconciliation_event(ev: str) -> bool:
    if not ev:
        return False
    if ev.startswith("RECONCILIATION"):
        return True
    if "STATE_CONSISTENCY_GATE" in ev and "RECONCILIATION" in ev:
        return True
    return False


def _is_adoption_event(ev: str) -> bool:
    if not ev:
        return False
    if ev.startswith("ADOPTION"):
        return True
    if ev.startswith("IEA_ADOPTION"):
        return True
    return False


def _stall_or_disconnect(ev: str) -> Tuple[bool, bool]:
    stall = ev.startswith("ENGINE_TICK_STALL")
    disc = ev == "CONNECTION_LOST"
    return stall, disc


def _pred_gate(ev: str) -> bool:
    return ev == "STATE_CONSISTENCY_GATE_RECONCILIATION_RESULT"


def _pred_mm(ev: str) -> bool:
    return ev.startswith("RECONCILIATION_MISMATCH")


def _pred_adopt_nc(ev: str) -> bool:
    return ev.startswith("ADOPTION_NON_CONVERGENCE")


def _upgrade_severity(lc: str) -> str:
    if lc not in _SEVERITY_ORDER:
        return "MODERATE"
    i = _SEVERITY_ORDER.index(lc)
    return _SEVERITY_ORDER[min(i + 1, len(_SEVERITY_ORDER) - 1)]


def _base_classification(
    peak_eps: float,
    peak_subwindow_eps: float,
    storms: List[Dict[str, Any]],
    loops: List[Dict[str, Any]],
    peak_low: float,
    peak_mod: float,
    peak_high: float,
    peak_crit: float,
) -> str:
    """Primary tier using coarse 5s peak and subwindow peak (micro-burst aware)."""
    eff = max(peak_eps, peak_subwindow_eps)
    if eff >= peak_crit or len(storms) >= 5:
        return "CRITICAL"
    if eff >= peak_high or len(storms) >= 2 or any(s.get("overlap_engine_tick_stall") for s in storms):
        return "HIGH"
    if eff >= peak_mod or len(storms) >= 1 or loops:
        return "MODERATE"
    if eff >= peak_low:
        return "LOW"
    return "LOW"


def compute_engine_load_analysis(
    events: Sequence[NormEvent],
    day_start: datetime,
    day_end: datetime,
    thresholds: Dict[str, Any],
    tz_arg: str,
) -> Dict[str, Any]:
    bucket_sec = float(thresholds.get("engine_load_bucket_seconds", 5))
    subwindow_sec = float(thresholds.get("engine_load_subwindow_seconds", 1.0))
    storm_eps = float(thresholds.get("engine_load_storm_eps_threshold", 35))
    storm_min_win = int(thresholds.get("engine_load_storm_min_consecutive_windows", 3))
    loop_win = float(thresholds.get("engine_load_loop_window_sec", 60))
    n_gate = int(thresholds.get("engine_load_loop_min_repeats_gate", 20))
    n_mm = int(thresholds.get("engine_load_loop_min_repeats_mismatch", 15))
    n_adopt = int(thresholds.get("engine_load_loop_min_repeats_adoption", 8))
    peak_low = float(thresholds.get("engine_load_peak_low", 8))
    peak_mod = float(thresholds.get("engine_load_peak_moderate", 20))
    peak_high = float(thresholds.get("engine_load_peak_high", 45))
    peak_crit = float(thresholds.get("engine_load_peak_critical", 90))
    recon_ratio_thr = float(thresholds.get("engine_load_reconciliation_ratio_severity_upgrade", 0.5))
    total_storm_esc_s = float(thresholds.get("engine_load_total_storm_time_escalate_seconds", 120))
    recon_storm_dur_crit = float(thresholds.get("engine_load_reconciliation_storm_duration_critical_seconds", 15))
    day_seconds = max(1e-6, (day_end - day_start).total_seconds())
    num_buckets = max(1, int(math.ceil(day_seconds / bucket_sec)))
    n_sub = max(1, int(round(bucket_sec / subwindow_sec)))

    total = [0] * num_buckets
    recon = [0] * num_buckets
    adopt = [0] * num_buckets
    sub_bins: List[List[int]] = [[0] * n_sub for _ in range(num_buckets)]

    stall_ts: List[datetime] = []
    disc_ts: List[datetime] = []

    for e in events:
        ev = e.event or ""
        idx = _bucket_idx(e.ts_utc, day_start, day_end, bucket_sec)
        if idx is None or idx >= num_buckets:
            continue
        total[idx] += 1
        if _is_reconciliation_event(ev):
            recon[idx] += 1
        if _is_adoption_event(ev):
            adopt[idx] += 1
        st, di = _stall_or_disconnect(ev)
        if st:
            stall_ts.append(e.ts_utc)
        if di:
            disc_ts.append(e.ts_utc)

        bucket_t0 = day_start + timedelta(seconds=idx * bucket_sec)
        off = (e.ts_utc - bucket_t0).total_seconds()
        si = int(off // subwindow_sec)
        if 0 <= si < n_sub:
            sub_bins[idx][si] += 1

    eps_list = [c / bucket_sec for c in total]
    total_ev = sum(total)
    peak_eps = max(eps_list) if eps_list else 0.0
    avg_eps_day = total_ev / day_seconds

    per_bucket_peak_sub: List[float] = []
    for idx in range(num_buckets):
        if total[idx] == 0:
            per_bucket_peak_sub.append(0.0)
        else:
            mx = max(sub_bins[idx]) if sub_bins[idx] else 0
            per_bucket_peak_sub.append(mx / subwindow_sec)
    peak_subwindow_eps = max(per_bucket_peak_sub) if per_bucket_peak_sub else 0.0

    dense_windows: List[Dict[str, Any]] = []
    for i in range(num_buckets):
        if total[i] == 0:
            continue
        t0 = day_start + timedelta(seconds=i * bucket_sec)
        t1 = t0 + timedelta(seconds=bucket_sec)
        psw = per_bucket_peak_sub[i] if i < len(per_bucket_peak_sub) else 0.0
        dense_windows.append(
            {
                "window_index": i,
                "start_time_utc": t0.isoformat().replace("+00:00", "Z"),
                "end_time_utc": t1.isoformat().replace("+00:00", "Z"),
                "total_events": total[i],
                "reconciliation_events": recon[i],
                "adoption_events": adopt[i],
                "events_per_second": round(total[i] / bucket_sec, 4),
                "peak_subwindow_eps": round(psw, 4),
            }
        )
    max_dense = int(thresholds.get("engine_load_max_dense_windows_export", 200))
    dense_windows.sort(key=lambda x: (x["peak_subwindow_eps"], x["events_per_second"]), reverse=True)
    top_dense = dense_windows[:max_dense]
    nonzero_bucket_count = len([c for c in total if c > 0])

    recon_total = sum(recon)
    recon_ratio = (recon_total / total_ev) if total_ev else 0.0

    storms: List[Dict[str, Any]] = []
    i = 0
    while i < num_buckets:
        if eps_list[i] <= storm_eps:
            i += 1
            continue
        j = i
        while j < num_buckets and eps_list[j] > storm_eps:
            j += 1
        run_len = j - i
        if run_len >= storm_min_win:
            seg_eps = eps_list[i:j]
            t_start = day_start + timedelta(seconds=i * bucket_sec)
            t_end = day_start + timedelta(seconds=j * bucket_sec)
            dur = (t_end - t_start).total_seconds()
            seg = total[i:j]
            avg_storm_eps = (sum(seg) / dur) if dur > 0 else 0.0

            def _overlap(ts_list: List[datetime], a: datetime, b: datetime) -> bool:
                for t in ts_list:
                    if a <= t < b:
                        return True
                return False

            stall_o = _overlap(stall_ts, t_start, t_end)
            disc_o = _overlap(disc_ts, t_start, t_end)

            gate_n = mm_n = ad_n = 0
            storm_recon_family = storm_adopt_family = 0
            storm_total_events = 0
            for e in events:
                ts = e.ts_utc
                if ts < t_start or ts >= t_end:
                    continue
                storm_total_events += 1
                ev = e.event or ""
                if _pred_gate(ev):
                    gate_n += 1
                if _pred_mm(ev):
                    mm_n += 1
                if _pred_adopt_nc(ev):
                    ad_n += 1
                if _is_reconciliation_event(ev):
                    storm_recon_family += 1
                if _is_adoption_event(ev):
                    storm_adopt_family += 1

            recon_loop_events = gate_n + mm_n
            if recon_loop_events > 0 and ad_n > 0:
                storm_class = "MIXED_STORM"
            elif ad_n > 0 and recon_loop_events == 0:
                storm_class = "ADOPTION_STORM"
            elif recon_loop_events > 0:
                storm_class = "RECONCILIATION_STORM"
            elif storm_recon_family > storm_adopt_family * 2:
                storm_class = "RECONCILIATION_STORM"
            elif storm_adopt_family > storm_recon_family * 2:
                storm_class = "ADOPTION_STORM"
            elif storm_adopt_family > 0 and storm_recon_family > 0:
                storm_class = "MIXED_STORM"
            else:
                storm_class = "GENERIC_BURST"

            storm_type_map = {
                "RECONCILIATION_STORM": "RECONCILIATION",
                "ADOPTION_STORM": "ADOPTION",
                "MIXED_STORM": "MIXED",
                "GENERIC_BURST": "UNKNOWN",
            }
            storm_type = storm_type_map.get(storm_class, "UNKNOWN")
            peak_recon_ratio = (storm_recon_family / storm_total_events) if storm_total_events else 0.0

            storms.append(
                {
                    "start_time_utc": t_start.isoformat().replace("+00:00", "Z"),
                    "end_time_utc": t_end.isoformat().replace("+00:00", "Z"),
                    "start_time_audit_tz": _fmt_audit_ts(t_start, tz_arg),
                    "end_time_audit_tz": _fmt_audit_ts(t_end, tz_arg),
                    "duration_seconds": round(dur, 3),
                    "avg_eps": round(avg_storm_eps, 4),
                    "max_eps": round(max(seg_eps), 4),
                    "consecutive_windows": run_len,
                    "overlap_engine_tick_stall": stall_o,
                    "overlap_connection_lost": disc_o,
                    "storm_type": storm_type,
                    "storm_classification": storm_class,
                    "loop_counts_inside_storm": {
                        "reconciliation_loop_count": recon_loop_events,
                        "adoption_loop_count": ad_n,
                    },
                    "peak_reconciliation_ratio": round(peak_recon_ratio, 6),
                    "loop_events_gate_result": gate_n,
                    "loop_events_reconciliation_mismatch": mm_n,
                    "loop_events_adoption_non_convergence": ad_n,
                    "reconciliation_event_count": storm_recon_family,
                    "adoption_event_count": storm_adopt_family,
                    "events_in_storm": storm_total_events,
                }
            )
        i = j if j > i else i + 1

    total_storm_time_seconds = round(sum(s["duration_seconds"] for s in storms), 3)

    ev_sorted = sorted(events, key=lambda x: x.ts_utc)

    def scan_loop(predicate: Callable[[str], bool]) -> Optional[Dict[str, Any]]:
        q: deque = deque()
        best: Tuple[int, datetime, datetime] = (0, day_start, day_start)
        for e in ev_sorted:
            if not predicate(e.event or ""):
                continue
            q.append(e.ts_utc)
            while q and (e.ts_utc - q[0]).total_seconds() > loop_win:
                q.popleft()
            cnt = len(q)
            if cnt > best[0] and q:
                best = (cnt, q[0], e.ts_utc)
        if best[0] == 0:
            return None
        return {
            "max_repeats_in_window": best[0],
            "window_sec": loop_win,
            "peak_window_start_utc": best[1].isoformat().replace("+00:00", "Z"),
            "peak_window_end_utc": best[2].isoformat().replace("+00:00", "Z"),
            "peak_window_start_audit_tz": _fmt_audit_ts(best[1], tz_arg),
            "peak_window_end_audit_tz": _fmt_audit_ts(best[2], tz_arg),
        }

    loops: List[Dict[str, Any]] = []
    g = scan_loop(_pred_gate)
    if g and g["max_repeats_in_window"] >= n_gate:
        loops.append({"loop_classification": "RECONCILIATION_LOOP", "subtype": "GATE_RESULT", **g})
    m = scan_loop(_pred_mm)
    if m and m["max_repeats_in_window"] >= n_mm:
        loops.append({"loop_classification": "RECONCILIATION_LOOP", "subtype": "MISMATCH_FAMILY", **m})
    a = scan_loop(_pred_adopt_nc)
    if a and a["max_repeats_in_window"] >= n_adopt:
        loops.append({"loop_classification": "ADOPTION_LOOP", "subtype": "ADOPTION_NON_CONVERGENCE", **a})

    lc = _base_classification(
        peak_eps, peak_subwindow_eps, storms, loops, peak_low, peak_mod, peak_high, peak_crit
    )
    classification_notes: List[str] = []

    if recon_ratio > recon_ratio_thr:
        lc = _upgrade_severity(lc)
        classification_notes.append(f"reconciliation_ratio_{recon_ratio:.2f}_gt_{recon_ratio_thr}_upgrade")

    if total_storm_time_seconds > total_storm_esc_s:
        lc = _upgrade_severity(lc)
        classification_notes.append(f"total_storm_time_{total_storm_time_seconds}s_gt_{total_storm_esc_s}_upgrade")

    loop_and_high_eps = bool(loops) and (max(peak_eps, peak_subwindow_eps) >= peak_high)
    if loop_and_high_eps:
        lc = "CRITICAL"
        classification_notes.append("loop_detected_and_high_eps_force_critical")

    if any(
        s.get("storm_type") == "RECONCILIATION" and float(s.get("duration_seconds", 0)) > recon_storm_dur_crit
        for s in storms
    ):
        lc = "CRITICAL"
        classification_notes.append(
            f"reconciliation_storm_duration_gt_{recon_storm_dur_crit}s_force_critical"
        )

    return {
        "bucket_seconds": bucket_sec,
        "subwindow_seconds": subwindow_sec,
        "nonzero_bucket_count": nonzero_bucket_count,
        "dense_windows_note": "Non-empty buckets; top by peak_subwindow_eps then coarse EPS (see engine_load_max_dense_windows_export)",
        "top_dense_buckets": top_dense,
        "peak_events_per_second": round(peak_eps, 4),
        "peak_subwindow_eps": round(peak_subwindow_eps, 4),
        "average_events_per_second": round(avg_eps_day, 4),
        "total_events_in_window": total_ev,
        "reconciliation_events_total": recon_total,
        "reconciliation_event_ratio": round(recon_ratio, 6),
        "storms": storms,
        "total_storm_time_seconds": total_storm_time_seconds,
        "storm_thresholds": {
            "events_per_second_gt": storm_eps,
            "min_consecutive_windows": storm_min_win,
            "total_time_escalate_seconds": total_storm_esc_s,
            "reconciliation_storm_duration_critical_seconds": recon_storm_dur_crit,
        },
        "loop_signatures": loops,
        "loop_thresholds": {
            "window_sec": loop_win,
            "min_repeats_gate": n_gate,
            "min_repeats_mismatch": n_mm,
            "min_repeats_adoption": n_adopt,
        },
        "load_classification": lc,
        "classification_notes": classification_notes,
        "classification_thresholds": {
            "peak_eps_low": peak_low,
            "peak_eps_moderate": peak_mod,
            "peak_eps_high": peak_high,
            "peak_eps_critical": peak_crit,
            "reconciliation_ratio_upgrade": recon_ratio_thr,
            "reconciliation_storm_duration_critical_seconds": recon_storm_dur_crit,
        },
    }
