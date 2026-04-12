"""
Daily watchdog + robot audit (deterministic, file-based).

Reads robot JSONL (excluding frontend_feed) and watchdog incidents; writes JSON, full TSV,
and a compact ``{date}.summary.tsv``. See ``audit_version`` / ``day_boundary_mode`` in JSON.

``--validate`` prints input counts, normalization stats, per-type interval totals and EOD-bound
unclosed counts (full robot log scan; does not write files).
"""
from __future__ import annotations

import argparse
import json
from collections import defaultdict
from concurrent.futures import ThreadPoolExecutor
from dataclasses import dataclass
from datetime import date, datetime, time, timedelta, timezone
from pathlib import Path
from typing import Any, Dict, List, Optional, Sequence, Set, Tuple

from zoneinfo import ZoneInfo

from modules.watchdog.config import (
    INCIDENTS_FILE,
    QTSW2_ROOT,
    ROBOT_LOGS_DIR,
    STATUS_SNAPSHOTS_FILE,
)

CHICAGO = ZoneInfo("America/Chicago")

# --- Event classification (robot / engine canonical names) ---

RECONCILIATION_FAIL_CLOSED_START: Set[str] = {
    "MISMATCH_FAIL_CLOSED",
    "RECONCILIATION_MISMATCH_FAIL_CLOSED",
    "STATE_CONSISTENCY_GATE_RECOVERY_FAILED",
}
RECONCILIATION_FAIL_CLOSED_END: Set[str] = {
    "RECONCILIATION_MISMATCH_CLEARED",
    "STATE_CONSISTENCY_GATE_RELEASED",
}

ENGAGED_START: Set[str] = {
    "STATE_CONSISTENCY_GATE_ENGAGED",
    "RECONCILIATION_MISMATCH_DETECTED",
}

DISCONNECT_FAIL_CLOSED_START: Set[str] = {"DISCONNECT_FAIL_CLOSED_ENTERED"}
DISCONNECT_FAIL_CLOSED_END: Set[str] = {
    "DISCONNECT_RECOVERY_COMPLETE",
    "DISCONNECT_RECOVERY_ABORTED",
}

RECOVERY_START: Set[str] = {"DISCONNECT_RECOVERY_STARTED"}
RECOVERY_END: Set[str] = {
    "DISCONNECT_RECOVERY_COMPLETE",
    "DISCONNECT_RECOVERY_ABORTED",
}

DISCONNECT_LOG_EVENTS: Set[str] = {"CONNECTION_LOST", "CONNECTION_LOST_SUSTAINED"}

ADOPTION_GRACE_EXPIRED: Set[str] = {"ADOPTION_GRACE_EXPIRED_UNOWNED"}
ADOPTION_SUCCESS_EVENTS: Set[str] = {
    "ADOPTION_SUCCESS",
    "RECONCILIATION_RECOVERY_ADOPTION_SUCCESS",
}

RECONCILIATION_DETECTED_ONLY: Set[str] = {"RECONCILIATION_MISMATCH_DETECTED"}
RECONCILIATION_FAIL_CLOSED_EVENT: Set[str] = set(RECONCILIATION_FAIL_CLOSED_START)
RECONCILIATION_CLEARED_EVENTS: Set[str] = set(RECONCILIATION_FAIL_CLOSED_END)

# Implied duration cap when pairing EXECUTION_BLOCKED → next log line (deterministic proxy)
EXECUTION_BLOCKED_IMPLIED_CAP_SEC = 900

AUDIT_VERSION = "2.1"
DAY_BOUNDARY_MODE = "chicago_calendar_day"
AUDIT_TIMEZONE = "America/Chicago"
EXECUTION_BLOCKED_TIME_METHOD = "implied_until_next_log_capped_900s"
EXECUTION_BLOCKED_TIME_SOURCE = "implied_until_next_log"

# Log-derived stall pairing (mirrors incident end types where tight)
ENGINE_STALL_LOG_START: Set[str] = {"ENGINE_TICK_STALL_DETECTED", "ENGINE_TICK_STALL"}
ENGINE_STALL_LOG_END: Set[str] = {"ENGINE_ALIVE", "ENGINE_TICK_STALL_RECOVERED"}

DATA_STALL_LOG_START: Set[str] = {"DATA_STALL_DETECTED", "DATA_LOSS_DETECTED"}
DATA_STALL_LOG_END: Set[str] = {"DATA_STALL_RECOVERED"}

# Validate: warn when many intervals are synthetically closed at day boundary
EOD_FORCED_WARN_MIN_INTERVALS = 5
EOD_FORCED_WARN_RATIO = 0.5


@dataclass(frozen=True)
class NormEvent:
    ts: datetime  # UTC
    ts_iso: str
    event_type: str
    run_id: str
    trading_date_effective: str
    stream: Optional[str]
    instrument: Optional[str]
    raw: Dict[str, Any]


def trading_date_end_utc_bound(trading_date: str) -> datetime:
    """Exclusive upper bound for 'calendar' trading_date in Chicago (end of that Chicago day)."""
    d = date.fromisoformat(trading_date)
    end_local = datetime.combine(d, time(23, 59, 59, 999999, tzinfo=CHICAGO))
    return end_local.astimezone(timezone.utc) + timedelta(microseconds=1)


def _parse_ts(ts_str: str) -> Optional[datetime]:
    if not ts_str or not isinstance(ts_str, str):
        return None
    try:
        ts = datetime.fromisoformat(ts_str.replace("Z", "+00:00"))
        if ts.tzinfo is None:
            ts = ts.replace(tzinfo=timezone.utc)
        return ts.astimezone(timezone.utc)
    except Exception:
        return None


