"""
Timetable Engine - "What trades to take today"

This module generates a timetable showing which trades should be taken today
based on RS/time selection rules and filters.

Author: Quantitative Trading System
Date: 2025
"""

import re
import pandas as pd
import numpy as np
from pathlib import Path
from typing import List, Optional, Dict, Tuple, Any, NamedTuple
from datetime import datetime, date, time as dt_time, timezone
import logging
import sys
import json
import pytz

# Import centralized config
# Handle both direct import and relative import
try:
    from modules.matrix.config import (
        DOM_BLOCKED_DAYS,
        S1_EARLY_OPEN_SLOT_TIME,
        S1_INSTRUMENTS_ALLOWED_EARLY_OPEN_SLOT,
        SCF_THRESHOLD,
        SLOT_ENDS,
    )
except ImportError:
    # Fallback: add parent directory to path
    sys.path.insert(0, str(Path(__file__).parent.parent.parent))
    from modules.matrix.config import (  # type: ignore
        DOM_BLOCKED_DAYS,
        S1_EARLY_OPEN_SLOT_TIME,
        S1_INSTRUMENTS_ALLOWED_EARLY_OPEN_SLOT,
        SCF_THRESHOLD,
        SLOT_ENDS,
    )

# CONTRACT: Only import validation helpers, never normalization functions
# Timetable Engine validates, enforces, and fails — it never normalizes dates.
try:
    from modules.matrix.data_loader import (
        _validate_trade_date_dtype,
        _validate_trade_date_presence
    )
    from modules.timetable.cme_session import get_cme_trading_date
except ImportError:
    # Fallback: add parent directory to path
    sys.path.insert(0, str(Path(__file__).parent.parent.parent))
    from modules.matrix.data_loader import (
        _validate_trade_date_dtype,
        _validate_trade_date_presence
    )
    from modules.timetable.cme_session import get_cme_trading_date

# Configure logging
logging.basicConfig(level=logging.INFO, format='%(asctime)s - %(levelname)s - %(message)s')
logger = logging.getLogger(__name__)


def eligibility_trade_date_ymd_from_matrix_df(master_matrix_df: pd.DataFrame) -> Optional[str]:
    """
    Latest ``trade_date`` in the master matrix (YYYY-MM-DD): written as ``eligibility_trade_date``
    on execution documents for operator alignment (not used for stream enablement in execution mode).
    """
    if master_matrix_df is None or master_matrix_df.empty or "trade_date" not in master_matrix_df.columns:
        return None
    td_series = master_matrix_df["trade_date"].dropna()
    if td_series.empty:
        return None
    md_ts = td_series.max()
    md = md_ts.date() if hasattr(md_ts, "date") else pd.to_datetime(md_ts).date()
    return md.isoformat()


_EXECUTION_AUDIT_DOW_NAMES = (
    "Monday",
    "Tuesday",
    "Wednesday",
    "Thursday",
    "Friday",
    "Saturday",
    "Sunday",
)


def _log_execution_timetable_audit_snapshot(
    *,
    master_matrix_df: pd.DataFrame,
    session_trading_date: str,
    eligibility_trade_date: Optional[str],
    streams: List[Dict],
) -> None:
    """
    Single publish-time audit line: session vs filter date vs matrix eligibility row date,
    plus enabled/disabled summary (weekend publish diagnostics).
    """
    utc_now = datetime.now(timezone.utc)
    chicago_tz = pytz.timezone("America/Chicago")
    chicago_now = datetime.now(chicago_tz)

    session_date_obj = pd.to_datetime(str(session_trading_date).strip()).date()
    sw = session_date_obj.weekday()
    session_dow = _EXECUTION_AUDIT_DOW_NAMES[sw]
    session_dom = session_date_obj.day

    filter_date_obj = session_date_obj
    fw = filter_date_obj.weekday()
    filter_dow_name = _EXECUTION_AUDIT_DOW_NAMES[fw]
    filter_dom = filter_date_obj.day

    allowed_df_trade_date: Optional[str] = None
    if master_matrix_df is not None and not master_matrix_df.empty:
        if "trade_date" in master_matrix_df.columns:
            td_series = master_matrix_df["trade_date"].dropna()
            if not td_series.empty:
                md_ts = td_series.max()
                md = md_ts.date() if hasattr(md_ts, "date") else pd.to_datetime(md_ts).date()
                allowed_df_trade_date = md.isoformat()

    enabled_streams = sorted(s["stream"] for s in streams if s.get("enabled"))
    disabled_streams = sorted(
        (
            {
                "stream": s["stream"],
                "reason": s.get("block_reason") or "disabled",
            }
            for s in streams
            if not s.get("enabled")
        ),
        key=lambda x: x["stream"],
    )

    payload = {
        "TIMING": {
            "utc_now": utc_now.isoformat().replace("+00:00", "Z"),
            "chicago_now": chicago_now.isoformat(),
        },
        "SESSION": {
            "session_trading_date": str(session_trading_date).strip(),
            "session_dow": session_dow,
            "session_dom": session_dom,
        },
        "ELIGIBILITY": {
            "eligibility_trade_date": eligibility_trade_date,
            "allowed_df_trade_date": allowed_df_trade_date,
        },
        "FILTER_INPUT": {
            "filter_date_obj": filter_date_obj.isoformat(),
            "filter_dow": filter_dow_name,
            "filter_dom": filter_dom,
        },
        "RESULT": {
            "enabled_streams": enabled_streams,
            "disabled_streams": disabled_streams,
        },
    }
    logger.info(
        "EXECUTION_TIMETABLE_AUDIT_SNAPSHOT %s",
        json.dumps(payload, ensure_ascii=False),
    )


class TimetablePublishResult(NamedTuple):
    """Result of a live execution timetable publish (versioned path)."""

    changed: bool
    skipped_no_change: bool
    timetable_hash: str
    previous_hash: Optional[str]


def _execution_mode_matrix_cell_is_nonempty(raw: Any) -> bool:
    if raw is None:
        return False
    if isinstance(raw, str) and not raw.strip():
        return False
    try:
        if pd.isna(raw):
            return False
    except TypeError:
        pass
    if isinstance(raw, float) and np.isnan(raw):
        return False
    return True


def _coerce_matrix_cell_to_slot_hhmm(raw: Any) -> Optional[str]:
    """
    Parse a matrix Time or Time Change cell into HH:MM (normalize_time).
    Handles datetime/Timestamp, 'HH:MM -> HH:MM' (RHS), and embedded timestamps.
    """
    from modules.matrix.utils import normalize_time

    if not _execution_mode_matrix_cell_is_nonempty(raw):
        return None
    if isinstance(raw, dt_time):
        return normalize_time(f"{raw.hour:d}:{raw.minute:02d}")
    if isinstance(raw, datetime) and not isinstance(raw, pd.Timestamp):
        return normalize_time(f"{raw.hour:d}:{raw.minute:02d}")
    if isinstance(raw, pd.Timestamp):
        return normalize_time(f"{raw.hour:d}:{raw.minute:02d}")
    s = str(raw).strip()
    if s.lower() == "nan":
        return None
    if "->" in s:
        s = s.split("->")[-1].strip()
    ts = pd.to_datetime(s, errors="coerce")
    if isinstance(ts, pd.Timestamp) and pd.notna(ts):
        return normalize_time(f"{ts.hour:d}:{ts.minute:02d}")
    m = re.search(r"(\d{1,2})\s*:\s*(\d{2})", s)
    if m:
        return normalize_time(f"{m.group(1)}:{m.group(2)}")
    return None


def _execution_mode_row_get_time_change(series: pd.Series) -> Any:
    for col in ("Time Change", "Time_Change"):
        if col in series.index:
            return series[col]
    return None


def _execution_mode_row_get_time(series: pd.Series) -> Any:
    if "Time" in series.index:
        return series["Time"]
    return None


def _merge_stream_filters_for_execution(
    project_root: Path,
    stream_filters: Optional[Dict],
) -> Dict:
    """
    Merge configs/stream_filters.json with caller-provided filters.
    Caller keys overwrite file keys for the same stream_id (shallow dict update per stream).

    When ``stream_filters`` is None, the returned dict is exactly what is on disk (or empty if
    the file is missing). Execution mode always calls this so disk config is applied unless the
    caller passes explicit overrides.
    """
    merged: Dict[str, Any] = {}
    cfg = (Path(project_root) / "configs" / "stream_filters.json").resolve()
    cfg_exists = cfg.is_file()
    logger.info(
        "EXECUTION_STREAM_FILTERS_CONFIG: path=%s exists=%s caller_provided=%s",
        cfg,
        cfg_exists,
        stream_filters is not None,
    )
    if cfg_exists:
        try:
            data = json.loads(cfg.read_text(encoding="utf-8"))
            if isinstance(data, dict):
                merged = {
                    str(k): dict(v) if isinstance(v, dict) else v for k, v in data.items()
                }
        except Exception as e:
            logger.warning("EXECUTION_STREAM_FILTERS_CONFIG: failed to load %s: %s", cfg, e)
    elif not stream_filters:
        logger.warning(
            "EXECUTION_STREAM_FILTERS_CONFIG: %s missing and no caller stream_filters; "
            "execution session filters use empty dict",
            cfg,
        )
    if stream_filters:
        for sid, filt in stream_filters.items():
            key = str(sid)
            if not isinstance(filt, dict):
                merged[key] = filt
                continue
            base = dict(merged.get(key) or {}) if isinstance(merged.get(key), dict) else {}
            base.update(filt)
            merged[key] = base
    try:
        merged_repr = json.dumps(merged, sort_keys=True, default=str)
    except TypeError:
        merged_repr = str(merged)
    logger.info("EXECUTION_STREAM_FILTERS_LOADED: filters=%s", merged_repr)
    return merged


def _log_execution_filter_audit_for_publish(stream_filters: Dict[str, Any]) -> None:
    """One-shot stdout + logger audit of merged stream_filters config (debug only; does not gate enabled)."""
    try:
        merged_repr = json.dumps(stream_filters, sort_keys=True, default=str)
    except TypeError:
        merged_repr = str(stream_filters)
    hdr = (
        "EXECUTION_FILTER_AUDIT_MERGED note=matrix_latest_row_plus_session_DOW_DOM "
        f"merged_stream_filters_snapshot={merged_repr}"
    )
    logger.info(hdr)
    print(hdr, flush=True)
    master_audit = stream_filters.get("master")
    if not isinstance(master_audit, dict):
        master_audit = {}
    for sid in ("ES1", "ES2", "YM1"):
        sfc = stream_filters.get(sid)
        if not isinstance(sfc, dict):
            sfc = {}
        try:
            sjson = json.dumps(sfc, sort_keys=True, default=str)
            mjson = json.dumps(master_audit, sort_keys=True, default=str)
        except TypeError:
            sjson = str(sfc)
            mjson = str(master_audit)
        line = (
            f"EXECUTION_FILTER_AUDIT_SAMPLE stream={sid} "
            f"stream_filter_config={sjson} master_filter_config={mjson}"
        )
        logger.info(line)
        print(line, flush=True)


