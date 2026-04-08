"""
Timetable Supervisor — single time-driven owner of aligning `timetable_current.json`
with the current CME trading session.

Does NOT build matrix, does NOT call UI. Compares file session vs canonical CME and
invokes the existing TimetableEngine publish path only.
"""

from __future__ import annotations

import json
import logging
import threading
from contextlib import contextmanager
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Dict, Optional

logger = logging.getLogger(__name__)

# Single-writer discipline for all live timetable publishes (supervisor, API, matrix save).
_PUBLISH_LOCK = threading.Lock()


@contextmanager
def timetable_publish_blocking():
    """Block until the lock is free, then publish (API / matrix save)."""
    _PUBLISH_LOCK.acquire()
    try:
        yield
    finally:
        _PUBLISH_LOCK.release()


def try_acquire_timetable_publish_nonblocking() -> bool:
    """If another publish is in progress, return False (supervisor skips this cycle)."""
    return _PUBLISH_LOCK.acquire(blocking=False)


def release_timetable_publish() -> None:
    """Release after a successful try_acquire_timetable_publish_nonblocking()."""
    _PUBLISH_LOCK.release()


def _read_timetable_doc(path: Path) -> Optional[Dict[str, Any]]:
    if not path.exists():
        return None
    try:
        with open(path, "r", encoding="utf-8") as f:
            return json.load(f)
    except Exception as e:
        logger.warning("TIMETABLE_SUPERVISOR_FILE_READ_ERROR path=%s error=%s", path, e)
        return None


def _file_session_ymd(doc: Optional[Dict[str, Any]]) -> Optional[str]:
    if not doc:
        return None
    raw = doc.get("session_trading_date") or doc.get("trading_date") or ""
    if isinstance(raw, str):
        s = raw.strip()
    else:
        s = str(raw).strip() if raw is not None else ""
    if not s:
        return None
    return s.split("T")[0].strip()


def _is_replay_document(doc: Optional[Dict[str, Any]]) -> bool:
    if not doc:
        return False
    if doc.get("replay") is True:
        return True
    meta = doc.get("metadata")
    return isinstance(meta, dict) and meta.get("replay") is True


def _supervisor_cycle(project_root: Path) -> None:
    from modules.matrix.file_manager import get_current_master_matrix_df

    from .cme_session import get_cme_trading_date
    from .timetable_engine import (
        TimetableEngine,
        TimetableWriteBlockedCmeMismatch,
    )

    utc_now = datetime.now(timezone.utc)
    current_cme = get_cme_trading_date(utc_now)

    timetable_file = Path(project_root) / "data" / "timetable" / "timetable_current.json"
    timetable_file.parent.mkdir(parents=True, exist_ok=True)

    doc = _read_timetable_doc(timetable_file)
    if _is_replay_document(doc):
        return

    file_ymd = _file_session_ymd(doc)

    # Option A: always enforce current CME session when labels differ (including missing file).
    if file_ymd == current_cme:
        return

    logger.info(
        "TIMETABLE_SESSION_MISMATCH_DETECTED current_cme_date=%s file_date=%s",
        current_cme,
        file_ymd or "(missing_or_empty)",
    )

    matrix_df = get_current_master_matrix_df()
    if matrix_df is None:
        logger.warning(
            "TIMETABLE_SUPERVISOR_MATRIX_MISSING current_cme_date=%s file_date=%s",
            current_cme,
            file_ymd or "(missing_or_empty)",
        )
        return

    if not try_acquire_timetable_publish_nonblocking():
        logger.debug("TIMETABLE_SUPERVISOR_SKIP_PUBLISH_IN_PROGRESS")
        return

    try:
        engine = TimetableEngine(
            master_matrix_dir=str(Path(project_root) / "data" / "master_matrix"),
            analyzer_runs_dir=str(Path(project_root) / "data" / "analyzed"),
            project_root=str(Path(project_root).resolve()),
        )
        try:
            res = engine.write_execution_timetable_from_master_matrix(
                matrix_df,
                execution_mode=True,
                publish_context={
                    "source": "auto",
                    "reason": "session_roll_supervisor",
                    "caller": "modules.timetable.timetable_supervisor",
                    "matrix_source": "in_memory",
                },
            )
        except TimetableWriteBlockedCmeMismatch as e:
            logger.error(
                "TIMETABLE_SESSION_AUTO_PUBLISH_FAILED error=%s",
                e,
            )
            return
        except Exception as e:
            logger.error(
                "TIMETABLE_SESSION_AUTO_PUBLISH_FAILED error=%s",
                e,
                exc_info=True,
            )
            return

        new_hash = getattr(res, "timetable_hash", None) if res else None
        logger.info(
            "TIMETABLE_SESSION_AUTO_PUBLISHED new_session=%s reason=session_roll_supervisor hash=%s",
            current_cme,
            new_hash or "",
        )
    finally:
        release_timetable_publish()


def _supervisor_loop(project_root: Path, stop_event: threading.Event) -> None:
    pr = Path(project_root).resolve()
    logger.info("TIMETABLE_SUPERVISOR_STARTED interval_s=30 root=%s", pr)
    while not stop_event.is_set():
        try:
            _supervisor_cycle(pr)
        except Exception as e:
            logger.error("TIMETABLE_SUPERVISOR_CYCLE_ERROR error=%s", e, exc_info=True)
        if stop_event.wait(timeout=30.0):
            break
    logger.info("TIMETABLE_SUPERVISOR_STOPPED")


def start_timetable_supervisor(project_root: Path) -> threading.Event:
    """
    Start the supervisor in a daemon thread. Returns an Event; set it to stop the loop on shutdown.
    """
    stop = threading.Event()
    t = threading.Thread(
        target=_supervisor_loop,
        args=(project_root, stop),
        name="TimetableSupervisor",
        daemon=True,
    )
    t.start()
    return stop


__all__ = [
    "timetable_publish_blocking",
    "try_acquire_timetable_publish_nonblocking",
    "release_timetable_publish",
    "start_timetable_supervisor",
]