def _deep_get(d: Any, *keys: str) -> Any:
    cur = d
    for k in keys:
        if not isinstance(cur, dict):
            return None
        cur = cur.get(k)
    return cur


def _stream_from_raw(raw: Dict[str, Any]) -> Optional[str]:
    s = raw.get("stream")
    if isinstance(s, str) and s.strip():
        return s.strip()
    data = raw.get("data")
    if isinstance(data, dict):
        for k in ("stream_id", "stream", "streamId"):
            v = data.get(k)
            if isinstance(v, str) and v.strip():
                return v.strip()
    return None


def _instrument_from_raw(raw: Dict[str, Any]) -> Optional[str]:
    ins = raw.get("instrument")
    if isinstance(ins, str) and ins.strip():
        return ins.strip()
    data = raw.get("data")
    if isinstance(data, dict):
        for k in ("instrument", "execution_instrument_full_name", "execution_instrument_key"):
            v = data.get(k)
            if isinstance(v, str) and v.strip():
                return v.strip()
    return None


def _trading_date_from_data(raw: Dict[str, Any]) -> Optional[str]:
    td = raw.get("trading_date")
    if isinstance(td, str) and len(td) >= 10 and td[:4].isdigit():
        return td[:10]
    data = raw.get("data")
    if isinstance(data, dict):
        td2 = data.get("trading_date")
        if isinstance(td2, str) and len(td2) >= 10 and td2[:4].isdigit():
            return td2[:10]
    return None


def effective_trading_date(raw: Dict[str, Any], ts: datetime) -> str:
    td = _trading_date_from_data(raw)
    if td:
        return td
    return ts.astimezone(CHICAGO).date().isoformat()


def normalize_raw_event(raw: Dict[str, Any]) -> Optional[NormEvent]:
    ts_str = (
        raw.get("ts_utc")
        or raw.get("timestamp_utc")
        or raw.get("ts")
        or _deep_get(raw, "data", "ts_utc")
        or ""
    )
    if not isinstance(ts_str, str):
        return None
    ts = _parse_ts(ts_str)
    if ts is None:
        return None
    et = raw.get("event") or raw.get("event_type") or raw.get("@event") or ""
    if not isinstance(et, str):
        et = str(et)
    et = et.strip()
    if not et:
        return None
    run_id = raw.get("run_id") or ""
    if not isinstance(run_id, str):
        run_id = str(run_id)
    tde = effective_trading_date(raw, ts)
    stre = _stream_from_raw(raw)
    ins = _instrument_from_raw(raw)
    # Normalize ts_iso for dedupe: truncate to microseconds for stability
    ts_iso = ts.isoformat()
    return NormEvent(
        ts=ts,
        ts_iso=ts_iso,
        event_type=et,
        run_id=run_id,
        trading_date_effective=tde,
        stream=stre,
        instrument=ins,
        raw=raw,
    )


def normalize_events(raw_events: Sequence[Dict[str, Any]]) -> List[NormEvent]:
    deduped, _meta = normalize_events_with_stats(raw_events)
    return deduped


def normalize_events_with_stats(
    raw_events: Sequence[Dict[str, Any]],
) -> Tuple[List[NormEvent], Dict[str, int]]:
    """Deduplicate (event_type, ts_iso, run_id); return counts for validation / metadata."""
    out: List[NormEvent] = []
    for r in raw_events:
        n = normalize_raw_event(r)
        if n:
            out.append(n)
    after_parse_count = len(out)
    out.sort(key=lambda e: (e.ts, e.ts_iso, e.event_type, e.run_id))
    seen: Set[Tuple[str, str, str]] = set()
    deduped: List[NormEvent] = []
    for e in out:
        k = (e.event_type, e.ts_iso, e.run_id)
        if k in seen:
            continue
        seen.add(k)
        deduped.append(e)
    duplicate_rows_dropped = after_parse_count - len(deduped)
    meta = {
        "raw_lines_normalized": after_parse_count,
        "after_sort_pre_dedupe": after_parse_count,
        "normalized_event_count": len(deduped),
        "duplicate_rows_dropped": duplicate_rows_dropped,
    }
    return deduped, meta


def _count_intervals_ending_at_eod(
    intervals: Sequence[Tuple[datetime, datetime]], eod_exclusive: datetime
) -> int:
    return sum(1 for _a, b in intervals if b == eod_exclusive)


def pair_adoption_grace_intervals(
    events: Sequence[NormEvent], eod_exclusive: datetime
) -> List[Tuple[datetime, datetime]]:
    """Grace expired → success FIFO; unmatched graces close at EOD bound."""
    starts: List[datetime] = []
    intervals: List[Tuple[datetime, datetime]] = []
    for ev in events:
        if ev.event_type in ADOPTION_GRACE_EXPIRED:
            starts.append(ev.ts)
        elif ev.event_type in ADOPTION_SUCCESS_EVENTS and starts:
            st = starts.pop(0)
            en = ev.ts
            if en < st:
                en = st
            intervals.append((st, min(en, eod_exclusive)))
    for st in starts:
        intervals.append((st, eod_exclusive))
    return intervals


def iter_robot_jsonl_paths() -> List[Path]:
    bases = [ROBOT_LOGS_DIR, ROBOT_LOGS_DIR / "archive"]
    paths: Set[Path] = set()
    for b in bases:
        if not b.is_dir():
            continue
        for p in b.glob("robot_*.jsonl"):
            paths.add(p.resolve())
    return sorted(paths)


