"""
CME Session Boundary — Trading date from UTC using 18:00 America/Chicago.

Does NOT rely on system timezone. Uses UTC and converts to Chicago.
Deterministic for replay and machines in any timezone.

Rule:
  If Chicago time >= 18:00 → trading_date = next calendar day
  Else → trading_date = current calendar day

Example:
  2026-03-03 17:59 CT → trading_date = 2026-03-03
  2026-03-03 18:00 CT → trading_date = 2026-03-04
"""

from datetime import datetime, timedelta, timezone
from typing import Optional

import pytz

_CHICAGO = pytz.timezone("America/Chicago")


def get_trading_date_cme(utc_now: Optional[datetime] = None) -> str:
    """
    Compute trading_date using CME session boundary (18:00 America/Chicago).

    Args:
        utc_now: UTC datetime. If None, uses datetime.now(timezone.utc).

    Returns:
        trading_date as YYYY-MM-DD string.
    """
    if utc_now is None:
        utc_now = datetime.now(timezone.utc)
    # Ensure timezone-aware
    if utc_now.tzinfo is None:
        utc_now = utc_now.replace(tzinfo=timezone.utc)
    chicago_now = utc_now.astimezone(_CHICAGO)
    chicago_date = chicago_now.date()
    chicago_hour = chicago_now.hour
    # >= 18:00 CT → trading_date = next calendar day
    if chicago_hour >= 18:
        return (chicago_date + timedelta(days=1)).isoformat()
    return chicago_date.isoformat()


def is_past_cme_rollover(utc_now: Optional[datetime] = None) -> bool:
    """True if Chicago time >= 18:00 (past session boundary)."""
    if utc_now is None:
        utc_now = datetime.now(timezone.utc)
    if utc_now.tzinfo is None:
        utc_now = utc_now.replace(tzinfo=timezone.utc)
    chicago_now = utc_now.astimezone(_CHICAGO)
    return chicago_now.hour >= 18