def _execution_session_calendar_filters_eval(
    stream_id: str,
    stream_filters: Dict[str, Any],
    filter_dow_name: str,
    filter_dow: int,
    filter_dom: int,
    slot_display_time: str,
) -> Dict[str, Any]:
    """
    Evaluate DOW/DOM/exclude_times for one stream (session trading day + published slot).
    Returns a dict with passes, filter snapshots, and ``session_block_reason`` if blocked.
    No logging.
    """
    empty_stream = {}  # JSON-serializable copies for reports
    empty_master: Dict[str, Any] = {}
    if not stream_filters:
        return {
            "dow_pass": True,
            "dom_pass": True,
            "time_pass": True,
            "overall_pass": True,
            "session_block_reason": None,
            "filter_config": {"stream": empty_stream, "master": empty_master},
            "detail": {"note": "no_stream_filters_merged_dict"},
        }
    stream_filter = stream_filters.get(stream_id, {}) or {}
    if not isinstance(stream_filter, dict):
        stream_filter = {}
    master_filter = stream_filters.get("master", {}) or {}
    if not isinstance(master_filter, dict):
        master_filter = {}
    stream_snap = dict(stream_filter)
    master_snap = dict(master_filter)

    def _dow_blocked(fc: Dict[str, Any]) -> bool:
        ex = fc.get("exclude_days_of_week")
        if not ex:
            return False
        return any(d == filter_dow_name or d == str(filter_dow) for d in ex)

    def _dom_blocked(fc: Dict[str, Any]) -> bool:
        ex = fc.get("exclude_days_of_month")
        if not ex:
            return False
        excluded_dom = [int(d) for d in ex]
        return filter_dom in excluded_dom

    stream_dow_fail = _dow_blocked(stream_filter)
    master_dow_fail = _dow_blocked(master_filter)
    dow_pass = not stream_dow_fail and not master_dow_fail

    stream_dom_fail = _dom_blocked(stream_filter)
    master_dom_fail = _dom_blocked(master_filter)
    dom_pass = not stream_dom_fail and not master_dom_fail

    time_pass = True
    stream_time_fail = False
    master_time_fail = False
    time_skipped = not (slot_display_time and str(slot_display_time).strip())
    time_check_error: Optional[str] = None
    if slot_display_time and str(slot_display_time).strip():
        try:
            from modules.matrix.utils import normalize_time

            normalized_time = normalize_time(str(slot_display_time))
            if stream_filter.get("exclude_times"):
                exclude_times_normalized = [
                    normalize_time(str(t)) for t in stream_filter["exclude_times"]
                ]
                stream_time_fail = normalized_time in exclude_times_normalized
            if master_filter.get("exclude_times"):
                exclude_times_normalized = [
                    normalize_time(str(t)) for t in master_filter["exclude_times"]
                ]
                master_time_fail = normalized_time in exclude_times_normalized
            time_pass = not stream_time_fail and not master_time_fail
        except Exception as e:
            logger.warning(
                "EXECUTION_SESSION_TIME_FILTER_CHECK_FAILED stream=%s: %s",
                stream_id,
                e,
            )
            time_check_error = str(e)
            time_pass = True

    overall_pass = dow_pass and dom_pass and time_pass

    session_block_reason: Optional[str] = None
    if stream_dow_fail:
        session_block_reason = f"dow_filter_{filter_dow_name.lower()}"
    elif stream_dom_fail:
        session_block_reason = f"dom_filter_{filter_dom}"
    elif master_dow_fail:
        session_block_reason = f"master_dow_filter_{filter_dow_name.lower()}"
    elif master_dom_fail:
        session_block_reason = f"master_dom_filter_{filter_dom}"
    elif slot_display_time and str(slot_display_time).strip():
        try:
            from modules.matrix.utils import normalize_time

            normalized_time = normalize_time(str(slot_display_time))
            if stream_filter.get("exclude_times"):
                exclude_times_normalized = [
                    normalize_time(str(t)) for t in stream_filter["exclude_times"]
                ]
                if normalized_time in exclude_times_normalized:
                    session_block_reason = f"time_filter({','.join(stream_filter['exclude_times'])})"
            if session_block_reason is None and master_filter.get("exclude_times"):
                exclude_times_normalized = [
                    normalize_time(str(t)) for t in master_filter["exclude_times"]
                ]
                if normalized_time in exclude_times_normalized:
                    session_block_reason = (
                        f"master_time_filter({','.join(master_filter['exclude_times'])})"
                    )
        except Exception:
            pass

    return {
        "dow_pass": dow_pass,
        "dom_pass": dom_pass,
        "time_pass": time_pass,
        "overall_pass": overall_pass,
        "session_block_reason": session_block_reason,
        "filter_config": {"stream": stream_snap, "master": master_snap},
        "detail": {
            "filter_dow_name": filter_dow_name,
            "filter_dow": filter_dow,
            "filter_dom": filter_dom,
            "stream_dow_fail": stream_dow_fail,
            "master_dow_fail": master_dow_fail,
            "stream_dom_fail": stream_dom_fail,
            "master_dom_fail": master_dom_fail,
            "stream_time_fail": stream_time_fail,
            "master_time_fail": master_time_fail,
            "time_check_skipped_empty_slot": time_skipped,
            "time_check_error": time_check_error,
        },
    }


def _log_cursor_execution_filter_audit(
    project_root: Path,
    merged_stream_filters: Dict[str, Any],
    session_trading_date: str,
    filter_dow_name: str,
    filter_dow: int,
    filter_dom: int,
    stream_order: List[str],
    streams_by_id: Dict[str, Dict[str, Any]],
) -> None:
    """
    Operator / Cursor audit: raw filter file, calendar-rule semantics, per-stream trace.

    **Enablement** uses latest matrix row per stream (``final_allowed``), a valid slot, and DOW/DOM from
    merged **stream_filters** on **session_trading_date**. This trace evaluates full calendar eval (including
    **time_match** vs published slot) for diagnostics; **time_match** does not gate ``enabled``.

    Rule lookup is **stream_id**-keyed (plus **master**). DOW/DOM exclusions use session calendar only.
    """
    from modules.timetable.stream_id_derived import (
        instrument_from_stream_id,
        session_from_stream_id,
    )

    cfg = (Path(project_root) / "configs" / "stream_filters.json").resolve()
    cfg_exists = cfg.is_file()
    if cfg_exists:
        try:
            raw_file = cfg.read_text(encoding="utf-8")
        except OSError as e:
            raw_file = f"<READ_ERROR {e}>"
    else:
        raw_file = "<FILE_MISSING>"

    sep = "=" * 72
    print(f"{sep}\nEXECUTION_FILTER_AUDIT_CURSOR — SECTION 1: FILTER INPUT (GROUND TRUTH)\n{sep}", flush=True)
    print(f"configs/stream_filters.json path={cfg}", flush=True)
    print(f"configs/stream_filters.json exists={cfg_exists}", flush=True)
    print("stream_filters.json (raw) BEGIN", flush=True)
    print(raw_file, flush=True)
    print("stream_filters.json (raw) END", flush=True)

    print(f"\n{sep}\nEXECUTION_FILTER_AUDIT_CURSOR — SECTION 2: CALENDAR vs SESSION TRADING DATE\n{sep}", flush=True)
    print(
        "``enabled`` combines: valid slot + latest-matrix ``final_allowed`` + **DOW/DOM** from **stream_filters** "
        "using calendar derived **only** from ``session_trading_date`` (below).\n"
        "- Identity: each rule set is looked up by **stream_id** (and a shared **master** dict).\n"
        "- **dow_match** / **dom_match**: exclude_days_of_week / exclude_days_of_month vs session day; "
        "both required for enablement; failure → ``calendar_filter_blocked:<weekday>:<dom>``.\n"
        "- **time_match** in the trace: published **slot_time** vs exclude_times — **diagnostic only**, "
        "not an enablement input.\n"
        "- Default when a list is missing or empty: that calendar dimension does not block.\n"
        "- **instrument_match** / **session_match** in the trace: always **true** — JSON is stream-keyed only.\n"
        f"Session trading date evaluated: {session_trading_date!r} "
        f"(filter_dow_name={filter_dow_name!r} filter_dow_mon0={filter_dow} filter_dom={filter_dom}).",
        flush=True,
    )

    print(f"\n{sep}\nEXECUTION_FILTER_AUDIT_CURSOR — SECTION 3: RUNTIME EVALUATION TRACE\n{sep}", flush=True)
    print(
        "merged_stream_filters (runtime dict after merge with API overrides, if any) = "
        + json.dumps(merged_stream_filters, sort_keys=True, default=str),
        flush=True,
    )

    trace_rows: List[Dict[str, Any]] = []
    for sid in stream_order:
        rec = streams_by_id.get(sid) or {}
        slot = (rec.get("slot_time") or "").strip()
        derived_i = instrument_from_stream_id(sid)
        derived_s = session_from_stream_id(sid)
        ev = _execution_session_calendar_filters_eval(
            sid,
            merged_stream_filters,
            filter_dow_name,
            filter_dow,
            filter_dom,
            slot,
        )
        sf = ev["filter_config"]["stream"]
        mf = ev["filter_config"]["master"]
        ex_t_stream = sf.get("exclude_times") if isinstance(sf, dict) else None
        ex_t_master = mf.get("exclude_times") if isinstance(mf, dict) else None
        ex_d_stream = sf.get("exclude_days_of_week") if isinstance(sf, dict) else None
        ex_d_master = mf.get("exclude_days_of_week") if isinstance(mf, dict) else None

        br = rec.get("block_reason")
        if br == "no_valid_execution_slot":
            final_decision = "DISABLED:no_valid_execution_slot"
        elif isinstance(br, str) and br.startswith("matrix_filter_blocked:"):
            final_decision = f"DISABLED:{br}"
        elif isinstance(br, str) and br.startswith("calendar_filter_blocked:"):
            final_decision = f"DISABLED:{br}"
        elif br == "session_filter_blocked":
            final_decision = (
                f"DISABLED:session_filter_blocked:{ev.get('session_block_reason') or 'unknown'}"
            )
        elif rec.get("enabled"):
            final_decision = "ENABLED"
        else:
            final_decision = f"DISABLED:{br or 'unknown'}"

        trace_rows.append(
            {
                "stream_id": sid,
                "derived_instrument": derived_i,
                "derived_session": derived_s,
                "slot_time": slot,
                "filter_instrument": "__N_A_JSON_HAS_NO_INSTRUMENT_FIELD_STREAM_KEYED__",
                "filter_session": "__N_A_JSON_HAS_NO_SESSION_FIELD_STREAM_KEYED__",
                "filter_slot_time_exclude_lists": {
                    "stream_entry": ex_t_stream,
                    "master": ex_t_master,
                },
                "filter_dow_context": {
                    "session_day_dow_name": filter_dow_name,
                    "stream_exclude_days_of_week": ex_d_stream,
                    "master_exclude_days_of_week": ex_d_master,
                },
                "instrument_match": True,
                "session_match": True,
                "time_match": bool(ev["time_pass"]),
                "dow_match": bool(ev["dow_pass"]),
                "dom_match": bool(ev["dom_pass"]),
                "FINAL_DECISION": final_decision,
            }
        )

    try:
        trace_json = json.dumps(trace_rows, indent=2, sort_keys=False, default=str)
    except TypeError:
        trace_json = str(trace_rows)
    print(trace_json, flush=True)
    print(f"{sep}\nEXECUTION_FILTER_AUDIT_CURSOR — END\n{sep}\n", flush=True)

    logger.info(
        "EXECUTION_FILTER_AUDIT_CURSOR completed streams=%s session=%s",
        len(trace_rows),
        session_trading_date,
    )


def _matrix_row_series_to_final_allowed(r0: pd.Series) -> Tuple[bool, str]:
    """
    Parse ``final_allowed`` / ``filter_reasons`` from one matrix row (Series).
    Strict matrix True (``numpy.bool_`` is not ``True`` by identity).
    """
    idx = r0.index
    fa = r0["final_allowed"] if "final_allowed" in idx else None
    matrix_ok = fa is True or fa == True
    fr_raw = r0.get("filter_reasons") if "filter_reasons" in idx else None
    if fr_raw is None:
        fr_str = ""
    else:
        try:
            if pd.isna(fr_raw):
                fr_str = ""
            else:
                fr_str = str(fr_raw).strip()
        except TypeError:
            fr_str = str(fr_raw).strip()
    return matrix_ok, fr_str


def _matrix_latest_row_final_allowed_for_stream(
    master_matrix_df: pd.DataFrame,
    stream_id: str,
) -> Tuple[bool, str]:
    """
    Latest matrix state per stream: row with max ``trade_date`` for that ``stream_id``.
    Returns ``(final_allowed`` is strictly True, ``filter_reasons`` text).
    """
    if master_matrix_df.empty or "Stream" not in master_matrix_df.columns:
        return False, ""
    sub = master_matrix_df[master_matrix_df["Stream"] == stream_id]
    if sub.empty:
        return False, ""
    if "trade_date" not in master_matrix_df.columns:
        return _matrix_row_series_to_final_allowed(sub.iloc[0])
    sub_td = sub.dropna(subset=["trade_date"])
    if sub_td.empty:
        return False, ""
    latest = sub_td.sort_values("trade_date", ascending=False).iloc[0]
    return _matrix_row_series_to_final_allowed(latest)