def _scan_robot_file_for_days(path: Path, dates: Set[str]) -> Dict[str, List[Dict[str, Any]]]:
    """Assign each matching line to one trading_date bucket (typically one pass per file)."""
    buckets: Dict[str, List[Dict[str, Any]]] = {d: [] for d in dates}
    try:
        with open(path, encoding="utf-8", errors="replace") as f:
            for line in f:
                line = line.strip()
                if not line:
                    continue
                try:
                    obj = json.loads(line)
                except json.JSONDecodeError:
                    continue
                if not isinstance(obj, dict):
                    continue
                n = normalize_raw_event(obj)
                if not n:
                    continue
                if n.trading_date_effective in buckets:
                    buckets[n.trading_date_effective].append(obj)
    except OSError:
        pass
    return buckets


def merge_bucket_parts(
    parts: Sequence[Dict[str, List[Dict[str, Any]]]], dates: Set[str]
) -> Dict[str, List[Dict[str, Any]]]:
    out: Dict[str, List[Dict[str, Any]]] = {d: [] for d in dates}
    for part in parts:
        for d in dates:
            out[d].extend(part.get(d, []))
    return out


def load_robot_raw_events_for_days(dates: Set[str]) -> Dict[str, List[Dict[str, Any]]]:
    """Load robot JSONL lines bucketed by effective trading_date (single scan per file)."""
    if not dates:
        return {}
    paths = iter_robot_jsonl_paths()
    if not paths:
        return {d: [] for d in dates}
    max_workers = min(32, len(paths))

    def _work(p: Path) -> Dict[str, List[Dict[str, Any]]]:
        return _scan_robot_file_for_days(p, dates)

    with ThreadPoolExecutor(max_workers=max_workers) as pool:
        parts = list(pool.map(_work, paths))
    return merge_bucket_parts(parts, dates)


def load_all_logs_for_day(trading_date: str) -> List[Dict[str, Any]]:
    """Load and filter raw JSON objects for one trading day (effective date).

    Scans ``logs/robot/robot_*.jsonl`` and ``archive/robot_*.jsonl`` only
    (excludes ``frontend_feed.jsonl`` and other non-robot_ prefixed logs).
    """
    return load_robot_raw_events_for_days({trading_date}).get(trading_date, [])


_INCIDENTS_BY_DAY_CACHE: Optional[Tuple[float, Dict[str, List[Dict[str, Any]]]]] = None


def load_all_incidents_by_chicago_day(
    incidents_path: Optional[Path] = None,
) -> Dict[str, List[Dict[str, Any]]]:
    """
    One full read of incidents.jsonl, bucketed by Chicago calendar date of start_ts.
    Cached by file mtime for repeated audits in the same process.
    """
    global _INCIDENTS_BY_DAY_CACHE
    path = incidents_path or INCIDENTS_FILE
    try:
        mtime = path.stat().st_mtime
    except OSError:
        return {}
    if _INCIDENTS_BY_DAY_CACHE is not None and _INCIDENTS_BY_DAY_CACHE[0] == mtime:
        return _INCIDENTS_BY_DAY_CACHE[1]

    buckets: Dict[str, List[Dict[str, Any]]] = defaultdict(list)
    seen_ids: Dict[str, Set[str]] = defaultdict(set)
    try:
        with open(path, encoding="utf-8", errors="replace") as f:
            for line in f:
                line = line.strip()
                if not line:
                    continue
                try:
                    rec = json.loads(line)
                except json.JSONDecodeError:
                    continue
                if not isinstance(rec, dict):
                    continue
                day = _incident_chicago_day(str(rec.get("start_ts", "")))
                if not day:
                    continue
                iid = str(rec.get("incident_id", ""))
                if iid:
                    if iid in seen_ids[day]:
                        continue
                    seen_ids[day].add(iid)
                buckets[day].append(rec)
    except OSError:
        return {}

    result = dict(buckets)
    _INCIDENTS_BY_DAY_CACHE = (mtime, result)
    return result


def pair_intervals_fifo(
    events: Sequence[NormEvent],
    start_types: Set[str],
    end_types: Set[str],
    eod_exclusive: datetime,
) -> List[Tuple[datetime, datetime]]:
    """Match each start with next end (FIFO). Unmatched starts close at eod_exclusive (exclusive bound)."""
    starts: List[datetime] = []
    intervals: List[Tuple[datetime, datetime]] = []
    for ev in events:
        if ev.event_type in start_types:
            starts.append(ev.ts)
        elif ev.event_type in end_types and starts:
            st = starts.pop(0)
            en = ev.ts
            if en < st:
                en = st
            intervals.append((st, min(en, eod_exclusive)))
    for st in starts:
        intervals.append((st, eod_exclusive))
    return intervals


def _duration_sec(a: datetime, b: datetime) -> float:
    return max(0.0, (b - a).total_seconds())


