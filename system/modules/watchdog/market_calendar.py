"""
CME-style market calendar state (data-driven holidays + session rules).

Holidays: modules/config/cme_holidays_{year}.json (FULL_CLOSE, extensible for early closes).

Session / holiday calendar day: modules.timetable.cme_session.get_cme_trading_date (same as timetable).

Transition logging is persisted under data/watchdog/market_state.json to survive process restarts.
"""

from __future__ import annotations

import json
import logging
import os
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple

import pytz

from modules.timetable.cme_session import get_cme_trading_date

logger = logging.getLogger(__name__)

CHICAGO_TZ = pytz.timezone("America/Chicago")

_WATCHDOG_DIR = Path(__file__).resolve().parent
_MODULES_DIR = _WATCHDOG_DIR.parent
_QTSW2_ROOT = _MODULES_DIR.parent
_CONFIG_DIR = _MODULES_DIR / "config"
_MARKET_STATE_PATH = _QTSW2_ROOT / "data" / "watchdog" / "market_state.json"


def _holiday_file_path(year: int) -> Path:
    return _CONFIG_DIR / f"cme_holidays_{year}.json"


def _load_holidays_from_existing_file(path: Path) -> Tuple[List[Dict[str, Any]], bool]:
    """
    Load holidays JSON. Caller must ensure path.exists().
    Returns (holidays_list, calendar_loaded_ok).
    """
    try:
        with open(path, "r", encoding="utf-8") as f:
            data = json.load(f)
        raw = data.get("holidays", [])
        return (raw if isinstance(raw, list) else [], True)
    except (OSError, json.JSONDecodeError) as e:
        logger.error("MARKET_CALENDAR_UNREADABLE: path=%s error=%s", path, e)
        return [], False


def load_holidays(year: int) -> List[Dict[str, Any]]:
    """Backward-compatible: missing file → []. Prefer get_market_state() for fail-safe behavior."""
    path = _holiday_file_path(year)
    if not path.exists():
        return []
    rows, ok = _load_holidays_from_existing_file(path)
    return rows if ok else []


def _match_holiday(
    chicago_date: str, holidays: List[Dict[str, Any]]
) -> Tuple[bool, Optional[str]]:
    for h in holidays:
        if not isinstance(h, dict):
            continue
        if h.get("date") == chicago_date and h.get("type") == "FULL_CLOSE":
            name = h.get("name")
            return True, str(name) if name else "Holiday"
    return False, None


def _read_persisted_last_state() -> Optional[str]:
    path = _MARKET_STATE_PATH
    if not path.exists():
        return None
    try:
        with open(path, "r", encoding="utf-8") as f:
            data = json.load(f)
        st = data.get("last_state")
        return str(st).strip() if st is not None else None
    except (OSError, json.JSONDecodeError):
        return None


def _write_persisted_last_state(state: str) -> None:
    path = _MARKET_STATE_PATH
    try:
        path.parent.mkdir(parents=True, exist_ok=True)
        payload = {"last_state": state}
        tmp = path.with_suffix(f".tmp.{os.getpid()}")
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump(payload, f, ensure_ascii=False)
            f.flush()
            os.fsync(f.fileno())
        os.replace(tmp, path)
    except OSError as e:
        logger.warning("MARKET_STATE_PERSIST_FAIL: path=%s error=%s", path, e)


def _apply_transition_logging(result: Dict[str, Any], log_transition: bool) -> None:
    if not log_transition:
        return
    state = result.get("state")
    if not state:
        return
    prev = _read_persisted_last_state()
    if prev is not None and prev != state:
        logger.info(
            "MARKET_STATE_CHANGE %s",
            json.dumps(
                {
                    "from": prev,
                    "to": state,
                    "phase": result.get("phase"),
                    "reason": result.get("reason"),
                    "calendar_date_chicago": result.get("calendar_date_chicago"),
                    "timestamp_chicago": result.get("timestamp_chicago"),
                },
                ensure_ascii=False,
            ),
        )
    _write_persisted_last_state(str(state))


def _base_result(
    *,
    state: str,
    phase: str,
    reason: str,
    timestamp_chicago: str,
    calendar_date_chicago: str,
    calendar_loaded: bool,
) -> Dict[str, Any]:
    return {
        "state": state,
        "phase": phase,
        "reason": reason,
        "timestamp_chicago": timestamp_chicago,
        "calendar_date_chicago": calendar_date_chicago,
        "calendar_loaded": calendar_loaded,
    }