def _matrix_row_final_allowed_for_session(
    latest_df: pd.DataFrame,
    stream_id: str,
) -> Tuple[bool, str]:
    """
    Single matrix row for (stream_id, session trading_date slice in ``latest_df``).
    Returns (final_allowed is strictly True, filter_reasons text for block_reason suffix).
    Missing row / missing column / non-True ``final_allowed`` → (False, reasons or "").
    """
    if latest_df.empty or "Stream" not in latest_df.columns:
        return False, ""
    mrow = latest_df[latest_df["Stream"] == stream_id]
    if mrow.empty:
        return False, ""
    return _matrix_row_series_to_final_allowed(mrow.iloc[0])


def _execution_session_calendar_filters_pass(
    stream_id: str,
    stream_filters: Dict[str, Any],
    filter_dow_name: str,
    filter_dow: int,
    filter_dom: int,
    slot_display_time: str,
) -> Tuple[bool, Optional[str]]:
    """
    DOW/DOM/exclude_times from ``stream_filters`` evaluated for the **session trading day**
    (not the matrix eligibility / ``allowed_df`` date).
    ``slot_display_time`` is the published slot (HH:MM); when empty, time exclusions are skipped.
    """
    ev = _execution_session_calendar_filters_eval(
        stream_id,
        stream_filters,
        filter_dow_name,
        filter_dow,
        filter_dom,
        slot_display_time,
    )
    return ev["overall_pass"], ev["session_block_reason"]


def _parse_slot_time(t: Any) -> datetime:
    """Parse ``HH:MM`` for descending sort; invalid/missing → min so they sort last."""
    try:
        raw = (t or "").strip() if t is not None else ""
        if not raw:
            return datetime.min
        return datetime.strptime(raw, "%H:%M")
    except Exception:
        return datetime.min


def _execution_slot_order_and_display_map(
    session_slots: List[str],
) -> Tuple[List[str], Dict[str, str]]:
    """Session slot order as normalized HH:MM, plus norm -> first canonical display string."""
    from modules.matrix.utils import normalize_time

    order: List[str] = []
    display: Dict[str, str] = {}
    for raw in session_slots:
        n = normalize_time(str(raw).strip())
        order.append(n)
        display.setdefault(n, str(raw).strip())
    return order, display


def _execution_instrument_slots_ordered(
    session: str,
    instrument: str,
    session_time_slots: Dict[str, List[str]],
) -> List[str]:
    """Session slots in order, minus S1 early open when instrument is not allowed (timetable write guard parity)."""
    from modules.matrix.utils import normalize_time

    slots = session_time_slots.get(session, [])
    full_order, _ = _execution_slot_order_and_display_map(slots)
    early_norm = normalize_time(S1_EARLY_OPEN_SLOT_TIME)
    inst_up = (instrument or "").strip().upper()
    out: List[str] = []
    for n in full_order:
        if (
            session == "S1"
            and n == early_norm
            and inst_up not in S1_INSTRUMENTS_ALLOWED_EARLY_OPEN_SLOT
        ):
            continue
        out.append(n)
    return out


def _pick_slot_from_preference(
    full_order_norm: List[str],
    candidates_norm: List[str],
    preferred_norm: Optional[str],
) -> Optional[str]:
    """
    Choose normalized slot from candidates_norm using matrix preference as anchor in full_order_norm.

    If preferred is missing or not in full_order_norm, first candidate in session order.
    If preferred is not in candidates, scan forward in full order then backward for the nearest candidate.
    """
    if not candidates_norm:
        return None
    cand_set = set(candidates_norm)
    if not preferred_norm or preferred_norm not in full_order_norm:
        return candidates_norm[0]
    if preferred_norm in cand_set:
        return preferred_norm
    try:
        idx = full_order_norm.index(preferred_norm)
    except ValueError:
        return candidates_norm[0]
    for j in range(idx + 1, len(full_order_norm)):
        if full_order_norm[j] in cand_set:
            return full_order_norm[j]
    for j in range(idx - 1, -1, -1):
        if full_order_norm[j] in cand_set:
            return full_order_norm[j]
    return None


class TimetableLivePublishBlocked(RuntimeError):
    """Writing live data/timetable/timetable_current.json from a blocked (non-approved) code path."""


class TimetableWriteBlockedCmeMismatch(RuntimeError):
    """Live execution: session_trading_date must equal get_cme_trading_date(now) when CME enforcement is on."""