def compute_intervals(events: Sequence[NormEvent], trading_date: str) -> Dict[str, List[Tuple[datetime, datetime]]]:
    eod_excl = trading_date_end_utc_bound(trading_date)
    return {
        "reconciliation_fail_closed": pair_intervals_fifo(
            events, RECONCILIATION_FAIL_CLOSED_START, RECONCILIATION_FAIL_CLOSED_END, eod_excl
        ),
        "reconciliation_engaged": pair_intervals_fifo(
            events,
            ENGAGED_START,
            RECONCILIATION_FAIL_CLOSED_END | RECONCILIATION_FAIL_CLOSED_START,
            eod_excl,
        ),
        "disconnect_fail_closed": pair_intervals_fifo(
            events, DISCONNECT_FAIL_CLOSED_START, DISCONNECT_FAIL_CLOSED_END, eod_excl
        ),
        "recovery": pair_intervals_fifo(events, RECOVERY_START, RECOVERY_END, eod_excl),
        "adoption_grace": pair_adoption_grace_intervals(events, eod_excl),
        "engine_stall_log": pair_intervals_fifo(
            events, ENGINE_STALL_LOG_START, ENGINE_STALL_LOG_END, eod_excl
        ),
        "data_stall_log": pair_intervals_fifo(
            events, DATA_STALL_LOG_START, DATA_STALL_LOG_END, eod_excl
        ),
    }


def _interval_stats(intervals: List[Tuple[datetime, datetime]]) -> Tuple[int, float, float, float]:
    if not intervals:
        return 0, 0.0, 0.0, 0.0
    durs = [_duration_sec(a, b) for a, b in intervals]
    total = sum(durs)
    mx = max(durs)
    avg = total / len(durs)
    return len(intervals), total, avg, mx


def _adoption_metrics(events: Sequence[NormEvent]) -> Tuple[int, int, int]:
    grace_n = 0
    success_n = 0
    pending_graces: List[datetime] = []
    for ev in events:
        if ev.event_type in ADOPTION_GRACE_EXPIRED:
            grace_n += 1
            pending_graces.append(ev.ts)
        elif ev.event_type in ADOPTION_SUCCESS_EVENTS:
            success_n += 1
            if pending_graces:
                pending_graces.pop(0)
    stuck = len(pending_graces)
    return grace_n, success_n, stuck


def _execution_blocked_implied_stats_by_stream(
    sorted_events: Sequence[NormEvent], eod_excl: datetime
) -> Tuple[Dict[str, float], int, int]:
    """Implied EXECUTION_BLOCKED intervals: duration until next log line, capped; counts capped hits."""
    lst = list(sorted_events)
    by_stream: Dict[str, float] = {}
    intervals_count = 0
    capped_intervals_count = 0
    cap_f = float(EXECUTION_BLOCKED_IMPLIED_CAP_SEC)
    for i, ev in enumerate(lst):
        if ev.event_type != "EXECUTION_BLOCKED":
            continue
        intervals_count += 1
        sk = ev.stream or "__engine__"
        t0 = ev.ts
        t1 = lst[i + 1].ts if i + 1 < len(lst) else eod_excl
        if t1 < t0:
            t1 = t0
        raw_dur = _duration_sec(t0, t1)
        if raw_dur > cap_f:
            capped_intervals_count += 1
        dur = min(raw_dur, cap_f)
        by_stream[sk] = by_stream.get(sk, 0.0) + dur
    return by_stream, intervals_count, capped_intervals_count


def _incident_chicago_day(iso_start: str) -> Optional[str]:
    ts = _parse_ts(iso_start)
    if ts is None:
        return None
    return ts.astimezone(CHICAGO).date().isoformat()


def load_incidents_for_day(trading_date: str) -> List[Dict[str, Any]]:
    return load_all_incidents_by_chicago_day().get(trading_date, [])


def load_status_snapshots_for_day(trading_date: str) -> Dict[str, Any]:
    """Optional aggregates from status_snapshots.jsonl (same Chicago trading day)."""
    if not STATUS_SNAPSHOTS_FILE.is_file():
        return {}
    n = 0
    engine_dead = 0
    fill_health_false = 0
    try:
        with open(STATUS_SNAPSHOTS_FILE, encoding="utf-8", errors="replace") as f:
            for line in f:
                line = line.strip()
                if not line:
                    continue
                try:
                    rec = json.loads(line)
                except json.JSONDecodeError:
                    continue
                if not isinstance(rec, dict):
                    continue
                ts_s = rec.get("timestamp_utc") or rec.get("ts_utc") or ""
                day = _incident_chicago_day(str(ts_s))
                if day != trading_date:
                    continue
                n += 1
                if rec.get("engine_alive") is False:
                    engine_dead += 1
                if rec.get("fill_health_ok") is False:
                    fill_health_false += 1
    except OSError:
        return {}
    return {
        "snapshot_row_count": n,
        "snapshots_engine_alive_false_count": engine_dead,
        "snapshots_fill_health_not_ok_count": fill_health_false,
    }


def _incident_coverage_complete_flag(
    incidents_file_exists: bool,
    incidents_day: Sequence[Dict[str, Any]],
    events: Sequence[NormEvent],
) -> Optional[bool]:
    """True / False / None (unknown): None if file missing or sparse with no signal."""
    if not incidents_file_exists:
        return None
    log_suggests_stall = any(
        e.event_type in (ENGINE_STALL_LOG_START | DATA_STALL_LOG_START) for e in events
    )
    if log_suggests_stall and len(incidents_day) == 0:
        return False
    if len(incidents_day) == 0:
        return None
    return True


