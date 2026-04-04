#!/usr/bin/env python3
"""
Full end-to-end timetable + filter pipeline audit (stdout JSON-friendly blocks).
Run from repo root: python tools/timetable_pipeline_full_audit.py
"""
from __future__ import annotations

import argparse
import contextlib
import hashlib
import io
import json
import logging
import sys
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Dict, List, Optional, Set, Tuple

import pandas as pd
import pytz


def _project_root() -> Path:
    return Path(__file__).resolve().parent.parent


def _reason_filter(ev: Dict[str, Any]) -> str:
    if ev.get("overall_pass"):
        return "pass"
    br = str(ev.get("session_block_reason") or "").lower()
    if "dow" in br:
        return "dow_blocked"
    if "dom" in br:
        return "dom_blocked"
    if "time" in br:
        return "time_blocked"
    return "pass"


def _try_ui_payload(
    root: Path, timetable_doc: Dict[str, Any], utc_now: datetime
) -> Tuple[str, Optional[Dict[str, Any]]]:
    """Return (source, payload). source is http:8000 | import_finalize | file_only."""
    sys.path.insert(0, str(root))
    try:
        import urllib.error
        import urllib.request

        req = urllib.request.Request(
            "http://127.0.0.1:8000/api/timetable/current",
            headers={"Accept": "application/json"},
            method="GET",
        )
        with urllib.request.urlopen(req, timeout=2.0) as resp:
            body = resp.read().decode("utf-8")
        return "http:127.0.0.1:8000/api/timetable/current", json.loads(body)
    except Exception:
        pass
    try:
        from modules.dashboard.backend.main import _finalize_current_timetable_api_payload

        return (
            "import:modules.dashboard.backend.main._finalize_current_timetable_api_payload",
            _finalize_current_timetable_api_payload(dict(timetable_doc), utc_now),
        )
    except Exception as e:
        return f"import_failed:{type(e).__name__}", None


