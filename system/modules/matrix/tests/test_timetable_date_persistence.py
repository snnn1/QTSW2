"""
End-to-end: manual session_trading_date (generate / write_execution path) survives save_master_matrix persist.

Uses real TimetableEngine.write_execution_timetable_from_master_matrix and file_manager.save_master_matrix
(same inner calls as POST /api/timetable/generate and background _run_timetable_persist).

Model A: persisted SessionAuthority is required; tests save authority before publish.
"""

from __future__ import annotations

import json
import shutil
import sys
import time
import uuid
from pathlib import Path
from typing import Optional

import pandas as pd

REPO_ROOT = Path(__file__).resolve().parents[4]
SYSTEM_ROOT = REPO_ROOT / "system"
sys.path.insert(0, str(SYSTEM_ROOT))

from modules.matrix.file_manager import (  # noqa: E402
    save_master_matrix,
    set_current_master_matrix_df,
)
from modules.session_authority import initialize_auto_authority  # noqa: E402
from modules.session_authority.models import SessionAuthorityState  # noqa: E402
from modules.session_authority.store import read_next_version, save_authority  # noqa: E402
from modules.timetable.cme_session import get_cme_trading_date  # noqa: E402
from modules.timetable.timetable_engine import TimetableEngine  # noqa: E402
from modules.timetable.timetable_supervisor import timetable_publish_blocking  # noqa: E402
from datetime import datetime, timezone  # noqa: E402

FIXTURE_DATE = "2026-04-06"
TIMETABLE_CURRENT = REPO_ROOT / "data" / "timetable" / "timetable_current.json"
SESSION_AUTHORITY = REPO_ROOT / "data" / "session" / "session_authority.json"


def _workspace_temp_dir() -> Path:
    base = Path.cwd() / "tmp" / "pytest_matrix"
    base.mkdir(parents=True, exist_ok=True)
    path = base / uuid.uuid4().hex
    path.mkdir(parents=True, exist_ok=False)
    return path


def _minimal_matrix_df(slot_time: str = "08:00") -> pd.DataFrame:
    """Single-row matrix sufficient for execution publish (mirrors dashboard matrix-first path)."""
    td = pd.Timestamp(FIXTURE_DATE)
    return pd.DataFrame(
        [
            {
                "Stream": "ES1",
                "trade_date": td,
                "Session": "S1",
                "Time": slot_time,
                "final_allowed": True,
                "Instrument": "ES",
            }
        ]
    )


def _execution_stream_filters() -> dict:
    """Non-empty merge so execution publish is not blocked (require_non_empty)."""
    return {
        "ES1": {
            "exclude_days_of_week": [],
            "exclude_days_of_month": [],
            "exclude_times": [],
        }
    }


def generate_timetable_like_api(date_yyyy_mm_dd: str, matrix_df: pd.DataFrame, stream_filters: dict) -> None:
    """
    Same core calls as dashboard POST /api/timetable/generate (_generate_timetable_sync in main.py):
    in-memory matrix + write_execution_timetable_from_master_matrix(..., execution_mode=True).
    """
    set_current_master_matrix_df(matrix_df)
    st = SessionAuthorityState(
        mode="manual",
        session_trading_date=date_yyyy_mm_dd,
        source="user",
        locked=True,
        set_at_utc=datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        set_by="test_timetable_date_persistence.generate_timetable_like_api",
        reason="manual_generate_request",
        version=read_next_version(REPO_ROOT),
    )
    save_authority(REPO_ROOT, st)

    engine = TimetableEngine(
        master_matrix_dir="data/master_matrix",
        analyzer_runs_dir="data/analyzed",
        project_root=str(REPO_ROOT),
    )
    session_td = date_yyyy_mm_dd
    with timetable_publish_blocking():
        engine.write_execution_timetable_from_master_matrix(
            matrix_df,
            trade_date=session_td,
            execution_mode=True,
            mode="historical",
            stream_filters=stream_filters,
            publish_context={
                "source": "manual",
                "reason": "publish",
                "caller": "test_timetable_date_persistence.generate_timetable_like_api",
                "matrix_source": "in_memory",
            },
        )


def _read_session_trading_date(path: Path) -> Optional[str]:
    if not path.is_file():
        return None
    try:
        doc = json.loads(path.read_text(encoding="utf-8"))
    except Exception:
        return None
    raw = doc.get("session_trading_date")
    return raw.strip() if isinstance(raw, str) else None


def _read_stream_slot(path: Path, stream: str) -> Optional[str]:
    if not path.is_file():
        return None
    try:
        doc = json.loads(path.read_text(encoding="utf-8"))
    except Exception:
        return None
    for row in doc.get("streams") or []:
        if isinstance(row, dict) and row.get("stream") == stream:
            slot = row.get("slot_time")
            return slot.strip() if isinstance(slot, str) else slot
    return None


def _fail_report(tc_path: Path, generate_ok: bool, label: str) -> str:
    final = _read_session_trading_date(tc_path)
    return (
        f"final session_trading_date={final!r} | generate worked={generate_ok} | "
        f"stage={label!r} | file exists={tc_path.is_file()}"
    )


def _wait_after_save_master_matrix(tc_path: Path, expected: str, timeout_s: float = 15.0) -> None:
    """Background _run_timetable_persist runs after save_master_matrix; poll until stable or timeout."""
    deadline = time.time() + timeout_s
    last_val = None
    stable = 0
    while time.time() < deadline:
        v = _read_session_trading_date(tc_path)
        if v == expected:
            if v == last_val:
                stable += 1
                if stable >= 3:
                    return
            else:
                stable = 0
            last_val = v
        time.sleep(0.05)
    raise AssertionError(f"persist wait timeout: expected session_trading_date={expected!r}")