def get_market_state(
    *, log_transition: bool = True, utc_now: Optional[datetime] = None
) -> Dict[str, Any]:
    """
    Rough CME Globex-style state (full-close holidays from JSON; not instrument-specific).

    Returns:
        state: OPEN | CLOSED | PRE-OPEN
        phase: SESSION | HOLIDAY | WEEKEND | MAINTENANCE | PRE_OPEN | CALENDAR_ERROR
        reason, timestamp_chicago,         calendar_date_chicago (YYYY-MM-DD, same as get_cme_trading_date / timetable session day),
        calendar_loaded: False if holiday file missing or unreadable for session year.

    utc_now:
        Wall-time instant as UTC (default: now). Used by is_market_open() for consistency with a passed Chicago clock.
    """
    utc_now = utc_now if utc_now is not None else datetime.now(timezone.utc)
    if utc_now.tzinfo is None:
        utc_now = utc_now.replace(tzinfo=timezone.utc)
    now = utc_now.astimezone(CHICAGO_TZ)
    holiday_date = get_cme_trading_date(utc_now)
    holiday_year = int(holiday_date[:4])
    ts = now.isoformat()

    hpath = _holiday_file_path(holiday_year)
    if not hpath.exists():
        logger.error(
            "MARKET_CALENDAR_MISSING: session_calendar_date=%s holiday_year=%s path=%s",
            holiday_date,
            holiday_year,
            hpath,
        )
        result = _base_result(
            state="CLOSED",
            phase="CALENDAR_ERROR",
            reason="Missing holiday calendar",
            timestamp_chicago=ts,
            calendar_date_chicago=holiday_date,
            calendar_loaded=False,
        )
        _apply_transition_logging(result, log_transition)
        return result

    holidays, ok = _load_holidays_from_existing_file(hpath)
    if not ok:
        result = _base_result(
            state="CLOSED",
            phase="CALENDAR_ERROR",
            reason="Holiday calendar unreadable",
            timestamp_chicago=ts,
            calendar_date_chicago=holiday_date,
            calendar_loaded=False,
        )
        _apply_transition_logging(result, log_transition)
        return result

    holiday, hname = _match_holiday(holiday_date, holidays)
    if holiday:
        result = _base_result(
            state="CLOSED",
            phase="HOLIDAY",
            reason=f"Holiday: {hname}",
            timestamp_chicago=ts,
            calendar_date_chicago=holiday_date,
            calendar_loaded=True,
        )
        _apply_transition_logging(result, log_transition)
        return result

    weekday = now.weekday()  # Mon=0 .. Sun=6
    hour = now.hour

    if weekday == 5:  # Saturday
        result = _base_result(
            state="CLOSED",
            phase="WEEKEND",
            reason="Weekend",
            timestamp_chicago=ts,
            calendar_date_chicago=holiday_date,
            calendar_loaded=True,
        )
        _apply_transition_logging(result, log_transition)
        return result

    if weekday == 6:  # Sunday
        if hour < 16:
            result = _base_result(
                state="CLOSED",
                phase="WEEKEND",
                reason="Weekend",
                timestamp_chicago=ts,
                calendar_date_chicago=holiday_date,
                calendar_loaded=True,
            )
        elif hour < 17:
            result = _base_result(
                state="PRE-OPEN",
                phase="PRE_OPEN",
                reason="Pre-open window",
                timestamp_chicago=ts,
                calendar_date_chicago=holiday_date,
                calendar_loaded=True,
            )
        elif hour < 18:
            result = _base_result(
                state="CLOSED",
                phase="MAINTENANCE",
                reason="Maintenance window",
                timestamp_chicago=ts,
                calendar_date_chicago=holiday_date,
                calendar_loaded=True,
            )
        else:
            result = _base_result(
                state="OPEN",
                phase="SESSION",
                reason="Regular session",
                timestamp_chicago=ts,
                calendar_date_chicago=holiday_date,
                calendar_loaded=True,
            )
        _apply_transition_logging(result, log_transition)
        return result

    # Monday–Friday: daily maintenance 17:00–17:59 Chicago
    if 17 <= hour < 18:
        result = _base_result(
            state="CLOSED",
            phase="MAINTENANCE",
            reason="Maintenance window",
            timestamp_chicago=ts,
            calendar_date_chicago=holiday_date,
            calendar_loaded=True,
        )
        _apply_transition_logging(result, log_transition)
        return result

    result = _base_result(
        state="OPEN",
        phase="SESSION",
        reason="Regular session",
        timestamp_chicago=ts,
        calendar_date_chicago=holiday_date,
        calendar_loaded=True,
    )
    _apply_transition_logging(result, log_transition)
    return result