def collect_eod_boundary_warnings(
    intervals: Dict[str, List[Tuple[datetime, datetime]]],
    eod_excl: datetime,
) -> List[str]:
    """Emit when a large share of intervals were synthetically closed at the Chicago day bound."""
    warns: List[str] = []
    unclosed = {k: _count_intervals_ending_at_eod(v, eod_excl) for k, v in intervals.items()}

    total_fc = len(intervals["reconciliation_fail_closed"]) + len(intervals["disconnect_fail_closed"])
    fc_open = unclosed["reconciliation_fail_closed"] + unclosed["disconnect_fail_closed"]
    if total_fc and (
        fc_open >= EOD_FORCED_WARN_MIN_INTERVALS
        or (fc_open / total_fc) >= EOD_FORCED_WARN_RATIO
    ):
        warns.append(
            f"Fail-closed: {fc_open} of {total_fc} interval(s) were closed at day boundary (no explicit clear in window); "
            "durations may be inflated."
        )

    tot_rec = len(intervals["recovery"])
    ro = unclosed["recovery"]
    if tot_rec and (
        ro >= EOD_FORCED_WARN_MIN_INTERVALS or (ro / tot_rec) >= EOD_FORCED_WARN_RATIO
    ):
        warns.append(
            f"Recovery: {ro} of {tot_rec} interval(s) closed at day boundary; missing DISCONNECT_RECOVERY_COMPLETE/ABORTED lines may explain this."
        )

    tot_ad = len(intervals["adoption_grace"])
    ao = unclosed["adoption_grace"]
    if tot_ad and (
        ao >= EOD_FORCED_WARN_MIN_INTERVALS or (ao / tot_ad) >= EOD_FORCED_WARN_RATIO
    ):
        warns.append(
            f"Adoption grace: {ao} of {tot_ad} interval(s) closed at day boundary without ADOPTION_SUCCESS pair."
        )
    return warns


def _compute_data_integrity_score(
    *,
    unclosed_interval_counts: Dict[str, int],
    interval_totals: Dict[str, int],
    coverage: Optional[bool],
    norm_stats: Dict[str, int],
    execution_blocked_intervals_count: int,
    execution_blocked_capped_intervals_count: int,
) -> Tuple[float, List[str]]:
    """
    Heuristic score in [0, 1]: 1.0 = high trust in same-day metrics; lower = more caveats.
    """
    score = 1.0
    flags: List[str] = []
    penalty = 0.0

    for key, uncl in unclosed_interval_counts.items():
        total = interval_totals.get(key, 0)
        if total > 0 and (uncl / total) > 0.5:
            penalty += 0.12
            flags.append(f"high_eod_unclosed_ratio:{key}={uncl}/{total}")

    if coverage is False:
        penalty += 0.2
        flags.append("incident_coverage_incomplete")

    raw_n = int(norm_stats.get("raw_lines_normalized", 0) or 0)
    dropped = int(norm_stats.get("duplicate_rows_dropped", 0) or 0)
    if raw_n > 0:
        dr = dropped / raw_n
        if dr > 0.05:
            penalty += min(0.2, dr)
            flags.append(f"high_duplicate_row_rate:{dr:.4f}")

    if execution_blocked_intervals_count > 0:
        cap_ratio = execution_blocked_capped_intervals_count / execution_blocked_intervals_count
        if cap_ratio > 0.5:
            penalty += 0.12
            flags.append(
                f"execution_blocked_mostly_capped:{execution_blocked_capped_intervals_count}/"
                f"{execution_blocked_intervals_count}"
            )

    out = max(0.0, score - penalty)
    return round(out, 4), flags