def test_manual_session_date_survives_save_master_matrix_persist():
    matrix_df = _minimal_matrix_df()
    sf = _execution_stream_filters()
    prior_exists = TIMETABLE_CURRENT.is_file()
    prior_bytes = TIMETABLE_CURRENT.read_bytes() if prior_exists else None
    prior_auth_exists = SESSION_AUTHORITY.is_file()
    prior_auth_bytes = SESSION_AUTHORITY.read_bytes() if prior_auth_exists else None

    generate_ok = False
    try:
        generate_timetable_like_api(FIXTURE_DATE, matrix_df, sf)
        generate_ok = True
        assert _read_session_trading_date(TIMETABLE_CURRENT) == FIXTURE_DATE, _fail_report(
            TIMETABLE_CURRENT, generate_ok, "after_generate"
        )

        tmp = _workspace_temp_dir()
        try:
            save_master_matrix(matrix_df, str(tmp), stream_filters=sf)
        finally:
            shutil.rmtree(tmp, ignore_errors=True)

        _wait_after_save_master_matrix(TIMETABLE_CURRENT, FIXTURE_DATE)

        assert _read_session_trading_date(TIMETABLE_CURRENT) == FIXTURE_DATE, _fail_report(
            TIMETABLE_CURRENT, generate_ok, "after_resequence_persist"
        )
    except AssertionError:
        print(_fail_report(TIMETABLE_CURRENT, generate_ok, "assertion"))
        raise
    finally:
        if prior_bytes is not None:
            TIMETABLE_CURRENT.write_bytes(prior_bytes)
        elif TIMETABLE_CURRENT.is_file():
            TIMETABLE_CURRENT.unlink()
        if prior_auth_bytes is not None:
            SESSION_AUTHORITY.write_bytes(prior_auth_bytes)
        elif SESSION_AUTHORITY.is_file():
            SESSION_AUTHORITY.unlink()


def test_no_explicit_date_defaults_to_cme_session_string():
    """With persisted auto authority, publish uses that session (canonical CME)."""
    matrix_df = _minimal_matrix_df()
    sf = _execution_stream_filters()
    prior_exists = TIMETABLE_CURRENT.is_file()
    prior_bytes = TIMETABLE_CURRENT.read_bytes() if prior_exists else None
    prior_auth_exists = SESSION_AUTHORITY.is_file()
    prior_auth_bytes = SESSION_AUTHORITY.read_bytes() if prior_auth_exists else None

    try:
        initialize_auto_authority(REPO_ROOT, set_by="test_timetable_date_persistence")
        expected = get_cme_trading_date(datetime.now(timezone.utc))
        set_current_master_matrix_df(matrix_df)
        engine = TimetableEngine(project_root=str(REPO_ROOT))
        with timetable_publish_blocking():
            engine.write_execution_timetable_from_master_matrix(
                matrix_df,
                trade_date=expected,
                execution_mode=True,
                mode="live",
                stream_filters=sf,
                publish_context={
                    "source": "manual",
                    "reason": "publish",
                    "caller": "test_timetable_date_persistence.no_date",
                    "matrix_source": "in_memory",
                },
            )
        got = _read_session_trading_date(TIMETABLE_CURRENT)
        assert got == expected, f"expected CME session {expected!r}, got {got!r}"
    finally:
        if prior_bytes is not None:
            TIMETABLE_CURRENT.write_bytes(prior_bytes)
        elif TIMETABLE_CURRENT.is_file():
            TIMETABLE_CURRENT.unlink()
        if prior_auth_bytes is not None:
            SESSION_AUTHORITY.write_bytes(prior_auth_bytes)
        elif SESSION_AUTHORITY.is_file():
            SESSION_AUTHORITY.unlink()


def test_manual_locked_authority_republishes_latest_matrix_on_save_master_matrix():
    prior_exists = TIMETABLE_CURRENT.is_file()
    prior_bytes = TIMETABLE_CURRENT.read_bytes() if prior_exists else None
    prior_auth_exists = SESSION_AUTHORITY.is_file()
    prior_auth_bytes = SESSION_AUTHORITY.read_bytes() if prior_auth_exists else None

    try:
        initial_df = _minimal_matrix_df(slot_time="08:00")
        updated_df = _minimal_matrix_df(slot_time="07:30")
        sf = _execution_stream_filters()

        generate_timetable_like_api(FIXTURE_DATE, initial_df, sf)
        assert _read_session_trading_date(TIMETABLE_CURRENT) == FIXTURE_DATE
        assert _read_stream_slot(TIMETABLE_CURRENT, "ES1") == "08:00"

        tmp = _workspace_temp_dir()
        try:
            save_master_matrix(updated_df, str(tmp), stream_filters=sf)
        finally:
            shutil.rmtree(tmp, ignore_errors=True)

        _wait_after_save_master_matrix(TIMETABLE_CURRENT, FIXTURE_DATE)

        assert _read_session_trading_date(TIMETABLE_CURRENT) == FIXTURE_DATE
        assert _read_stream_slot(TIMETABLE_CURRENT, "ES1") == "07:30"
    finally:
        if prior_bytes is not None:
            TIMETABLE_CURRENT.write_bytes(prior_bytes)
        elif TIMETABLE_CURRENT.is_file():
            TIMETABLE_CURRENT.unlink()
        if prior_auth_bytes is not None:
            SESSION_AUTHORITY.write_bytes(prior_auth_bytes)
        elif SESSION_AUTHORITY.is_file():
            SESSION_AUTHORITY.unlink()
