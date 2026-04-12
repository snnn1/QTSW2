"""
Timetable Supervisor — periodic validation of timetable artifact vs SessionAuthority and canonical CME.

Phase 4: does **not** publish or mutate session authority. On drift it logs structured events
(`TIMETABLE_SUPERVISOR_DRIFT_DETECTED`, `SUPERVISOR_REPAIR_RECOMMENDED`) so operators or automation
can republish via API / matrix persist.
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

# Single-writer discipline for all live timetable publishes (API, matrix save — not supervisor).
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
    """If another publish is in progress, return False (legacy helper; supervisor does not publish)."""
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
    from modules.session_authority.store import (
        SessionAuthorityRequiredError,
        load_persisted_strict,
    )

    from .cme_session import get_cme_trading_date

    utc_now = datetime.now(timezone.utc)
    current_cme = get_cme_trading_date(utc_now)

    try:
        auth = load_persisted_strict(Path(project_root))
    except SessionAuthorityRequiredError:
        logger.warning("TIMETABLE_SUPERVISOR_SKIP_NO_SESSION_AUTHORITY")
        return

    if auth.mode != "auto":
        return

    if auth.session_trading_date != current_cme:
        logger.info(
            "TIMETABLE_SUPERVISOR_SKIP_AUTHORITY_NOT_CANONICAL authority_session=%s canonical_cme=%s",
            auth.session_trading_date,
            current_cme,
        )
        return

    timetable_file = Path(project_root) / "data" / "timetable" / "timetable_current.json"
    timetable_file.parent.mkdir(parents=True, exist_ok=True)

    doc = _read_timetable_doc(timetable_file)
    if _is_replay_document(doc):
        return

    if isinstance(doc, dict):
        # LEGACY: manual publish marker — skip drift noise; not used for matrix selection.
        if doc.get("manual_session") is True:
            return
        _src = doc.get("source")
        if isinstance(_src, str) and _src.strip() and _src.strip().lower() != "auto":
            return

    file_ymd = _file_session_ymd(doc)

    if file_ymd == current_cme:
        return

    # Drift: authority is canonical auto, but timetable file session differs (or missing).
    drift_class = "stale_timetable_artifact"
    if file_ymd is None:
        drift_class = "missing_or_empty_timetable_session"
    elif file_ymd != auth.session_trading_date:
        drift_class = "timetable_vs_authority_mismatch"

    logger.warning(
        "TIMETABLE_SUPERVISOR_DRIFT_DETECTED drift_class=%s canonical_cme=%s authority_session=%s "
        "timetable_file_session=%s timetable_path=%s",
        drift_class,
        current_cme,
        auth.session_trading_date,
        file_ymd or "(missing_or_empty)",
        timetable_file,
    )
    logger.warning(
        "SUPERVISOR_REPAIR_RECOMMENDED action=republish_via_control_plane drift_class=%s "
        "hint=POST /api/timetable/execution or save_master_matrix after matrix update; "
        "do not expect supervisor to auto-publish",
        drift_class,
    )


def _supervisor_loop(project_root: Path, stop_event: threading.Event) -> None:
    pr = Path(project_root).resolve()
    logger.info("TIMETABLE_SUPERVISOR_STARTED interval_s=30 root=%s mode=validator_only", pr)
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