class TimetableEngine:
    """
    Generates a timetable showing which trades to take today based on rules and filters.
    """
    
    def __init__(self, master_matrix_dir: str = "data/master_matrix",
                 analyzer_runs_dir: str = "data/analyzed",
                 timetable_output_dir: Optional[str] = None,
                 project_root: Optional[str] = None):
        """
        Initialize Timetable Engine.
        
        Args:
            master_matrix_dir: Directory containing master matrix files
            analyzer_runs_dir: Directory containing analyzer output files (for RS calculation)
            timetable_output_dir: If set, write timetable to this dir as timetable_copy.json (not timetable_current.json)
            project_root: Repo root for logs/timetable_publish.jsonl (default: infer from this file location)
        """
        self.master_matrix_dir = Path(master_matrix_dir)
        self.analyzer_runs_dir = Path(analyzer_runs_dir)
        self.timetable_output_dir = timetable_output_dir
        self._project_root = Path(project_root).resolve() if project_root else Path(__file__).resolve().parents[2]
        
        # Streams to process
        self.streams = [
            "ES1", "ES2", "GC1", "GC2", "CL1", "CL2",
            "NQ1", "NQ2", "NG1", "NG2", "YM1", "YM2",
            "RTY1", "RTY2"
        ]
        
        # Day-of-month blocked days for "2" streams (from centralized config)
        self.dom_blocked_days = DOM_BLOCKED_DAYS
        
        # SCF threshold (from centralized config, but can be overridden)
        self.scf_threshold = SCF_THRESHOLD
        
        # Available time slots by session (from centralized config - SINGLE SOURCE OF TRUTH)
        self.session_time_slots = SLOT_ENDS
        
        # File list cache to avoid repeated rglob() calls
        self._file_list_cache = {}
        # SCF lookup cache: (stream_id, trade_date) -> (scf_s1, scf_s2), max 1000 entries
        self._scf_cache: Dict[tuple, Tuple[Optional[float], Optional[float]]] = {}
        self._scf_cache_max_size = 1000
    
    def _maybe_evict_scf_cache(self) -> None:
        """Evict oldest entries if cache exceeds max size."""
        if len(self._scf_cache) > self._scf_cache_max_size:
            keys_to_remove = list(self._scf_cache.keys())[: len(self._scf_cache) - self._scf_cache_max_size]
            for k in keys_to_remove:
                del self._scf_cache[k]
    
    def _get_parquet_files(self, stream_dir: Path) -> List[Path]:
        """
        Get parquet files for a stream directory with caching.
        
        Args:
            stream_dir: Stream directory path
            
        Returns:
            Sorted list of parquet files (most recent first)
        """
        if stream_dir not in self._file_list_cache:
            self._file_list_cache[stream_dir] = sorted(
                stream_dir.rglob("*.parquet"), reverse=True
            )
        return self._file_list_cache[stream_dir]
    
    def calculate_rs_for_stream(self, stream_id: str, session: str, 
                               lookback_days: int = 13) -> Dict[str, float]:
        """
        Calculate RS (Rolling Sum) values for each time slot in a stream/session.
        
        This simulates the RS calculation from sequential processor logic:
        - Win = +1 point
        - Loss = -2 points
        - BE = 0 points
        - Rolling sum over last 13 trades
        
        Args:
            stream_id: Stream identifier (e.g., "ES1", "GC2")
            session: Session ("S1" or "S2")
            lookback_days: Number of days to look back (default 13)
            
        Returns:
            Dictionary mapping time slots to RS values
        """
        stream_dir = self.analyzer_runs_dir / stream_id
        
        if not stream_dir.exists():
            return {}
        
        # Find most recent parquet files (using cache)
        parquet_files = self._get_parquet_files(stream_dir)
        
        if not parquet_files:
            return {}
        
        # Load recent data (last N files or last N days)
        all_trades = []
        for file_path in parquet_files[:10]:  # Load last 10 files
            try:
                df = pd.read_parquet(file_path)
                if df.empty:
                    continue
                
                # Validate required columns
                if 'Result' not in df.columns:
                    logger.warning(f"File {file_path.name} missing 'Result' column, skipping")
                    continue
                
                # CONTRACT ENFORCEMENT: Require trade_date column
                if 'trade_date' not in df.columns:
                    raise ValueError(
                        f"File {file_path.name} missing trade_date column - "
                        f"analyzer output contract requires trade_date. "
                        f"Timetable Engine does not normalize dates. "
                        f"Fix analyzer output before proceeding."
                    )
                
                # Validate dtype/presence only (no normalization)
                _validate_trade_date_dtype(df, stream_id)
                _validate_trade_date_presence(df, stream_id)
                
                # CONTRACT ENFORCEMENT: Invalid trade_date values → ValueError
                invalid_dates = df['trade_date'].isna()
                if invalid_dates.any():
                    invalid_count = invalid_dates.sum()
                    raise ValueError(
                        f"File {file_path.name}: Found {invalid_count} rows with invalid trade_date. "
                        f"This violates analyzer output contract. Fix analyzer output before proceeding."
                    )
                
                all_trades.append(df)
            except Exception as e:
                # Log contract violations but continue (don't fail entire timetable generation)
                # This allows timetable to be generated even if some files have issues
                if isinstance(e, ValueError):
                    logger.warning(
                        f"Contract violation in {file_path.name}: {e}. "
                        f"Skipping this file (timetable generation will continue with available data)."
                    )
                    import traceback
                    logger.debug(f"Traceback: {traceback.format_exc()}")
                    continue
                # Log other errors but continue
                logger.warning(f"Error loading {file_path}: {e}")
                import traceback
                logger.debug(f"Traceback: {traceback.format_exc()}")
                continue
        
        # Handle missing data gracefully - return empty dict instead of raising error
        # This allows timetable generation to continue even if some streams have no data
        if not all_trades:
            logger.warning(
                f"Stream {stream_id} session {session}: No valid trade data found for RS calculation. "
                f"Returning empty RS values (will use default time slot)."
            )
            return {}
        
        # Merge and filter by session
        df = pd.concat(all_trades, ignore_index=True)
        df = df[df['Session'] == session].copy()
        
        if df.empty:
            return {}
        
        # Sort by trade_date (already datetime dtype)
        df = df.sort_values('trade_date').reset_index(drop=True)
        
        # Vectorized score mapping: WIN=+1, LOSS=-2, else 0
        result_clean = df['Result'].fillna('').astype(str).str.strip().str.upper()
        df = df.copy()
        df['_score'] = result_clean.map({'WIN': 1, 'LOSS': -2}).fillna(0)
        
        # Get last N trades per time slot and sum scores (vectorized)
        time_slot_rs = {}
        for time_slot in self.session_time_slots.get(session, []):
            time_trades = df[df['Time'] == time_slot].tail(lookback_days)
            if time_trades.empty:
                time_slot_rs[time_slot] = 0.0
            else:
                time_slot_rs[time_slot] = float(time_trades['_score'].sum())
        
        return time_slot_rs
    
    def select_best_time(self, stream_id: str, session: str) -> Tuple[Optional[str], str]:
        """
        Select the best time slot for a stream/session based on RS values.
        
        Args:
            stream_id: Stream identifier
            session: Session ("S1" or "S2")
            
        Returns:
            Tuple of (selected_time, reason)
        """
        rs_values = self.calculate_rs_for_stream(stream_id, session)
        
        if not rs_values:
            return None, "no_data"
        
        # Find time slot with highest RS
        best_time = max(rs_values.items(), key=lambda x: x[1])
        
        if best_time[1] <= 0:
            # All RS values are 0 or negative, use first available time slot
            available_times = self.session_time_slots.get(session, [])
            if available_times:
                return available_times[0], "default_first_time"
        
        return best_time[0], "RS_best_time"
    
    def check_filters(self, trade_date: date, stream_id: str, session: str,
                     scf_s1: Optional[float] = None,
                     scf_s2: Optional[float] = None) -> Tuple[bool, str]:
        """
        Check if a trade should be allowed based on filters.
        
        Args:
            trade_date: Trading date
            stream_id: Stream identifier
            session: Session ("S1" or "S2")
            scf_s1: SCF value for S1 (if available)
            scf_s2: SCF value for S2 (if available)
            
        Returns:
            Tuple of (allowed, reason)
        """
        day_of_month = trade_date.day
        
        # 1. Day-of-month filter for "2" streams
        is_two_stream = stream_id.endswith('2')
        if is_two_stream and day_of_month in self.dom_blocked_days:
            return False, f"dom_blocked_{day_of_month}"
        
        # 2. SCF filter
        if session == "S1" and scf_s1 is not None:
            if scf_s1 >= self.scf_threshold:
                return False, "scf_blocked"
        
        if session == "S2" and scf_s2 is not None:
            if scf_s2 >= self.scf_threshold:
                return False, "scf_blocked"
        
        # 3. Other filters (Wednesday no-trade, etc.) would go here
        # For now, assuming all days are valid unless filtered above
        
        return True, "allowed"
    
    def get_scf_values(self, stream_id: str, trade_date: date) -> Tuple[Optional[float], Optional[float]]:
        """
        Get SCF values for a stream on a specific date.
        Uses instance cache to avoid repeated parquet reads for same (stream_id, trade_date).
        
        Args:
            stream_id: Stream identifier
            trade_date: Trading date
            
        Returns:
            Tuple of (scf_s1, scf_s2) or (None, None) if not found
        """
        cache_key = (stream_id, trade_date)
        if cache_key in self._scf_cache:
            return self._scf_cache[cache_key]
        
        stream_dir = self.analyzer_runs_dir / stream_id
        
        if not stream_dir.exists():
            self._scf_cache[cache_key] = (None, None)
            self._maybe_evict_scf_cache()
            return None, None
        
        # Find parquet files for this date
        year = trade_date.year
        month = trade_date.month
        
        # Try to find file for this month/year
        file_pattern = f"{stream_id}_an_{year}_{month:02d}.parquet"
        file_path = stream_dir / str(year) / file_pattern
        
        if not file_path.exists():
            # Try alternative patterns (using cache)
            parquet_files = self._get_parquet_files(stream_dir)
            for pf in parquet_files:
                try:
                    df = pd.read_parquet(pf)
                    if not df.empty:
                        # CONTRACT ENFORCEMENT: Require trade_date column
                        if 'trade_date' not in df.columns:
                            continue  # Skip files without trade_date
                        _validate_trade_date_dtype(df, stream_id)
                        # Ensure trade_date is datetime dtype before using .dt accessor
                        if not pd.api.types.is_datetime64_any_dtype(df['trade_date']):
                            continue  # Skip files with invalid dtype
                        if (df['trade_date'].dt.date == trade_date).any():
                            file_path = pf
                            break
                except:
                    continue
        
        if not file_path.exists():
            self._scf_cache[cache_key] = (None, None)
            self._maybe_evict_scf_cache()
            return None, None
        
        try:
            df = pd.read_parquet(file_path)
            
            # CONTRACT ENFORCEMENT: Require trade_date column
            if 'trade_date' not in df.columns:
                raise ValueError(
                    f"File {file_path.name} missing trade_date column - "
                    f"analyzer output contract requires trade_date. "
                    f"Timetable Engine does not normalize dates. "
                    f"Fix analyzer output before proceeding."
                )
            
            # Validate dtype only (no normalization, no re-parsing)
            _validate_trade_date_dtype(df, stream_id)
            # Ensure trade_date is datetime dtype before using .dt accessor
            if not pd.api.types.is_datetime64_any_dtype(df['trade_date']):
                raise ValueError(
                    f"File {file_path.name} trade_date column is not datetime dtype after validation: {df['trade_date'].dtype}. "
                    f"This is a contract violation. Analyzer output must have normalized trade_date column."
                )
            day_data = df[df['trade_date'].dt.date == trade_date]
            
            if day_data.empty:
                result = (None, None)
            else:
                scf_s1 = day_data['scf_s1'].iloc[0] if 'scf_s1' in day_data.columns else None
                scf_s2 = day_data['scf_s2'].iloc[0] if 'scf_s2' in day_data.columns else None
                result = (scf_s1, scf_s2)
            self._scf_cache[cache_key] = result
            self._maybe_evict_scf_cache()
            return result
        except Exception as e:
            logger.debug(f"Error reading SCF values from {file_path}: {e}")
            self._scf_cache[cache_key] = (None, None)
            self._maybe_evict_scf_cache()
            return None, None
    
    def generate_timetable(self, trade_date: Optional[str] = None) -> pd.DataFrame:
        """
        Generate timetable for a specific trading day.
        
        Args:
            trade_date: Trading date (YYYY-MM-DD) or None for today (Chicago time)
            
        Returns:
            DataFrame with timetable entries
        """
        if trade_date is None:
            # Use Chicago timezone to get today's date (consistent with robot engine)
            chicago_tz = pytz.timezone("America/Chicago")
            chicago_now = datetime.now(chicago_tz)
            trade_date = chicago_now.date().isoformat()
        
        trade_date_obj = pd.to_datetime(trade_date).date()
        
        logger.info("=" * 80)
        logger.info(f"GENERATING TIMETABLE FOR {trade_date}")
        logger.info("=" * 80)
        
        # OPTIMIZATION: Pre-load all SCF values once (batch loading)
        # SCF values are per-stream, not per-session, so we can load once and reuse
        scf_cache = {}
        for stream_id in self.streams:
            scf_cache[stream_id] = self.get_scf_values(stream_id, trade_date_obj)
        
        timetable_rows = []
        
        for stream_id in self.streams:
            # Extract instrument from stream_id
            instrument = stream_id[:-1]  # ES1 -> ES
            
            # Process both sessions
            for session in ["S1", "S2"]:
                # Get SCF values from cache (pre-loaded above)
                scf_s1, scf_s2 = scf_cache[stream_id]
                
                # Select best time based on RS
                try:
                    selected_time, time_reason = self.select_best_time(stream_id, session)
                except Exception as e:
                    logger.warning(f"Error selecting best time for {stream_id} {session}: {e}")
                    # Fallback to default time on error
                    available_times = self.session_time_slots.get(session, [])
                    selected_time = available_times[0] if available_times else ""
                    time_reason = f"error_fallback_{str(e)[:50]}"
                
                # CRITICAL: If time selection fails, use default time and mark as blocked
                # NEVER skip streams - all streams must be present in timetable
                if selected_time is None:
                    # Use first available time slot as default (sequencer intent)
                    available_times = self.session_time_slots.get(session, [])
                    selected_time = available_times[0] if available_times else ""
                    block_reason = "no_rs_data"
                    allowed = False
                    final_reason = f"{time_reason}_{block_reason}"
                else:
                    block_reason = None
                    # Check filters
                    allowed, filter_reason = self.check_filters(
                        trade_date_obj, stream_id, session, scf_s1, scf_s2
                    )
                    
                    # Combine reasons
                    if not allowed:
                        final_reason = filter_reason
                        block_reason = filter_reason
                    else:
                        final_reason = time_reason
                
                # ALWAYS append - never skip (timetable must contain all streams)
                timetable_rows.append({
                    'trade_date': trade_date,
                    'symbol': instrument,
                    'stream_id': stream_id,
                    'session': session,
                    'selected_time': selected_time,
                    'reason': final_reason,
                    'allowed': allowed,
                    'block_reason': block_reason,
                    'scf_s1': scf_s1,
                    'scf_s2': scf_s2,
                    'day_of_month': trade_date_obj.day,
                    'dow': trade_date_obj.strftime('%a')
                })
        
        timetable_df = pd.DataFrame(timetable_rows)
        
        logger.info(f"Timetable generated: {len(timetable_df)} entries")
        logger.info(f"Allowed trades: {timetable_df['allowed'].sum()} / {len(timetable_df)}")

        # Do not write timetable_current.json here — analyzer path is not an approved live publisher.
        # Callers save via save_timetable() or use matrix-first write_execution_timetable_from_master_matrix(..., execution_mode=True).

        return timetable_df
    
    def save_timetable(self, timetable_df: pd.DataFrame, 
                       output_dir: str = "data/timetable") -> Tuple[Path, Path]:
        """
        Save timetable to file.
        
        Args:
            timetable_df: Timetable DataFrame
            output_dir: Output directory
            
        Returns:
            Tuple of (parquet_file, json_file) paths
        """
        output_path = Path(output_dir)
        output_path.mkdir(parents=True, exist_ok=True)
        
        if not timetable_df.empty:
            trade_date = timetable_df['trade_date'].iloc[0]
        else:
            # Use Chicago timezone to get today's date (consistent with robot engine)
            chicago_tz = pytz.timezone("America/Chicago")
            chicago_now = datetime.now(chicago_tz)
            trade_date = chicago_now.date().isoformat()
        timestamp = datetime.now().strftime('%Y%m%d_%H%M%S')
        
        parquet_file = output_path / f"timetable_{trade_date.replace('-', '')}_{timestamp}.parquet"
        json_file = output_path / f"timetable_{trade_date.replace('-', '')}_{timestamp}.json"
        
        # Save as Parquet
        timetable_df.to_parquet(parquet_file, index=False, compression='snappy')
        logger.info(f"Saved: {parquet_file}")
        
        # Save as JSON
        timetable_df.to_json(json_file, orient='records', date_format='iso', indent=2)
        logger.info(f"Saved: {json_file}")
        
        return parquet_file, json_file
    
    def write_execution_timetable_from_master_matrix(
        self,
        master_matrix_df: pd.DataFrame,
        trade_date: Optional[str] = None,
        stream_filters: Optional[Dict] = None,
        execution_mode: bool = False,
        replay: bool = False,
        publish_context: Optional[Dict[str, Any]] = None,
    ) -> Optional[TimetablePublishResult]:
        """
        Write execution timetable from master matrix data.

        This is the authoritative persistence point - called when master matrix is finalized.
        Generates timetable from latest date in master matrix, applying filters.

        Args:
            master_matrix_df: Master matrix DataFrame
            trade_date: Optional trading date (YYYY-MM-DD). If None, uses latest date in matrix.
            stream_filters: Optional per-stream filter dict. Non-execution: DOW/DOM/exclude_times for
                display paths. Execution: merged with configs/stream_filters.json; **DOW/DOM** gate ``enabled``
                on ``session_trading_date``; exclude_times does not gate ``enabled``; ``slot_time`` follows
                matrix Time / Time Change.
            execution_mode: If True, matrix-backed execution streams with calendar DOW/DOM from ``stream_filters``.
            replay: If True with execution_mode, ``trade_date`` pins ``session_trading_date`` and CME check is off.
            publish_context: Optional ``{"source": "...", "reason": "..."}`` for audit / history metadata.
        """
        utc_now = datetime.now(timezone.utc)
        if execution_mode:
            if replay:
                session_trading_date = (trade_date or "").strip()
                if not session_trading_date:
                    raise ValueError(
                        "write_execution_timetable_from_master_matrix(..., execution_mode=True, replay=True) "
                        "requires trade_date (YYYY-MM-DD)"
                    )
            else:
                session_trading_date = get_cme_trading_date(utc_now)
        else:
            if not self.timetable_output_dir:
                raise TimetableLivePublishBlocked(
                    "write_execution_timetable_from_master_matrix(..., execution_mode=False) requires "
                    "TimetableEngine(..., timetable_output_dir=...) so output goes to timetable_copy.json only. "
                    "Live timetable_current.json requires execution_mode=True (matrix pipeline / CME session)."
                )
            session_trading_date = (trade_date or "").strip() or get_cme_trading_date(utc_now)
        streams = self.build_streams_from_master_matrix(
            master_matrix_df,
            session_trading_date,
            stream_filters,
            execution_mode,
            execution_replay=replay,
        )
        if not streams:
            return None
        eligibility_trade_date = eligibility_trade_date_ymd_from_matrix_df(master_matrix_df)
        if execution_mode:
            _log_execution_timetable_audit_snapshot(
                master_matrix_df=master_matrix_df,
                session_trading_date=session_trading_date,
                eligibility_trade_date=eligibility_trade_date,
                streams=streams,
            )
        pub = publish_context or {}
        doc_source = (pub.get("source") or "master_matrix").strip() or "master_matrix"
        # Scratch / analyzer copy path — no versioning
        if self.timetable_output_dir:
            self._write_execution_timetable_file(
                streams,
                session_trading_date,
                ledger_writer="matrix",
                ledger_source=doc_source,
                execution_document_source=doc_source,
                enforce_cme_live=False,
                eligibility_trade_date=eligibility_trade_date,
            )
            return None

        return self._publish_live_execution_timetable_versioned(
            streams,
            session_trading_date,
            execution_document_source=doc_source,
            ledger_writer="matrix",
            ledger_source="master_matrix",
            enforce_cme_live=execution_mode and not replay,
            publish_context=publish_context,
            eligibility_trade_date=eligibility_trade_date,
        )

    def build_timetable_dataframe_from_master_matrix(
        self,
        master_matrix_df: pd.DataFrame,
        trade_date: Optional[str] = None,
        stream_filters: Optional[Dict] = None,
        execution_mode: bool = False,
    ) -> pd.DataFrame:
        """
        Build timetable DataFrame from master matrix (no analyzer reads).
        Matrix is the authoritative source; RS/SCF/points come from matrix.

        Returns DataFrame with columns matching generate_timetable for display/API parity.
        """
        utc_now = datetime.now(timezone.utc)
        if execution_mode:
            eff_trade_date = get_cme_trading_date(utc_now)
        else:
            eff_trade_date = trade_date
        streams = self.build_streams_from_master_matrix(
            master_matrix_df, eff_trade_date, stream_filters, execution_mode
        )
        if not streams:
            return pd.DataFrame()

        trade_date_str = eff_trade_date if execution_mode else trade_date
        if not trade_date_str:
            if not master_matrix_df.empty and 'trade_date' in master_matrix_df.columns:
                latest = master_matrix_df['trade_date'].max()
                trade_date_str = latest.strftime('%Y-%m-%d') if hasattr(latest, 'strftime') else str(latest)[:10]
            else:
                chicago_tz = pytz.timezone("America/Chicago")
                trade_date_str = datetime.now(chicago_tz).date().isoformat()
        trade_date_obj = pd.to_datetime(trade_date_str).date()

        # Build scf lookup from matrix (stream -> (scf_s1, scf_s2) for trade_date)
        scf_lookup = {}
        if not master_matrix_df.empty and 'trade_date' in master_matrix_df.columns:
            date_df = master_matrix_df[master_matrix_df['trade_date'].dt.date == trade_date_obj]
            for stream_id in self.streams:
                row = date_df[date_df['Stream'] == stream_id]
                if not row.empty:
                    r = row.iloc[0]
                    scf_s1 = r.get('scf_s1') if 'scf_s1' in r.index else None
                    scf_s2 = r.get('scf_s2') if 'scf_s2' in r.index else None
                    if pd.notna(scf_s1) or pd.notna(scf_s2):
                        scf_lookup[stream_id] = (float(scf_s1) if pd.notna(scf_s1) else None, float(scf_s2) if pd.notna(scf_s2) else None)
                    else:
                        scf_lookup[stream_id] = (None, None)
                else:
                    scf_lookup[stream_id] = (None, None)
        else:
            scf_lookup = {s: (None, None) for s in self.streams}

        rows = []
        from modules.timetable.stream_id_derived import (
            instrument_from_stream_id,
            session_from_stream_id,
        )

        for s in streams:
            stream_id = s['stream']
            scf_s1, scf_s2 = scf_lookup.get(stream_id, (None, None))
            reason = s.get('block_reason') or 'matrix_derived'
            rows.append({
                'trade_date': trade_date_str,
                'symbol': instrument_from_stream_id(stream_id),
                'stream_id': stream_id,
                'session': session_from_stream_id(stream_id),
                'selected_time': s['slot_time'],
                'reason': reason,
                'allowed': s['enabled'],
                'block_reason': s.get('block_reason'),
                'scf_s1': scf_s1,
                'scf_s2': scf_s2,
                'day_of_month': trade_date_obj.day,
                'dow': trade_date_obj.strftime('%a'),
            })
        return pd.DataFrame(rows)

    def build_streams_from_master_matrix(
        self,
        master_matrix_df: pd.DataFrame,
        trade_date: Optional[str] = None,
        stream_filters: Optional[Dict] = None,
        execution_mode: bool = False,
        execution_replay: bool = False,
    ) -> List[Dict]:
        """
        Build streams list (enabled/block_reason per stream) from master matrix.

        When execution_mode=True: ``enabled`` requires valid slot, **latest** matrix row ``final_allowed``
        (max ``trade_date`` per stream), and **DOW/DOM** from merged ``stream_filters`` on
        ``session_trading_date`` (exclude_times does not gate ``enabled``). Slot times from matrix Time /
        Time Change. Live execution ignores ``trade_date`` for session/matrix day:
        ``get_cme_trading_date(now)`` only.
        Use ``execution_replay=True`` and ``trade_date=YYYY-MM-DD`` to pin session (tests / replay writes).
        """
        if master_matrix_df.empty:
            if execution_mode:
                if execution_replay:
                    pin = (trade_date or "").strip()
                    if not pin:
                        raise ValueError(
                            "build_streams_from_master_matrix(..., execution_mode=True, execution_replay=True) "
                            "requires trade_date when matrix is empty"
                        )
                    trade_date_obj_empty = pd.to_datetime(pin).date()
                else:
                    trade_date_obj_empty = pd.to_datetime(
                        get_cme_trading_date(datetime.now(timezone.utc))
                    ).date()
                eff_empty = _merge_stream_filters_for_execution(
                    self._project_root, stream_filters
                )
                _log_execution_filter_audit_for_publish(eff_empty)
                return self._build_all_disabled_streams(
                    trade_date_obj_empty, "no_valid_execution_slot"
                )
            logger.warning("Master matrix is empty, cannot build streams")
            return []

        # CONTRACT ENFORCEMENT: Require trade_date column when matrix is non-empty
        if not master_matrix_df.empty:
            if 'trade_date' not in master_matrix_df.columns:
                raise ValueError(
                    "Master matrix missing trade_date column - DataLoader must normalize dates before timetable generation. "
                    "This is a contract violation. Timetable Engine does not normalize dates."
                )
            _validate_trade_date_presence(master_matrix_df, "master_matrix")
            _validate_trade_date_dtype(master_matrix_df, "master_matrix")
            if not pd.api.types.is_datetime64_any_dtype(master_matrix_df['trade_date']):
                raise ValueError(
                    f"Master matrix trade_date column is not datetime dtype after validation: {master_matrix_df['trade_date'].dtype}. "
                    f"This is a contract violation. DataLoader must normalize dates before timetable generation."
                )

        if execution_mode:
            eff_stream_filters = _merge_stream_filters_for_execution(
                self._project_root, stream_filters
            )
            _log_execution_filter_audit_for_publish(eff_stream_filters)
            if execution_replay:
                pin = (trade_date or "").strip()
                if not pin:
                    raise ValueError(
                        "build_streams_from_master_matrix(..., execution_mode=True, execution_replay=True) "
                        "requires trade_date (YYYY-MM-DD)"
                    )
                session_trading_date_ymd = pd.to_datetime(pin).date().isoformat()
            else:
                session_trading_date_ymd = get_cme_trading_date(datetime.now(timezone.utc))
            trade_date_obj = pd.to_datetime(session_trading_date_ymd).date()
            logger.info(
                "PATH_B_EXECUTION_MODE: session_trading_date=%s (%s); "
                "enabled=matrix(valid_slot+latest_row_final_allowed+DOW/DOM_on_session_day); "
                "stream_filters merged keys=%s (DOW/DOM gate; exclude_times does not gate)",
                session_trading_date_ymd,
                "replay_pin" if execution_replay else "get_cme_trading_date",
                len(eff_stream_filters),
            )
            return self._build_streams_execution_mode(
                master_matrix_df,
                trade_date_obj,
                session_trading_date_ymd,
                eff_stream_filters,
            )

        # Get latest date from master matrix if not provided (non-execution only)
        if trade_date is None:
            if master_matrix_df.empty:
                chicago_tz = pytz.timezone("America/Chicago")
                trade_date = datetime.now(chicago_tz).date().isoformat()
            else:
                latest_date = master_matrix_df['trade_date'].max()
                trade_date = latest_date.strftime('%Y-%m-%d')

        trade_date_obj = pd.to_datetime(trade_date).date()

        # Check if we have data for target date or previous/most-recent date
        # For future dates (no data yet), use previous day or most recent date in matrix
        from datetime import timedelta
        previous_date_obj = trade_date_obj - timedelta(days=1)
        previous_df = master_matrix_df[master_matrix_df['trade_date'].dt.date == previous_date_obj].copy()
        latest_df = master_matrix_df[master_matrix_df['trade_date'].dt.date == trade_date_obj].copy()

        # No data for target date (future or not yet in matrix) — use previous day or most recent date
        if latest_df.empty:
            if not previous_df.empty:
                logger.info(
                    f"No data for date {trade_date_obj} (future/not yet traded); "
                    f"using previous day {previous_date_obj} for eligibility and time slots"
                )
                source_df = previous_df
                use_previous_day_logic = False  # source is previous, no "time change" semantics
                latest_df = previous_df  # use for final_allowed
            else:
                # Use most recent date in matrix (any date before target)
                before_target = master_matrix_df[master_matrix_df['trade_date'].dt.date < trade_date_obj]
                if before_target.empty:
                    logger.warning(
                        f"No data before date {trade_date_obj}, returning all streams disabled (MATRIX_DATE_MISSING)"
                    )
                    return self._build_all_disabled_streams(trade_date_obj, "MATRIX_DATE_MISSING")
                most_recent_date = before_target['trade_date'].max().date()
                source_df = master_matrix_df[master_matrix_df['trade_date'].dt.date == most_recent_date].copy()
                latest_df = source_df.copy()
                use_previous_day_logic = False
                logger.info(
                    f"No data for date {trade_date_obj}; using most recent {most_recent_date} for eligibility"
                )
        else:
            # Have data for target date
            if previous_df.empty:
                logger.info(f"No data for previous date {previous_date_obj}, using current date {trade_date_obj} data")
                source_df = latest_df.copy()
                use_previous_day_logic = False
            else:
                source_df = previous_df
                use_previous_day_logic = True

        if source_df.empty:
            logger.warning(f"No source data for date {trade_date_obj}, returning all streams disabled")
            return self._build_all_disabled_streams(trade_date_obj, "MATRIX_DATE_MISSING")
        
        # Extract day-of-week and day-of-month for filtering (use target date, not source date)
        target_dow = trade_date_obj.weekday()  # 0=Monday, 6=Sunday
        target_dom = trade_date_obj.day
        
        # Build streams array from master matrix data
        # CRITICAL: Must include ALL streams (complete execution contract)
        # Streams not in master matrix or filtered are included with enabled=false
        streams_dict = {}  # stream_id -> stream_entry
        seen_streams = set()
        
        # Day names for DOW filtering (0=Monday, 6=Sunday)
        day_names = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday']
        target_dow_name = day_names[target_dow]
        
        # Check if final_allowed column exists (authoritative filter indicator)
        # Use current date's data for final_allowed check (filters apply to target date, not source date)
        latest_df = master_matrix_df[master_matrix_df['trade_date'].dt.date == trade_date_obj].copy()
        has_final_allowed = 'final_allowed' in latest_df.columns if not latest_df.empty else False
        
        # First pass: Process streams that exist in master matrix (vectorized - use itertuples)
        # Use previous day's data to determine time slot, but current day's data for filters
        # Drop duplicates by Stream (keep last) - source_df should have at most one per stream
        source_unique = source_df.drop_duplicates(subset=['Stream'], keep='last')
        for row in source_unique.itertuples(index=False):
            stream = getattr(row, 'Stream', '') or ''
            if not stream or stream in seen_streams:
                continue
            
            seen_streams.add(stream)
            
            # Extract instrument and session from stream
            instrument = stream[:-1] if len(stream) > 1 else ''
            
            # Use Session column from master matrix if available (itertuples: spaces -> underscores)
            sess = getattr(row, 'Session', None)
            if sess is not None and pd.notna(sess):
                session = str(sess).strip()
                if session not in ['S1', 'S2']:
                    logger.warning(f"Stream {stream}: Invalid session '{session}', using stream_id pattern")
                    session = 'S1' if stream.endswith('1') else 'S2'
            else:
                session = 'S1' if stream.endswith('1') else 'S2'
            
            # CRITICAL FIX: Get time from PREVIOUS day's row
            # The sequencer sets Time Change only when there's a LOSS (decide_time_change returns non-None)
            # Time Change in previous day's row tells us what time to use TODAY
            if use_previous_day_logic:
                time = getattr(row, 'Time', '') or ''
                time_change = getattr(row, 'Time_Change', '') or ''
                if time_change and str(time_change).strip():
                    time_change_str = str(time_change).strip()
                    if '->' in time_change_str:
                        parts = time_change_str.split('->')
                        if len(parts) == 2:
                            time = parts[1].strip()
                    else:
                        time = time_change_str
            else:
                time = getattr(row, 'Time', '') or ''
            
            # Validate that parsed time is in session_time_slots
            if time:
                from modules.matrix.utils import normalize_time
                normalized_time = normalize_time(str(time))
                available_times_normalized = [normalize_time(str(t)) for t in self.session_time_slots.get(session, [])]
                
                if normalized_time not in available_times_normalized:
                    logger.warning(
                        f"Stream {stream}: Parsed time '{time}' (normalized: '{normalized_time}') "
                        f"is not in available times for session {session}. "
                        f"Available: {self.session_time_slots.get(session, [])}"
                    )
                    # Use default time instead
                    available_times = self.session_time_slots.get(session, [])
                    time = available_times[0] if available_times else ""
            
            # If no time, use default for session
            if not time:
                available_times = self.session_time_slots.get(session, [])
                time = available_times[0] if available_times else ""
            
            # Initialize enabled status
            enabled = True
            block_reason = None
            
            # Check final_allowed column first (if exists, this is the authoritative filter)
            # CRITICAL: Use current date's row for final_allowed (filters apply to target date, not source date)
            if has_final_allowed and not latest_df.empty:
                # Find the current date's row for this stream
                current_day_row = latest_df[latest_df['Stream'] == stream]
                if not current_day_row.empty:
                    final_allowed = current_day_row.iloc[0].get('final_allowed')
                    # If final_allowed is False/NaN/None, mark as blocked
                    if final_allowed is not True:
                        enabled = False
                        block_reason = f"master_matrix_filtered_{final_allowed}"
            else:
                # No final_allowed column - check filters manually
                if stream_filters:
                    stream_filter = stream_filters.get(stream, {})
                    
                    # Check stream-specific DOW filter
                    if stream_filter.get('exclude_days_of_week'):
                        excluded_dow = stream_filter['exclude_days_of_week']
                        if any(d == target_dow_name or d == str(target_dow) for d in excluded_dow):
                            enabled = False
                            block_reason = f"dow_filter_{target_dow_name.lower()}"
                    
                    # Check stream-specific DOM filter
                    if enabled and stream_filter.get('exclude_days_of_month'):
                        excluded_dom = [int(d) for d in stream_filter['exclude_days_of_month']]
                        if target_dom in excluded_dom:
                            enabled = False
                            block_reason = f"dom_filter_{target_dom}"
                    
                    # Check master filter
                    master_filter = stream_filters.get('master', {})
                    if enabled and master_filter.get('exclude_days_of_week'):
                        excluded_dow = master_filter['exclude_days_of_week']
                        if any(d == target_dow_name or d == str(target_dow) for d in excluded_dow):
                            enabled = False
                            block_reason = f"master_dow_filter_{target_dow_name.lower()}"
                    
                    if enabled and master_filter.get('exclude_days_of_month'):
                        excluded_dom = [int(d) for d in master_filter['exclude_days_of_month']]
                        if target_dom in excluded_dom:
                            enabled = False
                            block_reason = f"master_dom_filter_{target_dom}"
            
            # CRITICAL: Check if the selected time is in exclude_times filter
            # If the time is filtered, block the stream
            if enabled and stream_filters and time:
                try:
                    from modules.matrix.utils import normalize_time
                    normalized_time = normalize_time(str(time))
                    
                    # Check stream-specific exclude_times
                    stream_filter = stream_filters.get(stream, {})
                    if stream_filter.get('exclude_times'):
                        exclude_times_normalized = [normalize_time(str(t)) for t in stream_filter['exclude_times']]
                        if normalized_time in exclude_times_normalized:
                            enabled = False
                            block_reason = f"time_filter({','.join(stream_filter['exclude_times'])})"
                    
                    # Check master exclude_times if stream filter didn't block it
                    if enabled:
                        master_filter = stream_filters.get('master', {})
                        if master_filter.get('exclude_times'):
                            exclude_times_normalized = [normalize_time(str(t)) for t in master_filter['exclude_times']]
                            if normalized_time in exclude_times_normalized:
                                enabled = False
                                block_reason = f"master_time_filter({','.join(master_filter['exclude_times'])})"
                except Exception as e:
                    # If normalization fails, log warning but don't fail
                    logger.warning(f"Failed to check exclude_times for stream {stream} time {time}: {e}")
            
            # Always include stream (enabled or blocked)
            stream_entry = {
                'stream': stream,
                'instrument': instrument,
                'session': session,
                'slot_time': time,
                'decision_time': time,  # Sequencer intent (same as slot_time)
                'enabled': enabled
            }
            if block_reason:
                stream_entry['block_reason'] = block_reason
            streams_dict[stream] = stream_entry
        
        # Second pass: Ensure ALL 14 streams are present (add missing ones as blocked)
        for stream_id in self.streams:
            if stream_id not in streams_dict:
                # Stream not in master matrix - add as blocked
                instrument = stream_id[:-1] if len(stream_id) > 1 else ''
                session = 'S1' if stream_id.endswith('1') else 'S2'
                available_times = self.session_time_slots.get(session, [])
                default_time = ""
                
                # CONTRACT ENFORCEMENT: Select first non-filtered time
                if stream_filters:
                    from modules.matrix.utils import normalize_time
                    exclude_times_list = []
                    
                    # Collect exclude_times from stream and master filters
                    stream_filter = stream_filters.get(stream_id, {})
                    if stream_filter.get('exclude_times'):
                        exclude_times_list.extend(stream_filter['exclude_times'])
                    
                    master_filter = stream_filters.get('master', {})
                    if master_filter.get('exclude_times'):
                        exclude_times_list.extend(master_filter['exclude_times'])
                    
                    # Select first NON-FILTERED time
                    if exclude_times_list:
                        exclude_times_normalized = [normalize_time(str(t)) for t in exclude_times_list]
                        for time_slot in available_times:
                            if normalize_time(time_slot) not in exclude_times_normalized:
                                default_time = time_slot
                                break
                        
                        # CONTRACT ENFORCEMENT: All times filtered → ValueError
                        if not default_time:
                            raise ValueError(
                                f"Stream {stream_id} (session {session}): All available times are filtered. "
                                f"Available times: {available_times}, Filtered times: {exclude_times_list}. "
                                f"This is a configuration error - cannot select default time for missing stream."
                            )
                    else:
                        default_time = available_times[0] if available_times else ""
                else:
                    default_time = available_times[0] if available_times else ""
                
                # CONTRACT ENFORCEMENT: No available time slots → ValueError
                if not default_time:
                    raise ValueError(
                        f"Stream {stream_id} (session {session}): No available time slots. "
                        f"This is a configuration error."
                    )
                
                streams_dict[stream_id] = {
                    'stream': stream_id,
                    'instrument': instrument,
                    'session': session,
                    'slot_time': default_time,
                    'decision_time': default_time,  # Sequencer intent
                    'enabled': False,
                    'block_reason': 'not_in_master_matrix'
                }
        
        # Convert dict to list (all 14 streams guaranteed)
        return list(streams_dict.values())

    def _build_streams_execution_mode(
        self,
        master_matrix_df: pd.DataFrame,
        trade_date_obj: date,
        session_trading_date: str,
        stream_filters: Dict,
    ) -> List[Dict]:
        """
        Live execution: ``slot_time`` from matrix Time / Time Change using ``slot_date_obj`` parsed from
        **session_trading_date** (calendar DOW/DOM from this date only, not wall-clock).

        **Matrix:** ``final_allowed`` / ``filter_reasons`` from **max(trade_date)** row per ``stream_id``.

        **Calendar gate:** ``_execution_session_calendar_filters_eval`` with empty slot time → **dow_pass** and
        **dom_pass** only (exclude_times does not gate ``enabled``).

        **Enablement:** ``has_valid_slot AND latest_final_allowed AND dow_pass AND dom_pass``.

        ``block_reason``: ``no_valid_execution_slot``, ``matrix_filter_blocked:…``, ``calendar_filter_blocked:…``, or ``None``.

        Top-level ``eligibility_trade_date`` remains the latest matrix ``trade_date`` (operators only).
        ``trade_date_obj`` is retained for callers but ignored for slot structure; a warning is logged if
        it differs from ``slot_date_obj``.
        """
        from datetime import timedelta

        raw_session = (session_trading_date or "").strip()
        if not raw_session:
            raise ValueError(
                "_build_streams_execution_mode requires session_trading_date (YYYY-MM-DD from live session)"
            )
        # Compare with UI: full merged input here + on-disk configs/stream_filters.json (source of merge).
        _cfg_path = (Path(self._project_root) / "configs" / "stream_filters.json").resolve()
        _cfg_exists = _cfg_path.is_file()
        if _cfg_exists:
            try:
                _cfg_raw = _cfg_path.read_text(encoding="utf-8")
            except OSError as _e:
                _cfg_raw = f"<read_error {_e}>"
        else:
            _cfg_raw = ""
        try:
            _merged_repr = json.dumps(stream_filters, sort_keys=True, default=str)
        except TypeError:
            _merged_repr = str(stream_filters)
        _cmp1 = (
            f"BACKEND_TIMETABLE_FILTER_COMPARE path={_cfg_path} exists={_cfg_exists} "
            f"merged_stream_filters={_merged_repr}"
        )
        logger.info(_cmp1)
        print(_cmp1, flush=True)
        _cmp2 = f"BACKEND_TIMETABLE_FILTER_COMPARE config_file_contents={_cfg_raw}"
        logger.info(_cmp2)
        print(_cmp2, flush=True)

        slot_date_obj = pd.to_datetime(raw_session).date()
        filter_date_obj = slot_date_obj
        _day_names = (
            "Monday",
            "Tuesday",
            "Wednesday",
            "Thursday",
            "Friday",
            "Saturday",
            "Sunday",
        )
        filter_dow = filter_date_obj.weekday()
        filter_dom = filter_date_obj.day
        filter_dow_name = _day_names[filter_dow]

        if trade_date_obj != slot_date_obj:
            logger.warning(
                "EXECUTION_SLOT_DATE_MISMATCH: trade_date_obj=%s ignored for slots; using slot_date_obj=%s "
                "from session_trading_date",
                trade_date_obj.isoformat(),
                slot_date_obj.isoformat(),
            )

        # Weekend publish audit: unambiguous stdout (logger may not surface in dashboard terminals)
        print(
            "AUDIT_CHECK >>> session:",
            session_trading_date,
            "| filter:",
            filter_dow_name,
            "| filter_dow_idx:",
            filter_dow,
            "| slot_date:",
            slot_date_obj.isoformat(),
            "| legacy_trade_date_param:",
            trade_date_obj.isoformat(),
            flush=True,
        )

        streams_dict = {}
        previous_date_obj = slot_date_obj - timedelta(days=1)
        if not master_matrix_df.empty and 'trade_date' in master_matrix_df.columns:
            previous_df = master_matrix_df[master_matrix_df['trade_date'].dt.date == previous_date_obj].copy()
            latest_df = master_matrix_df[master_matrix_df['trade_date'].dt.date == slot_date_obj].copy()
        else:
            previous_df = pd.DataFrame()
            latest_df = pd.DataFrame()
        source_df = previous_df if not previous_df.empty else latest_df
        use_previous_day_logic = not previous_df.empty and source_df is previous_df

        for stream_id in self.streams:
            instrument = stream_id[:-1] if len(stream_id) > 1 else ''
            session = 'S1' if stream_id.endswith('1') else 'S2'

            # Matrix preference (Time Change > Time); must still be a valid session slot.
            time_from_matrix = ""
            if not source_df.empty:
                row = source_df[source_df['Stream'] == stream_id]
                if not row.empty:
                    r = row.iloc[0]
                    from modules.matrix.utils import normalize_time

                    available_norm = [
                        normalize_time(str(t)) for t in self.session_time_slots.get(session, [])
                    ]

                    def _in_session_slots(hhmm: Optional[str]) -> bool:
                        if not hhmm:
                            return False
                        return normalize_time(str(hhmm)) in available_norm

                    time_raw = _execution_mode_row_get_time(r)
                    time_from_matrix = ""

                    if use_previous_day_logic:
                        # Previous day's row: Time Change (next session slot) wins over Time when valid.
                        tc_raw = _execution_mode_row_get_time_change(r)
                        original_display = _coerce_matrix_cell_to_slot_hhmm(time_raw) or ""

                        if _execution_mode_matrix_cell_is_nonempty(tc_raw):
                            tc_parsed = _coerce_matrix_cell_to_slot_hhmm(tc_raw)
                            if tc_parsed and _in_session_slots(tc_parsed):
                                time_from_matrix = tc_parsed
                                logger.info(
                                    "TIME_CHANGE_APPLIED stream=%s original_time=%s new_time=%s",
                                    stream_id,
                                    original_display if original_display else "(none)",
                                    tc_parsed,
                                )
                            else:
                                logger.warning(
                                    "TIME_CHANGE_INVALID stream=%s session=%s raw=%r coerced=%r "
                                    "allowed=%s",
                                    stream_id,
                                    session,
                                    tc_raw,
                                    tc_parsed,
                                    available_norm,
                                )

                        if not time_from_matrix:
                            t_parsed = _coerce_matrix_cell_to_slot_hhmm(time_raw)
                            if t_parsed and _in_session_slots(t_parsed):
                                time_from_matrix = t_parsed
                    else:
                        t_parsed = _coerce_matrix_cell_to_slot_hhmm(time_raw)
                        if t_parsed and _in_session_slots(t_parsed):
                            time_from_matrix = t_parsed

            from modules.matrix.utils import normalize_time as _norm_slot

            session_slots = self.session_time_slots.get(session, [])
            full_order_norm, norm_to_display = _execution_slot_order_and_display_map(session_slots)
            instrument_slots_norm = _execution_instrument_slots_ordered(
                session, instrument, self.session_time_slots
            )
            # Slot picking follows matrix Time + session instrument allow-list only; exclude_times does not remap slot.
            candidates_norm = list(instrument_slots_norm)
            matrix_pref_norm = _norm_slot(str(time_from_matrix)) if time_from_matrix else ""

            picked_norm: Optional[str] = None

            if not matrix_pref_norm:
                picked_norm = candidates_norm[0] if candidates_norm else None
            elif matrix_pref_norm not in full_order_norm:
                picked_norm = candidates_norm[0] if candidates_norm else None
            elif matrix_pref_norm not in candidates_norm:
                picked_norm = _pick_slot_from_preference(
                    full_order_norm, candidates_norm, matrix_pref_norm
                )
            else:
                picked_norm = matrix_pref_norm

            if picked_norm is None:
                time = ""
                logger.error(
                    "NO_VALID_EXECUTION_SLOTS stream=%s session=%s instrument=%s "
                    "matrix_time=%s candidates=%s",
                    stream_id,
                    session,
                    instrument,
                    time_from_matrix or "(empty)",
                    candidates_norm,
                )
                has_valid_slot = False
            else:
                time = norm_to_display[picked_norm]
                if matrix_pref_norm and picked_norm != matrix_pref_norm:
                    logger.info(
                        "EXECUTION_INSTRUMENT_SLOT_REMAP stream=%s matrix_time=%s published_slot=%s",
                        stream_id,
                        matrix_pref_norm,
                        picked_norm,
                    )
                has_valid_slot = True

            matrix_ok, matrix_filter_reasons = _matrix_latest_row_final_allowed_for_stream(
                master_matrix_df, stream_id
            )

            _ev_calendar_only = _execution_session_calendar_filters_eval(
                stream_id,
                stream_filters,
                filter_dow_name,
                filter_dow,
                filter_dom,
                "",
            )
            dow_pass = bool(_ev_calendar_only["dow_pass"])
            dom_pass = bool(_ev_calendar_only["dom_pass"])
            calendar_ok = dow_pass and dom_pass

            passes_session_filters, _ = _execution_session_calendar_filters_pass(
                stream_id,
                stream_filters,
                filter_dow_name,
                filter_dow,
                filter_dom,
                time if has_valid_slot else "",
            )

            enabled = bool(has_valid_slot and matrix_ok and calendar_ok)
            _cal_block = f"calendar_filter_blocked:{filter_dow_name}:{filter_dom}"

            if not has_valid_slot:
                block_reason = "no_valid_execution_slot"
            elif not matrix_ok:
                block_reason = "matrix_filter_blocked:" + matrix_filter_reasons
            elif not calendar_ok:
                block_reason = _cal_block
            else:
                block_reason = None

            print(
                f"STREAM_AUDIT >>> stream={stream_id} | slot_date={slot_date_obj} | "
                f"has_valid_slot={has_valid_slot} | matrix_final_allowed={matrix_ok} | "
                f"dow_pass={dow_pass} dom_pass={dom_pass} | "
                f"filter_eval_overall_pass={passes_session_filters} (incl_time_diagnostic) | enabled={enabled}",
                flush=True,
            )

            entry: Dict[str, Any] = {
                'stream': stream_id,
                'slot_time': time,
                'enabled': enabled,
            }
            if block_reason is not None:
                entry['block_reason'] = block_reason
            streams_dict[stream_id] = entry

        # One combined report: latest-matrix outcome + calendar eval + merged filter snapshot.
        _pr_streams: List[Dict[str, Any]] = []
        for _sid in self.streams:
            _rec = streams_dict[_sid]
            _slot = _rec.get("slot_time") or ""
            _ev = _execution_session_calendar_filters_eval(
                _sid,
                stream_filters,
                filter_dow_name,
                filter_dow,
                filter_dom,
                _slot,
            )
            _m_ok, _m_fr = _matrix_latest_row_final_allowed_for_stream(
                master_matrix_df, _sid
            )
            _pr_streams.append(
                {
                    "stream": _sid,
                    "slot_time": _slot,
                    "matrix_final_allowed": _m_ok,
                    "matrix_filter_reasons": _m_fr,
                    "filter_config": _ev["filter_config"],
                    "dow_pass": _ev["dow_pass"],
                    "time_pass": _ev["time_pass"],
                    "overall_pass": _ev["overall_pass"],
                    "session_block_reason_if_filters": _ev["session_block_reason"],
                    "dom_pass": _ev["dom_pass"],
                    "filter_eval_detail": _ev["detail"],
                    "enabled": _rec["enabled"],
                    "block_reason": _rec.get("block_reason"),
                }
            )
        _pr_streams.sort(
            key=lambda s: _parse_slot_time(s.get("slot_time")),
            reverse=True,
        )
        _publish_report: Dict[str, Any] = {
            "merged_stream_filters": stream_filters,
            "session_trading_date": session_trading_date,
            "filter_calendar": {
                "dow_name": filter_dow_name,
                "dow_mon0_sun6": filter_dow,
                "day_of_month": filter_dom,
            },
            "streams": _pr_streams,
        }
        try:
            _report_line = "EXECUTION_FILTER_PUBLISH_REPORT " + json.dumps(
                _publish_report, sort_keys=True, default=str
            )
        except TypeError:
            _report_line = f"EXECUTION_FILTER_PUBLISH_REPORT {_publish_report!r}"
        logger.info(_report_line)
        print(_report_line, flush=True)

        _log_cursor_execution_filter_audit(
            self._project_root,
            stream_filters,
            session_trading_date,
            filter_dow_name,
            filter_dow,
            filter_dom,
            self.streams,
            streams_dict,
        )

        streams_out: List[Dict[str, Any]] = list(streams_dict.values())
        streams_out.sort(
            key=lambda s: _parse_slot_time(s.get("slot_time")),
            reverse=True,
        )
        return streams_out

    def _build_all_disabled_streams(
        self, trade_date_obj: date, block_reason: str
    ) -> List[Dict]:
        """Build streams list with all streams disabled (e.g. empty matrix in execution path)."""
        streams = []
        for stream_id in self.streams:
            instrument = stream_id[:-1] if len(stream_id) > 1 else ''
            session = 'S1' if stream_id.endswith('1') else 'S2'
            available_times = self.session_time_slots.get(session, [])
            default_time = available_times[0] if available_times else ""
            br = block_reason
            if br == "MATRIX_DATE_MISSING":
                br = "no_valid_execution_slot"
            streams.append({
                'stream': stream_id,
                'slot_time': default_time,
                'enabled': False,
                'block_reason': br,
            })
        return streams

    def _publish_live_execution_timetable_versioned(
        self,
        streams: List[Dict],
        session_trading_date: str,
        *,
        execution_document_source: str = "master_matrix",
        ledger_writer: str = "timetable_engine",
        ledger_source: Optional[str] = None,
        enforce_cme_live: bool = False,
        publish_context: Optional[Dict[str, Any]] = None,
        eligibility_trade_date: Optional[str] = None,
    ) -> TimetablePublishResult:
        """Write data/timetable/timetable_current.json with history/, skip if content unchanged."""
        from modules.timetable.timetable_write_guard import validate_streams_before_execution_write
        from modules.timetable.timetable_content_hash import (
            compute_timetable_hash_sorted,
            timetable_hash_from_document,
        )

        ctx = publish_context or {}
        doc_source = (ctx.get("source") or execution_document_source or "master_matrix").strip()
        reason = (ctx.get("reason") or "publish").strip()

        streams_copy: List[Dict[str, Any]] = [dict(s) for s in streams]

        validate_streams_before_execution_write(streams_copy, session_time_slots=self.session_time_slots)

        tz_label = "America/Chicago"
        timetable_hash = compute_timetable_hash_sorted(session_trading_date, tz_label, streams_copy)

        timetable_dir = self._project_root / "data" / "timetable"
        final_file = timetable_dir / "timetable_current.json"
        previous_hash: Optional[str] = None
        if final_file.exists():
            try:
                with open(final_file, "r", encoding="utf-8") as rf:
                    existing_doc = json.load(rf)
                previous_hash = existing_doc.get("timetable_hash")
                if not previous_hash and isinstance(existing_doc.get("streams"), list):
                    previous_hash = timetable_hash_from_document(existing_doc)
            except Exception as ex:
                logger.warning("TIMETABLE_PREVIOUS_READ: %s", ex)

        if previous_hash is not None and timetable_hash == previous_hash:
            logger.info(
                "TIMETABLE_WRITE_SKIPPED_NO_CHANGE trading_date=%s timetable_hash=%s previous_hash=%s source=%s reason=%s",
                session_trading_date,
                timetable_hash,
                previous_hash,
                doc_source,
                reason,
            )
            return TimetablePublishResult(
                changed=False,
                skipped_no_change=True,
                timetable_hash=timetable_hash,
                previous_hash=previous_hash,
            )

        history_dir = timetable_dir / "history"
        history_dir.mkdir(parents=True, exist_ok=True)
        utc_now = datetime.now(timezone.utc)
        version_ts = utc_now.isoformat().replace("+00:00", "Z")
        version_tag = utc_now.strftime("%Y%m%dT%H%M%SZ")

        chicago_tz = pytz.timezone(tz_label)
        chicago_now = datetime.now(chicago_tz)
        as_of = chicago_now.isoformat()

        execution_base = {
            "as_of": as_of,
            "session_trading_date": session_trading_date,
            "timezone": tz_label,
            "source": doc_source,
            "streams": streams_copy,
        }
        if eligibility_trade_date:
            execution_base["eligibility_trade_date"] = str(eligibility_trade_date).strip()
        full_doc = {
            **execution_base,
            "timetable_hash": timetable_hash,
            "previous_hash": previous_hash or "",
            "version_timestamp": version_ts,
        }
        meta = {
            "timestamp_utc": version_ts,
            "trading_date": session_trading_date,
            "hash": timetable_hash,
            "source": doc_source,
            "reason": reason,
            "previous_hash": previous_hash or "",
        }
        hist_name = f"{version_tag}_{session_trading_date}.json"
        hist_path = history_dir / hist_name
        try:
            with open(hist_path, "w", encoding="utf-8") as hf:
                json.dump(
                    {"timetable": full_doc, "metadata": meta},
                    hf,
                    indent=2,
                    ensure_ascii=False,
                )
        except Exception as ex:
            logger.error("TIMETABLE_HISTORY_WRITE_FAILED: %s", ex)
            raise

        extra_write: Dict[str, Any] = {
            "timetable_hash": timetable_hash,
            "previous_hash": previous_hash or "",
            "version_timestamp": version_ts,
        }
        self._write_execution_timetable_file(
            streams_copy,
            session_trading_date,
            ledger_writer=ledger_writer,
            ledger_source=ledger_source or doc_source,
            execution_document_source=doc_source,
            enforce_cme_live=enforce_cme_live,
            eligibility_trade_date=eligibility_trade_date,
            extra_document_fields=extra_write,
        )

        logger.info(
            "TIMETABLE_PUBLISHED trading_date=%s timetable_hash=%s previous_hash=%s source=%s reason=%s",
            session_trading_date,
            timetable_hash,
            previous_hash or "",
            doc_source,
            reason,
        )
        return TimetablePublishResult(
            changed=True,
            skipped_no_change=False,
            timetable_hash=timetable_hash,
            previous_hash=previous_hash,
        )

    def _write_execution_timetable_file(
        self,
        streams: List[Dict],
        session_trading_date: str,
        *,
        execution_document_source: str = "master_matrix",
        ledger_writer: str = "timetable_engine",
        ledger_source: Optional[str] = None,
        enforce_cme_live: bool = False,
        eligibility_trade_date: Optional[str] = None,
        extra_document_fields: Optional[Dict[str, Any]] = None,
    ) -> None:
        """
        Write execution timetable JSON.

        Preconditions:
          - session_trading_date: explicit YYYY-MM-DD (authoritative for this document)
        When enforce_cme_live is True (live timetable_current): session_trading_date must equal
        get_cme_trading_date(datetime.now(timezone.utc)).
        """
        if not (session_trading_date or "").strip():
            raise ValueError("session_trading_date is required")
        session_trading_date = str(session_trading_date).strip()

        output_dir = Path(self.timetable_output_dir) if self.timetable_output_dir else Path("data/timetable")
        filename_base = "timetable_copy" if self.timetable_output_dir else "timetable_current"
        output_dir.mkdir(parents=True, exist_ok=True)

        utc_now = datetime.now(timezone.utc)
        expected_cme = get_cme_trading_date(utc_now)
        if enforce_cme_live and filename_base == "timetable_current":
            if session_trading_date != expected_cme:
                logger.error(
                    "TIMETABLE_WRITE_BLOCKED_CME_MISMATCH: session_trading_date=%s expected_cme=%s",
                    session_trading_date,
                    expected_cme,
                )
                raise TimetableWriteBlockedCmeMismatch(
                    f"live execution session_trading_date must be {expected_cme}, got {session_trading_date}"
                )
            # Defensive invariant (upstream already enforced); fails if logic regresses (e.g. under python -O assert is stripped).
            assert session_trading_date == expected_cme, (
                "live timetable invariant: session_trading_date must equal get_cme_trading_date(now)"
            )

        self._cleanup_old_timetable_files(output_dir, keep_filename=filename_base)

        from modules.timetable.timetable_write_guard import validate_streams_before_execution_write

        validate_streams_before_execution_write(streams, session_time_slots=self.session_time_slots)

        chicago_tz = pytz.timezone("America/Chicago")
        chicago_now = datetime.now(chicago_tz)
        as_of = chicago_now.isoformat()

        execution_timetable: Dict[str, Any] = {
            "as_of": as_of,
            "session_trading_date": session_trading_date,
            "timezone": "America/Chicago",
            "source": execution_document_source,
            "streams": streams,
        }
        if eligibility_trade_date:
            execution_timetable["eligibility_trade_date"] = str(eligibility_trade_date).strip()
        if extra_document_fields:
            execution_timetable.update(extra_document_fields)

        temp_file = output_dir / f"{filename_base}.tmp"
        final_file = output_dir / f"{filename_base}.json"

        try:
            with open(temp_file, "w", encoding="utf-8") as f:
                json.dump(execution_timetable, f, indent=2, ensure_ascii=False)

            temp_file.replace(final_file)

            logger.info("Execution timetable written: %s (%s streams)", final_file, len(streams))

            if filename_base == "timetable_current":
                try:
                    import uuid
                    from modules.timetable.timetable_content_hash import compute_content_hash_from_document
                    from modules.timetable.timetable_publish_ledger import append_timetable_publish_ledger

                    with open(final_file, "r", encoding="utf-8") as rf:
                        written_doc = json.load(rf)
                    content_hash = compute_content_hash_from_document(written_doc)
                    run_id = uuid.uuid4().hex[:12]
                    append_timetable_publish_ledger(
                        self._project_root,
                        {
                            "event": "TIMETABLE_PUBLISHED_WITH_CONTEXT",
                            "timestamp": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
                            "hash": content_hash,
                            "writer": ledger_writer,
                            "source": ledger_source or execution_document_source,
                            "streams_count": len(streams),
                            "path": str(final_file.resolve()),
                            "matrix_trade_date": session_trading_date,
                            "session_trading_date": session_trading_date,
                            "run_id": run_id,
                        },
                    )
                    logger.info(
                        "TRADING_DATE_ROLLED: ts_utc=%s session_trading_date=%s source=timetable_engine run_id=%s",
                        datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
                        session_trading_date,
                        run_id,
                    )
                except Exception as e:
                    logger.warning("TIMETABLE_PUBLISH_LEDGER_SKIP: %s", e)
        except Exception as e:
            logger.error("Failed to write execution timetable: %s", e)
            if temp_file.exists():
                try:
                    temp_file.unlink()
                except Exception:
                    pass
            raise
    
    def write_execution_timetable(self, timetable_df: pd.DataFrame, trade_date: str) -> None:
        """
        Write execution timetable JSON (timetable_copy.json only).

        Live ``timetable_current.json`` is restricted to
        ``write_execution_timetable_from_master_matrix(..., execution_mode=True)`` — not the analyzer dataframe path.
        """
        if not self.timetable_output_dir:
            raise TimetableLivePublishBlocked(
                "write_execution_timetable requires TimetableEngine(..., timetable_output_dir=...) "
                "to write timetable_copy.json under a scratch directory. "
                "Analyzer output must not write live timetable_current.json."
            )
        output_dir = Path(self.timetable_output_dir)
        filename_base = "timetable_copy"
        output_dir.mkdir(parents=True, exist_ok=True)
        
        # Clean up old files (keep only the canonical file for this output dir)
        self._cleanup_old_timetable_files(output_dir, keep_filename=filename_base)
        
        # Build streams array - include ALL streams (enabled and blocked)
        # Each stream_id maps to one session: ES1->S1, ES2->S2, etc.
        # CRITICAL: Timetable must contain complete execution contract - all 14 streams
        streams = []
        
        # Create a lookup dict from timetable_df: stream_id -> (session, slot_time, enabled, block_reason)
        # Use itertuples (faster than iterrows) for small timetable
        all_streams = {}
        for row in timetable_df.itertuples(index=False):
            stream_id = getattr(row, 'stream_id', None)
            session = getattr(row, 'session', None)
            # Only store if this stream_id matches its natural session
            # ES1 should only have S1 entries, ES2 should only have S2 entries
            expected_session = "S1" if stream_id.endswith("1") else "S2"
            if session == expected_session:
                all_streams[stream_id] = {
                    'session': session,
                    'slot_time': getattr(row, 'selected_time', ''),
                    'enabled': getattr(row, 'allowed', False),
                    'block_reason': getattr(row, 'block_reason', None)
                }
        
        # Include ALL streams - enabled and blocked (complete execution contract)
        for stream_id, stream_data in all_streams.items():
            stream_entry: Dict[str, Any] = {
                'stream': stream_id,
                'slot_time': stream_data['slot_time'],
                'enabled': stream_data['enabled'],
            }
            if stream_data.get('block_reason'):
                stream_entry['block_reason'] = stream_data['block_reason']
            streams.append(stream_entry)
        
        # Write execution timetable file using shared method
        self._write_execution_timetable_file(
            streams,
            trade_date,
            ledger_writer="timetable_engine",
            ledger_source="generate_timetable",
            execution_document_source="master_matrix",
            enforce_cme_live=False,
        )
    
    def _cleanup_old_timetable_files(self, output_dir: Path, keep_filename: str = "timetable_current") -> None:
        """
        Remove all files in timetable directory except the canonical file and its temp.
        
        Args:
            output_dir: Timetable output directory
            keep_filename: Base filename to keep (e.g. timetable_current or timetable_copy)
        """
        if not output_dir.exists():
            return
        
        removed_count = 0
        for file_path in output_dir.iterdir():
            # Skip the canonical file and its temp file
            if file_path.name == f"{keep_filename}.json":
                continue
            if file_path.name == f"{keep_filename}.tmp":
                continue
            # Never delete eligibility files (immutable per trading_date; robot fails closed without them)
            if file_path.name.startswith("eligibility_"):
                continue
            # Version history (timetable snapshots)
            if file_path.name == "history" and file_path.is_dir():
                continue
            
            try:
                if file_path.is_dir():
                    continue
                file_path.unlink()
                removed_count += 1
            except Exception as e:
                logger.warning(f"Failed to remove old file {file_path}: {e}")
        
        if removed_count > 0:
            logger.info(f"Cleaned up {removed_count} old timetable files")


