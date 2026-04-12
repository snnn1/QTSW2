"""
Market Session Utility

``market_open`` for Watchdog status/alerts follows ``market_calendar.get_market_state``
(holidays JSON + session day from ``get_cme_trading_date``), not a duplicate clock table.
"""
from datetime import datetime, timezone

import pytz

CHICAGO_TZ = pytz.timezone("America/Chicago")


def is_market_open(chicago_dt: datetime) -> bool:
    """
    True only when ``get_market_state`` reports session ``OPEN`` (not PRE-OPEN / CLOSED / holiday / weekend / maintenance).
    """
    if chicago_dt.tzinfo is None:
        raise ValueError("Datetime must be timezone-aware (America/Chicago)")

    from modules.watchdog.market_calendar import get_market_state

    utc = chicago_dt.astimezone(timezone.utc)
    state = get_market_state(log_transition=False, utc_now=utc).get("state")
    return state == "OPEN"
