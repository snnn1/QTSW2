"""
Market Session Utility

Determines if CME futures market is open based on Chicago time.
"""
from datetime import datetime, time
import pytz

CHICAGO_TZ = pytz.timezone("America/Chicago")


def is_market_open(chicago_dt: datetime) -> bool:
    """
    CME futures session rules (initial implementation, no holidays):

    - Open: Sunday 17:00 CT → Friday 16:00 CT
    - Daily maintenance break: 16:00–17:00 CT (closed)
    - Closed all day Saturday
    """

    if chicago_dt.tzinfo is None:
        raise ValueError("Datetime must be timezone-aware (America/Chicago)")

    weekday = chicago_dt.weekday()  # Mon=0 … Sun=6
    t = chicago_dt.time()

    # Saturday: always closed
    if weekday == 5:
        return False

    # Sunday: open only after 17:00
    if weekday == 6:
        return t >= time(17, 0)

    # Friday: closed after 16:00
    if weekday == 4:
        return t < time(16, 0)

    # Mon–Thu: daily maintenance break
    if time(16, 0) <= t < time(17, 0):
        return False

    return True