def compute_metrics(
    trading_date: str,
    events: Sequence[NormEvent],
    incidents: Sequence[Dict[str, Any]],
    *,
    normalization_stats: Optional[Dict[str, int]] = None,
    robot_jsonl_file_count: int = 0,
    raw_line_count: int = 0,
) -> Dict[str, Any]:
    eod_excl = trading_date_end_utc_bound(trading_date)
    intervals = compute_intervals(events, trading_date)

    rf_intervals = intervals["reconciliation_fail_closed"]
    df_intervals = intervals["disconnect_fail_closed"]
    eng_intervals = intervals["reconciliation_engaged"]
    rec_intervals = intervals["recovery"]

    fc_r_count, fc_r_total, fc_r_avg, fc_r_max = _interval_stats(rf_intervals)
    fc_d_count, fc_d_total, fc_d_avg, fc_d_max = _interval_stats(df_intervals)
    eng_count, eng_total, eng_avg, eng_max = _interval_stats(eng_intervals)
    rec_count, rec_total, rec_avg, rec_max = _interval_stats(rec_intervals)

    fail_closed_all = rf_intervals + df_intervals
    fc_all_count, fc_all_total, fc_all_avg, fc_all_max = _interval_stats(fail_closed_all)

    grace_n, adopt_succ, adopt_stuck = _adoption_metrics(events)

    recon_detected = sum(1 for e in events if e.event_type in RECONCILIATION_DETECTED_ONLY)
    recon_fc_events = sum(1 for e in events if e.event_type in RECONCILIATION_FAIL_CLOSED_EVENT)
    recon_cleared = sum(1 for e in events if e.event_type in RECONCILIATION_CLEARED_EVENTS)

    disconnect_count = sum(1 for e in events if e.event_type in DISCONNECT_LOG_EVENTS)

    recovery_started_count = sum(1 for e in events if e.event_type in RECOVERY_START)
    recovery_completed_count = sum(1 for e in events if e.event_type == "DISCONNECT_RECOVERY_COMPLETE")

    execution_unsafe_count = sum(1 for e in events if e.event_type == "EXECUTION_BLOCKED")

    eng_st_inc = [inc for inc in incidents if inc.get("type") == "ENGINE_STALLED"]
    data_st_inc = [inc for inc in incidents if inc.get("type") == "DATA_STALL"]

    def _inc_durs(incs: List[Dict[str, Any]]) -> List[float]:
        durs: List[float] = []
        for inc in incs:
            try:
                d = int(inc.get("duration_sec", 0))
            except (TypeError, ValueError):
                d = 0
            durs.append(max(0, d))
        return durs

    eng_durs = _inc_durs(eng_st_inc)
    data_durs = _inc_durs(data_st_inc)

    stall_events_engine = sum(
        1 for e in events if e.event_type in ("ENGINE_TICK_STALL_DETECTED", "ENGINE_TICK_STALL")
    )
    stall_events_data = sum(
        1 for e in events if e.event_type in ("DATA_STALL_DETECTED", "DATA_LOSS_DETECTED")
    )

    # Prefer incident-derived counts; add log-derived detection counts if incidents empty
    engine_stall_count = max(len(eng_st_inc), stall_events_engine)
    data_stall_count = max(len(data_st_inc), stall_events_data)

    snapshot_meta = load_status_snapshots_for_day(trading_date)

    streams: Dict[str, Dict[str, Any]] = {}
    blocked_map, eb_intervals_n, eb_capped_n = _execution_blocked_implied_stats_by_stream(events, eod_excl)

    for e in events:
        sk = e.stream
        if not sk:
            continue
        if sk not in streams:
            streams[sk] = {
                "trades_taken": 0,
                "fail_closed_events": 0,
                "engaged_events": 0,
                "execution_blocked_time": 0.0,
            }
        bucket = streams[sk]
        if e.event_type == "EXECUTION_FILLED":
            bucket["trades_taken"] += 1
        if e.event_type in (
            RECONCILIATION_FAIL_CLOSED_START
            | DISCONNECT_FAIL_CLOSED_START
        ):
            bucket["fail_closed_events"] += 1
        if e.event_type in ENGAGED_START:
            bucket["engaged_events"] += 1

    for sk, dur in blocked_map.items():
        if sk not in streams:
            streams[sk] = {
                "trades_taken": 0,
                "fail_closed_events": 0,
                "engaged_events": 0,
                "execution_blocked_time": 0.0,
            }
        streams[sk]["execution_blocked_time"] = round(dur, 3)

    streams = {
        k: v
        for k, v in streams.items()
        if v["trades_taken"]
        or v["fail_closed_events"]
        or v["engaged_events"]
        or (v.get("execution_blocked_time") or 0) > 0
    }

    critical_incidents: List[Dict[str, Any]] = []
    for inc in incidents:
        sev = str(inc.get("severity", "")).upper()
        if sev != "CRITICAL":
            continue
        start_s = inc.get("start_ts")
        end_s = inc.get("end_ts")
        try:
            dsec = int(inc.get("duration_sec", 0))
        except (TypeError, ValueError):
            dsec = 0
        inst_list = inc.get("instruments") or []
        instrument = inst_list[0] if isinstance(inst_list, list) and inst_list else None
        critical_incidents.append(
            {
                "type": inc.get("type"),
                "start_time": start_s,
                "end_time": end_s,
                "duration": max(0, dsec),
                "instrument": instrument,
            }
        )

    unclosed_interval_counts = {
        k: _count_intervals_ending_at_eod(v, eod_excl) for k, v in intervals.items()
    }
    incidents_file_exists = INCIDENTS_FILE.is_file()
    coverage = _incident_coverage_complete_flag(incidents_file_exists, incidents, events)

    norm_stats = normalization_stats or {}
    interval_totals_for_integrity = {k: len(v) for k, v in intervals.items()}
    data_integrity_score, data_integrity_flags = _compute_data_integrity_score(
        unclosed_interval_counts=dict(unclosed_interval_counts),
        interval_totals=interval_totals_for_integrity,
        coverage=coverage,
        norm_stats=dict(norm_stats),
        execution_blocked_intervals_count=eb_intervals_n,
        execution_blocked_capped_intervals_count=eb_capped_n,
    )

    metrics_quality: Dict[str, Any] = {
        "execution_blocked_time_is_estimated": True,
        "execution_blocked_time_source": EXECUTION_BLOCKED_TIME_SOURCE,
        "execution_blocked_intervals_count": eb_intervals_n,
        "execution_blocked_capped_intervals_count": eb_capped_n,
        "incident_coverage_complete": coverage,
        "unclosed_interval_counts": dict(unclosed_interval_counts),
        "fail_closed_open_intervals": unclosed_interval_counts["reconciliation_fail_closed"]
        + unclosed_interval_counts["disconnect_fail_closed"],
        "recovery_open_intervals": unclosed_interval_counts["recovery"],
        "adoption_open_intervals": unclosed_interval_counts["adoption_grace"],
        "engine_stall_open_intervals": unclosed_interval_counts["engine_stall_log"],
        "data_stall_open_intervals": unclosed_interval_counts["data_stall_log"],
        "normalization": dict(norm_stats),
    }

    # Flat required metrics (Part 3) + breakdown structs for audit/debug
    return {
        "audit_version": AUDIT_VERSION,
        "day_boundary_mode": DAY_BOUNDARY_MODE,
        "timezone": AUDIT_TIMEZONE,
        "generated_at_utc": datetime.now(timezone.utc).isoformat(),
        "execution_blocked_time_method": EXECUTION_BLOCKED_TIME_METHOD,
        "execution_blocked_time_state_based_seconds": None,
        "data_integrity_score": data_integrity_score,
        "data_integrity_flags": data_integrity_flags,
        "metrics_quality": metrics_quality,
        "trading_date": trading_date,
        "fail_closed_count": fc_all_count,
        "fail_closed_total_duration_seconds": round(fc_all_total, 3),
        "fail_closed_avg_duration_seconds": round(fc_all_avg, 3) if fc_all_count else 0.0,
        "max_fail_closed_duration_seconds": round(fc_all_max, 3) if fc_all_count else 0.0,
        "engaged_count": eng_count,
        "execution_unsafe_count": execution_unsafe_count,
        "disconnect_count": disconnect_count,
        "recovery_started_count": recovery_started_count,
        "recovery_completed_count": recovery_completed_count,
        "avg_recovery_duration_seconds": round(rec_avg, 3) if rec_count else 0.0,
        "max_recovery_duration_seconds": round(rec_max, 3) if rec_count else 0.0,
        "adoption_grace_expired_count": grace_n,
        "adoption_success_count": adopt_succ,
        "adoption_stuck_count": adopt_stuck,
        "reconciliation_mismatch_detected_count": recon_detected,
        "reconciliation_fail_closed_count": recon_fc_events,
        "reconciliation_cleared_count": recon_cleared,
        "engine_stall_count": engine_stall_count,
        "data_stall_count": data_stall_count,
        "total_engine_stall_duration_seconds": round(sum(eng_durs), 3),
        "total_data_stall_duration_seconds": round(sum(data_durs), 3),
        "streams": streams,
        "critical_incidents": critical_incidents,
        "breakdown": {
            "raw_lines_for_day": raw_line_count,
            "robot_jsonl_globs_scanned": robot_jsonl_file_count or len(iter_robot_jsonl_paths()),
            "reconciliation_fail_closed_intervals": fc_r_count,
            "reconciliation_fail_closed_total_duration_seconds": round(fc_r_total, 3),
            "disconnect_fail_closed_intervals": fc_d_count,
            "disconnect_fail_closed_total_duration_seconds": round(fc_d_total, 3),
            "engaged_total_duration_seconds": round(eng_total, 3),
            "recovery_interval_count": rec_count,
            "total_recovery_duration_seconds": round(rec_total, 3),
            "adoption_grace_interval_count": len(intervals["adoption_grace"]),
            "engine_stall_log_interval_count": len(intervals["engine_stall_log"]),
            "data_stall_log_interval_count": len(intervals["data_stall_log"]),
            "interval_counts_by_type": {k: len(v) for k, v in intervals.items()},
            "engine_stall_incident_count": len(eng_st_inc),
            "data_stall_incident_count": len(data_st_inc),
            "incidents_matched_day": len(incidents),
            "incidents_file_present": incidents_file_exists,
        },
        "status_snapshots": snapshot_meta,
    }