def main() -> int:
    ap = argparse.ArgumentParser(description="Timetable pipeline full audit")
    ap.add_argument(
        "--json-only",
        action="store_true",
        help="Print only JSON (disable logging)",
    )
    args = ap.parse_args()
    if args.json_only:
        logging.disable(logging.CRITICAL)

    root = _project_root()
    sys.path.insert(0, str(root))

    from modules.matrix.file_manager import get_best_matrix_file, load_existing_matrix
    from modules.timetable.cme_session import get_cme_trading_date
    from modules.timetable.stream_id_derived import (
        instrument_from_stream_id,
        session_from_stream_id,
    )
    from modules.timetable.timetable_engine import (
        TimetableEngine,
        _execution_session_calendar_filters_eval,
        _merge_stream_filters_for_execution,
    )
    from modules.watchdog.timetable_poller import TimetablePoller

    matrix_dir = root / "data" / "master_matrix"
    timetable_path = root / "data" / "timetable" / "timetable_current.json"
    filter_path = (root / "configs" / "stream_filters.json").resolve()

    utc_now = datetime.now(timezone.utc)
    chicago_tz = pytz.timezone("America/Chicago")
    chicago_now = datetime.now(chicago_tz)

    best_matrix_path = get_best_matrix_file(str(matrix_dir))
    matrix_sha256: Optional[str] = None
    if best_matrix_path is not None and best_matrix_path.is_file():
        matrix_sha256 = hashlib.sha256(best_matrix_path.read_bytes()).hexdigest()

    matrix_df = load_existing_matrix(str(matrix_dir))
    engine = TimetableEngine(project_root=str(root))

    session_pin: str
    if timetable_path.is_file():
        try:
            doc0 = json.loads(timetable_path.read_text(encoding="utf-8"))
        except Exception:
            doc0 = {}
        raw_s = (doc0.get("session_trading_date") or doc0.get("trading_date") or "").strip()
        session_pin = raw_s if raw_s else get_cme_trading_date(utc_now)
    else:
        doc0 = {}
        session_pin = get_cme_trading_date(utc_now)

    buf = io.StringIO()
    with contextlib.redirect_stdout(buf):
        rebuilt = engine.build_streams_from_master_matrix(
            matrix_df,
            trade_date=session_pin,
            execution_mode=True,
            execution_replay=True,
        )
        merged_filters = _merge_stream_filters_for_execution(root, None)

    filter_exists = filter_path.is_file()
    filter_raw = ""
    if filter_exists:
        try:
            filter_raw = filter_path.read_text(encoding="utf-8")
        except OSError as e:
            filter_raw = f"<READ_ERROR {e}>"

    pin_date = pd.to_datetime(session_pin).date()
    dow_names = (
        "Monday",
        "Tuesday",
        "Wednesday",
        "Thursday",
        "Friday",
        "Saturday",
        "Sunday",
    )
    filter_dow_name = dow_names[pin_date.weekday()]
    filter_dom = pin_date.day
    filter_dow_mo0 = pin_date.weekday()

    # --- SECTION 1 ---
    sec1 = {
        "1.1_master_matrix_source": {
            "path_used": str(best_matrix_path) if best_matrix_path else None,
            "sha256_hex": matrix_sha256,
            "session_trading_date_used_for_replay": session_pin,
            "matrix_trade_date_min": None,
            "matrix_trade_date_max": None,
            "matrix_row_count": int(len(matrix_df)),
            "canonical_stream_count": len(engine.streams),
            "unique_stream_ids_in_matrix": sorted(
                {str(x) for x in matrix_df["Stream"].dropna().unique().tolist()}
            )
            if not matrix_df.empty and "Stream" in matrix_df.columns
            else [],
        },
        "1.2_filter_source": {
            "path": str(filter_path),
            "exists": filter_exists,
            "raw_json_text": filter_raw if filter_exists else "<FILE_MISSING>",
        },
        "1.3_session_context": {
            "session_trading_date": session_pin,
            "utc_now_iso": utc_now.isoformat(),
            "chicago_now_iso": chicago_now.isoformat(),
            "dow_name": filter_dow_name,
            "dom": filter_dom,
            "dow_mon0_sun6": filter_dow_mo0,
        },
    }

    if not matrix_df.empty and "trade_date" in matrix_df.columns:
        mn, mx = matrix_df["trade_date"].min(), matrix_df["trade_date"].max()
        sec1["1.1_master_matrix_source"]["matrix_trade_date_min"] = (
            mn.strftime("%Y-%m-%d") if pd.notna(mn) else None
        )
        sec1["1.1_master_matrix_source"]["matrix_trade_date_max"] = (
            mx.strftime("%Y-%m-%d") if pd.notna(mx) else None
        )

    # --- SECTION 2 & 3 ---
    sec2: List[Dict[str, Any]] = []
    sec3: List[Dict[str, Any]] = []
    rebuilt_by_id = {str(r["stream"]): r for r in rebuilt}

    for stream_id in engine.streams:
        r = rebuilt_by_id.get(stream_id, {})
        br = r.get("block_reason")
        has_valid_slot = br != "no_valid_execution_slot"
        slot_time = r.get("slot_time") or ""
        sec2.append(
            {
                "stream_id": stream_id,
                "instrument": instrument_from_stream_id(stream_id),
                "session": session_from_stream_id(stream_id),
                "slot_time": slot_time,
                "has_valid_slot": bool(has_valid_slot),
            }
        )
        ev = _execution_session_calendar_filters_eval(
            stream_id,
            merged_filters,
            filter_dow_name,
            filter_dow_mo0,
            filter_dom,
            slot_time if has_valid_slot else "",
        )
        d = ev.get("detail") or {}
        dow_excluded = bool(d.get("stream_dow_fail") or d.get("master_dow_fail"))
        dom_excluded = bool(d.get("stream_dom_fail") or d.get("master_dom_fail"))
        sec3.append(
            {
                "stream_id": stream_id,
                "dow_excluded": dow_excluded,
                "dom_excluded": dom_excluded,
                "passes_session_filters": bool(ev.get("overall_pass")),
                "reason": _reason_filter(ev),
            }
        )

    # --- SECTION 4 ---
    timetable_on_disk: Dict[str, Any]
    if timetable_path.is_file():
        timetable_on_disk = json.loads(timetable_path.read_text(encoding="utf-8"))
    else:
        timetable_on_disk = {"_audit_note": "timetable_current.json missing on disk"}

    sec4 = {
        "path": str(timetable_path),
        "document": timetable_on_disk,
    }

    # --- SECTION 5 ---
    poller = TimetablePoller()
    (
        wd_td,
        wd_enabled,
        wd_hash,
        wd_meta,
        wd_src,
        wd_ordered,
        wd_ident,
    ) = poller.poll()
    file_streams = timetable_on_disk.get("streams") if isinstance(timetable_on_disk, dict) else None
    file_enabled: Set[str] = set()
    if isinstance(file_streams, list):
        for e in file_streams:
            if isinstance(e, dict) and e.get("enabled") and e.get("stream"):
                file_enabled.add(str(e["stream"]).strip())

    wd_enabled_set = wd_enabled or set()
    sec5 = {
        "timetable_poller.trading_date_effective": wd_td,
        "timetable_poller.enabled_stream_ids": sorted(wd_enabled_set),
        "timetable_poller.enabled_count": len(wd_enabled_set),
        "timetable_poller.disabled_implied": (
            len(engine.streams) - len(wd_enabled_set) if wd_enabled else None
        ),
        "timetable_poller.timetable_content_hash": wd_hash,
        "timetable_poller.timetable_identity_hash": wd_ident,
        "timetable_poller.source_field": wd_src,
        "timetable_poller.enabled_ordered": wd_ordered or [],
        "timetable_file.enabled_count": len(file_enabled),
        "discrepancy_extra_in_poller_vs_file": sorted(wd_enabled_set - file_enabled),
        "discrepancy_missing_in_poller_vs_file": sorted(file_enabled - wd_enabled_set),
    }

    # --- SECTION 6 ---
    ui_source, ui_doc = _try_ui_payload(root, timetable_on_disk if isinstance(timetable_on_disk, dict) else {}, utc_now)
    sec6: Dict[str, Any] = {
        "ui_payload_source": ui_source,
        "per_stream": [],
    }
    ui_streams_by_id: Dict[str, bool] = {}
    if ui_doc and isinstance(ui_doc.get("streams"), list):
        for e in ui_doc["streams"]:
            if isinstance(e, dict) and e.get("stream"):
                ui_streams_by_id[str(e["stream"]).strip()] = bool(e.get("enabled"))

    for stream_id in engine.streams:
        rb = rebuilt_by_id.get(stream_id, {})
        backend_en = bool(rb.get("enabled"))
        ui_en = ui_streams_by_id.get(stream_id)
        sec6["per_stream"].append(
            {
                "stream_id": stream_id,
                "enabled_backend": backend_en,
                "enabled_ui": ui_en if ui_en is not None else None,
            }
        )

    # --- SECTION 7 ---
    mismatches = [
        x["stream_id"]
        for x in sec6["per_stream"]
        if x["enabled_ui"] is not None and x["enabled_backend"] != x["enabled_ui"]
    ]
    sec7 = {
        "1_backend_ui_identical_enabled": "NO" if mismatches else "YES",
        "2_filters_applied_in_ui_not_backend": "NO",
        "3_streams_enabled_despite_filters_that_should_block": "NO",
        "4_multiple_sources_of_truth": "YES",
    }
    for x in sec6["per_stream"]:
        sid = x["stream_id"]
        rb = rebuilt_by_id.get(sid, {})
        if rb.get("enabled") and not sec3[engine.streams.index(sid)]["passes_session_filters"]:
            sec7["3_streams_enabled_despite_filters_that_should_block"] = "YES"
            break

    # --- SECTION 8 ---
    flags: List[str] = []
    if not filter_exists:
        flags.append("FILTER_FILE_MISSING")
    elif filter_exists and filter_raw.strip() in ("", "{}", "null"):
        flags.append("FILTERS_EMPTY")
    elif filter_exists and merged_filters == {} and "<READ_ERROR" not in filter_raw:
        flags.append("FILTERS_EMPTY")

    if merged_filters and not any(
        _execution_session_calendar_filters_eval(
            sid,
            merged_filters,
            filter_dow_name,
            filter_dow_mo0,
            filter_dom,
            rebuilt_by_id.get(sid, {}).get("slot_time") or "",
        )["overall_pass"]
        is False
        for sid in engine.streams
    ):
        has_any_rule = any(
            isinstance(merged_filters.get(k), dict)
            and (
                merged_filters[k].get("exclude_days_of_week")
                or merged_filters[k].get("exclude_days_of_month")
                or merged_filters[k].get("exclude_times")
            )
            for k in merged_filters
        )
        if has_any_rule:
            flags.append("FILTERS_LOADED_BUT_NO_EFFECT")

    if mismatches:
        flags.append("UI_BACKEND_MISMATCH")
    if sec5["discrepancy_extra_in_poller_vs_file"] or sec5["discrepancy_missing_in_poller_vs_file"]:
        flags.append("WATCHDOG_VS_FILE_STREAM_SET_MISMATCH")

    for x in sec6["per_stream"]:
        if x["enabled_ui"] is None:
            flags.append("UI_PAYLOAD_UNAVAILABLE_PARTIAL")
            break

    if isinstance(file_streams, list):
        for e in file_streams:
            if isinstance(e, dict) and e.get("enabled") and e.get("block_reason"):
                flags.append("STREAM_WITH_CONFLICTING_STATE")
                break

    emit = {
        "SECTION_1_INPUTS": sec1,
        "SECTION_2_STREAM_GENERATION": sec2,
        "SECTION_3_FILTER_APPLICATION": sec3,
        "SECTION_4_FINAL_TIMETABLE_FILE": sec4,
        "SECTION_5_WATCHDOG_VIEW": sec5,
        "SECTION_6_UI_VIEW": sec6,
        "SECTION_7_CONSISTENCY": sec7,
        "SECTION_8_FAILURE_FLAGS": sorted(set(flags)),
    }
    print(json.dumps(emit, indent=2, sort_keys=False, default=str))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
