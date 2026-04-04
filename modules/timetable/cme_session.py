"""
CME session boundary — single canonical implementation (18:00 America/Chicago).

All timetable, eligibility, watchdog market calendar (holidays), and orchestration code
MUST use get_cme_trading_date() only.
"""

from datetime import date, datetime, timedelta, timezone
from typing import Optional, Tuple

import pytz

_CHICAGO = pytz.timezone("America/Chicago")


def apply_cme_weekend_roll(session_day: date) -> date:
    """
    Equity-index-style session label: Saturday/Sunday roll forward to the following Monday.
    Applies after the 18:00 Chicago calendar-day roll (parity with Matrix / JS getCmeSessionTradingCalendarYmdFromUtc).
    """
    wd = session_day.weekday()  # Monday=0 .. Sunday=6
    if wd == 5:  # Saturday
        return session_day + timedelta(days=2)
    if wd == 6:  # Sunday
        return session_day + timedelta(days=1)
    return session_day


def get_cme_trading_date(dt_utc: Optional[datetime] = None) -> str:
    """
    Canonical CME session trading date (YYYY-MM-DD).

    - Convert UTC → America/Chicago.
    - If Chicago wall time >= 18:00 → session calendar day = next calendar day (same local date rule as CME pit/globex session change).
    - Else → session calendar day = Chicago calendar date.
    - If that calendar day falls on Saturday or Sunday, roll forward to Monday (weekend has no standalone session label).

    Args:
        dt_utc: Instant in UTC. If None, uses datetime.now(timezone.utc).

    Returns:
        session trading date as YYYY-MM-DD.
    """
    if dt_utc is None:
        dt_utc = datetime.now(timezone.utc)
    if dt_utc.tzinfo is None:
        dt_utc = dt_utc.replace(tzinfo=timezone.utc)
    chicago_now = dt_utc.astimezone(_CHICAGO)
    chicago_date = chicago_now.date()
    if chicago_now.hour >= 18:
        session_day = chicago_date + timedelta(days=1)
    else:
        session_day = chicago_date
    session_day = apply_cme_weekend_roll(session_day)
    return session_day.isoformat()


def get_trading_date_cme(utc_now: Optional[datetime] = None) -> str:
    """Deprecated alias — use get_cme_trading_date()."""
    return get_cme_trading_date(utc_now)


def is_past_cme_rollover(utc_now: Optional[datetime] = None) -> bool:
    """True if Chicago time >= 18:00 (past CME session boundary)."""
    if utc_now is None:
        utc_now = datetime.now(timezone.utc)
    if utc_now.tzinfo is None:
        utc_now = utc_now.replace(tzinfo=timezone.utc)
    chicago_now = utc_now.astimezone(_CHICAGO)
    return chicago_now.hour >= 18


def resolve_live_execution_session_trading_date(
    file_session_ymd: str,
    utc_now: Optional[datetime] = None,
    *,
    is_replay_document: bool = False,
) -> Tuple[Optional[str], str]:
    """
    Map timetable_current.json ``session_trading_date`` to the live CME session day used
    for watchdog + robot alignment.

    Returns ``(effective_yyyy_mm_dd_or_None, reason)``.

    - **replay_ok**: replay documents — trust file date (must be valid YYYY-MM-DD).
    - **ok**: file date equals ``get_cme_trading_date(utc_now)``.
    - **clamped_ahead**: file is exactly one calendar day ahead of canonical CME and
      Chicago wall time is **before** 18:00 — treat as canonical day (parity with RobotEngine).
    - **reject_***: unusable for live alignment (staleness, gap, invalid string).

    Reasons: ``replay_ok``, ``ok``, ``clamped_ahead``, ``reject_empty``, ``reject_invalid_format``,
    ``reject_mismatch``.
    """
    if utc_now is None:
        utc_now = datetime.now(timezone.utc)
    if utc_now.tzinfo is None:
        utc_now = utc_now.replace(tzinfo=timezone.utc)

    raw = (file_session_ymd or "").strip()
    if not raw:
        return None, "reject_empty"

    try:
        file_d = date.fromisoformat(raw)
    except ValueError:
        return None, "reject_invalid_format"

    if is_replay_document:
        return raw, "replay_ok"

    expected_s = get_cme_trading_date(utc_now)
    expected_d = date.fromisoformat(expected_s)

    if file_d == expected_d:
        return expected_s, "ok"

    # Early publish: next calendar day in file before 18:00 CT rollover
    if file_d == expected_d + timedelta(days=1) and not is_past_cme_rollover(utc_now):
        return expected_s, "clamped_ahead"

    return None, "reject_mismatch"