def write_outputs(
    trading_date: str, payload: Dict[str, Any], out_dir: Optional[Path] = None
) -> Tuple[Path, Path, Path]:
    base = out_dir or (QTSW2_ROOT / "data" / "watchdog" / "daily_audit")
    base.mkdir(parents=True, exist_ok=True)
    json_path = base / f"{trading_date}.json"
    tsv_path = base / f"{trading_date}.tsv"
    summary_path = base / f"{trading_date}.summary.tsv"

    json_path.write_text(json.dumps(payload, indent=2, sort_keys=True), encoding="utf-8")

    header = [
        "date",
        "fail_closed_count",
        "fail_closed_total_duration_seconds",
        "disconnect_count",
        "recovery_started_count",
        "recovery_completed_count",
        "avg_recovery_duration_seconds",
        "engaged_count",
        "execution_unsafe_count",
        "adoption_grace_expired_count",
        "adoption_success_count",
        "adoption_stuck_count",
        "reconciliation_mismatch_detected_count",
        "reconciliation_fail_closed_count",
        "reconciliation_cleared_count",
        "engine_stall_count",
        "data_stall_count",
        "total_engine_stall_duration_seconds",
        "total_data_stall_duration_seconds",
        "critical_incident_count",
    ]
    row = [
        trading_date,
        str(payload.get("fail_closed_count", 0)),
        str(payload.get("fail_closed_total_duration_seconds", 0)),
        str(payload.get("disconnect_count", 0)),
        str(payload.get("recovery_started_count", 0)),
        str(payload.get("recovery_completed_count", 0)),
        str(payload.get("avg_recovery_duration_seconds", 0)),
        str(payload.get("engaged_count", 0)),
        str(payload.get("execution_unsafe_count", 0)),
        str(payload.get("adoption_grace_expired_count", 0)),
        str(payload.get("adoption_success_count", 0)),
        str(payload.get("adoption_stuck_count", 0)),
        str(payload.get("reconciliation_mismatch_detected_count", 0)),
        str(payload.get("reconciliation_fail_closed_count", 0)),
        str(payload.get("reconciliation_cleared_count", 0)),
        str(payload.get("engine_stall_count", 0)),
        str(payload.get("data_stall_count", 0)),
        str(payload.get("total_engine_stall_duration_seconds", 0)),
        str(payload.get("total_data_stall_duration_seconds", 0)),
        str(len(payload.get("critical_incidents") or [])),
    ]
    tsv_path.write_text("\t".join(header) + "\n" + "\t".join(row) + "\n", encoding="utf-8")

    sum_header = [
        "date",
        "fail_closed_count",
        "fail_closed_total_duration_seconds",
        "disconnect_count",
        "recovery_completed_count",
        "adoption_grace_expired_count",
        "engine_stall_count",
        "data_stall_count",
    ]
    sum_row = [
        trading_date,
        str(payload.get("fail_closed_count", 0)),
        str(payload.get("fail_closed_total_duration_seconds", 0)),
        str(payload.get("disconnect_count", 0)),
        str(payload.get("recovery_completed_count", 0)),
        str(payload.get("adoption_grace_expired_count", 0)),
        str(payload.get("engine_stall_count", 0)),
        str(payload.get("data_stall_count", 0)),
    ]
    summary_path.write_text("\t".join(sum_header) + "\n" + "\t".join(sum_row) + "\n", encoding="utf-8")
    return json_path, tsv_path, summary_path