def main():
    """Main function for command-line usage"""
    import argparse
    
    parser = argparse.ArgumentParser(description='Generate Timetable for trading day')
    parser.add_argument('--date', type=str, help='Trading date (YYYY-MM-DD) or today if not specified')
    parser.add_argument('--master-matrix-dir', type=str, default='data/master_matrix',
                       help='Directory containing master matrix files')
    parser.add_argument('--analyzer-runs-dir', type=str, default='data/analyzed',
                       help='Directory containing analyzer output files')
    parser.add_argument('--output-dir', type=str, default='data/timetable',
                       help='Output directory for timetable files')
    parser.add_argument('--scf-threshold', type=float, default=0.5,
                       help='SCF threshold for blocking trades')
    
    args = parser.parse_args()
    
    engine = TimetableEngine(
        master_matrix_dir=args.master_matrix_dir,
        analyzer_runs_dir=args.analyzer_runs_dir
    )
    engine.scf_threshold = args.scf_threshold
    
    timetable_df = engine.generate_timetable(trade_date=args.date)
    
    if not timetable_df.empty:
        parquet_file, json_file = engine.save_timetable(timetable_df, args.output_dir)
        
        print("\n" + "=" * 80)
        print("TIMETABLE SUMMARY")
        print("=" * 80)
        print(f"Date: {timetable_df['trade_date'].iloc[0]}")
        print(f"Total entries: {len(timetable_df)}")
        print(f"Allowed trades: {timetable_df['allowed'].sum()}")
        print(f"\nTimetable:")
        print(timetable_df[['symbol', 'stream_id', 'session', 'selected_time', 
                           'allowed', 'reason']].to_string(index=False))
        print(f"\nFiles saved:")
        print(f"  - {parquet_file}")
        print(f"  - {json_file}")


if __name__ == "__main__":
    main()



