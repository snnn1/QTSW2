"""
CME session alignment check for timetable_current.json (watchdog-only).

Read-only: does not create or modify timetable or eligibility. After 18:00 CT the CME trading
day advances; until matrix/dashboard publishes a file whose session_trading_date matches
get_cme_trading_date(now), we log and exit. Robot remains fail-closed until valid data exists.
"""

from __future__ import annotations

import json
import logging
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Dict, Optional

logger = logging.getLogger(__name__)


def _session_from_doc(doc: Dict[str, Any]) -> Optional[str]:
    sd = doc.get("session_trading_date") or doc.get("trading_date")
    if sd is None:
        return None
    s = str(sd).strip()
    return s or None


def _timetable_doc_is_replay(doc: Dict[str, Any]) -> bool:
    if doc.get("replay") is True:
        return True
    meta = doc.get("metadata")
    if isinstance(meta, dict) and meta.get("replay") is True:
        return True
    return False


def _try_load_timetable_current(timetable_path: Path) -> Optional[Dict[str, Any]]:
    try:
        if not timetable_path.is_file():
            return None
        data = json.loads(timetable_path.read_bytes().decode("utf-8"))
        return data if isinstance(data, dict) else None
    except Exception:
        return None


def ensure_current_session_timetable(project_root: Path | str) -> None:
    """
    Read-only check: compare timetable_current.json session date to current CME session.

    If missing, unreadable, or session_trading_date != get_cme_trading_date(UTC now), log
    SESSION_WAITING_FOR_VALID_TIMETABLE at INFO and return. No files are written.

    Replay-marked timetables are skipped (no log; live vs replay separation).
    """
    try:
        root = Path(project_root).resolve()
        timetable_path = root / "data" / "timetable" / "timetable_current.json"
        from modules.timetable.cme_session import get_cme_trading_date, resolve_live_execution_session_trading_date

        utc = datetime.now(timezone.utc)
        expected = get_cme_trading_date(utc)
        doc = _try_load_timetable_current(timetable_path)

        replay = doc is not None and _timetable_doc_is_replay(doc)
        if replay:
            return

        raw_session = _session_from_doc(doc) if doc is not None else None
        if raw_session is None:
            logger.info(
                "SESSION_WAITING_FOR_VALID_TIMETABLE: expected_cme_session=%s timetable_session=%s path=%s",
                expected,
                None,
                timetable_path,
            )
            return

        effective, _ = resolve_live_execution_session_trading_date(
            raw_session, utc, is_replay_document=False
        )
        if effective == expected:
            return

        logger.info(
            "SESSION_WAITING_FOR_VALID_TIMETABLE: expected_cme_session=%s timetable_session=%s path=%s",
            expected,
            raw_session,
            timetable_path,
        )
    except Exception:
        logger.debug("SESSION_WAITING_FOR_VALID_TIMETABLE check skipped (non-fatal)", exc_info=True)