def run_validate(trading_date: str) -> None:
    """Print ingestion / interval diagnostics (no files written)."""
    paths = iter_robot_jsonl_paths()
    raw = load_all_logs_for_day(trading_date)
    events, nstats = normalize_events_with_stats(raw)
    eod_excl = trading_date_end_utc_bound(trading_date)
    intervals = compute_intervals(events, trading_date)

    print("=== daily_audit_builder --validate ===")
    print(f"trading_date: {trading_date}")
    print(f"day_boundary_mode: {DAY_BOUNDARY_MODE} ({AUDIT_TIMEZONE})")
    print(f"robot_jsonl_files: {len(paths)}")
    print(f"raw_lines_for_day (bucket): {len(raw)}")
    print(f"normalized_event_count: {nstats.get('normalized_event_count')}")
    print(f"duplicate_rows_dropped: {nstats.get('duplicate_rows_dropped')}")
    print("interval_counts_by_type (total / unclosed_at_eod_bound):")
    for name in sorted(intervals.keys()):
        iv = intervals[name]
        uc = _count_intervals_ending_at_eod(iv, eod_excl)
        print(f"  {name}: {len(iv)} / {uc}")
    warns = collect_eod_boundary_warnings(intervals, eod_excl)
    if warns:
        print("warnings:")
        for w in warns:
            print(f"  WARNING: {w}")
    else:
        print("warnings: (none)")


def build_daily_audit(
    trading_date: str, out_dir: Optional[Path] = None
) -> Tuple[Dict[str, Any], Path, Path, Path]:
    raw = load_all_logs_for_day(trading_date)
    events, norm_stats = normalize_events_with_stats(raw)
    incidents = load_incidents_for_day(trading_date)
    path_count = len(iter_robot_jsonl_paths())
    payload = compute_metrics(
        trading_date,
        events,
        incidents,
        normalization_stats=norm_stats,
        robot_jsonl_file_count=path_count,
        raw_line_count=len(raw),
    )
    jp, tp, sp = write_outputs(trading_date, payload, out_dir=out_dir)
    return payload, jp, tp, sp


def build_daily_audits_for_dates(
    trading_dates: Sequence[str],
    out_dir: Optional[Path] = None,
) -> List[Tuple[Dict[str, Any], Path, Path, Path]]:
    """Single robot-log scan for all dates; shared incidents bucket (mtime-cached)."""
    ds: Set[str] = {d.strip() for d in trading_dates if d.strip()}
    by_day = load_robot_raw_events_for_days(ds)
    incidents_all = load_all_incidents_by_chicago_day()
    path_count = len(iter_robot_jsonl_paths())
    out: List[Tuple[Dict[str, Any], Path, Path, Path]] = []
    for d in trading_dates:
        d = d.strip()
        if not d:
            continue
        raw = by_day.get(d, [])
        events, norm_stats = normalize_events_with_stats(raw)
        incidents = incidents_all.get(d, [])
        payload = compute_metrics(
            d,
            events,
            incidents,
            normalization_stats=norm_stats,
            robot_jsonl_file_count=path_count,
            raw_line_count=len(raw),
        )
        jp, tp, sp = write_outputs(d, payload, out_dir=out_dir)
        out.append((payload, jp, tp, sp))
    return out


def main() -> None:
    ap = argparse.ArgumentParser(description="Build daily watchdog audit from robot JSONL + incidents.")
    ap.add_argument("--date", default=None, help="Trading date YYYY-MM-DD (Chicago session label)")
    ap.add_argument(
        "--dates",
        default=None,
        help="Comma-separated trading dates; one process / shared incidents cache (faster than multiple runs)",
    )
    ap.add_argument(
        "--validate",
        action="store_true",
        help="Print file/event/interval diagnostics for a single --date (does not write reports)",
    )
    ap.add_argument(
        "--out-dir",
        type=Path,
        default=None,
        help="Override output directory (default data/watchdog/daily_audit)",
    )
    args = ap.parse_args()
    dates: List[str] = []
    if args.dates:
        dates = [d.strip() for d in args.dates.split(",") if d.strip()]
    elif args.date:
        dates = [args.date.strip()]
    if not dates:
        ap.error("Provide --date YYYY-MM-DD or --dates YYYY-MM-DD,YYYY-MM-DD,...")
    if args.validate:
        if len(dates) != 1:
            ap.error("--validate requires exactly one --date (not --dates)")
        run_validate(dates[0])
        return
    if len(dates) > 1:
        built = build_daily_audits_for_dates(dates, out_dir=args.out_dir)
        for _, json_path, tsv_path, summary_path in built:
            print(json_path.as_posix())
            print(tsv_path.as_posix())
            print(summary_path.as_posix())
    else:
        _, json_path, tsv_path, summary_path = build_daily_audit(dates[0], out_dir=args.out_dir)
        print(json_path.as_posix())
        print(tsv_path.as_posix())
        print(summary_path.as_posix())


if __name__ == "__main__":
    main()
