"""
Eligibility immutability relative to CME *execution session* open (not 18:00 session-date rollover).

Matches RobotEngine.GetSessionWindow default: previous calendar day at 17:00 America/Chicago
until instruments in spec use different session starts (dashboard uses same default).
"""

from __future__ import annotations

import logging
from datetime import date, datetime, timedelta, timezone
from typing import Optional

import pytz

logger = logging.getLogger(__name__)

_CHICAGO = pytz.timezone("America/Chicago")


class EligibilityOverwriteBlockedAfterSessionStart(RuntimeError):
    """Overwrite denied: execution session for this session_trading_date has started."""


def execution_session_opens_chicago(session_trading_date_ymd: str) -> datetime:
    """Timezone-aware Chicago instant when Globex-style session opens for session date (prior day 17:00 CT)."""
    d = date.fromisoformat(session_trading_date_ymd.strip())
    prev = d - timedelta(days=1)
    naive = datetime(prev.year, prev.month, prev.day, 17, 0, 0)
    return _CHICAGO.localize(naive)


def is_execution_session_started_for_date(session_trading_date_ymd: str, now_utc: Optional[datetime] = None) -> bool:
    if now_utc is None:
        now_utc = datetime.now(timezone.utc)
    if now_utc.tzinfo is None:
        now_utc = now_utc.replace(tzinfo=timezone.utc)
    open_chi = execution_session_opens_chicago(session_trading_date_ymd)
    now_chi = now_utc.astimezone(_CHICAGO)
    return now_chi >= open_chi


def assert_eligibility_overwrite_allowed(
    session_trading_date: str,
    *,
    now_utc: Optional[datetime] = None,
    bypass_session_immutability_guard: bool = False,
) -> None:
    """
    Raises EligibilityOverwriteBlockedAfterSessionStart if overwrite would violate freeze after session open.
    bypass_session_immutability_guard: Emergency overwrite while file still exists (logs ELIGIBILITY_IMMUTABILITY_BYPASS).
        Distinct from eligibility_builder --force, which deletes the file first and does not use this flag.
    """
    if bypass_session_immutability_guard:
        return
    if is_execution_session_started_for_date(session_trading_date, now_utc):
        logger.error(
            "ELIGIBILITY_OVERWRITE_BLOCKED_AFTER_SESSION_START: session_trading_date=%s",
            session_trading_date,
        )
        raise EligibilityOverwriteBlockedAfterSessionStart(
            f"Eligibility overwrite blocked: execution session already started for {session_trading_date}"
        )
