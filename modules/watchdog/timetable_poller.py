"""
Timetable Poller

Polls timetable_current.json and computes trading_date using CME rollover rule.
"""
import json
import hashlib
import logging
from pathlib import Path
from typing import Tuple, Optional, Set
from datetime import datetime
import pytz

from .config import QTSW2_ROOT

logger = logging.getLogger(__name__)

CHICAGO_TZ = pytz.timezone("America/Chicago")


def compute_timetable_trading_date(chicago_now: datetime) -> str:
    """
    Compute trading_date using CME rollover rule (17:00 Chicago).
    
    CRITICAL: chicago_now must be timezone-aware (America/Chicago).
    This function is called on every poll to advance trading_date at 17:00 CT,
    independent of timetable file content changes.
    
    Args:
        chicago_now: Timezone-aware datetime in America/Chicago timezone
        
    Returns:
        Trading date string (YYYY-MM-DD format)
        
    Raises:
        ValueError: If chicago_now is not timezone-aware
    """
    if chicago_now.tzinfo is None:
        raise ValueError("chicago_now must be timezone-aware (America/Chicago)")
    
    chicago_date = chicago_now.date()
    if chicago_now.hour >= 17:
        from datetime import timedelta
        return (chicago_date + timedelta(days=1)).isoformat()
    return chicago_date.isoformat()


class TimetablePoller:
    """Polls timetable_current.json and extracts enabled streams."""
    
    def __init__(self):
        """Initialize timetable poller."""
        self._timetable_path = QTSW2_ROOT / "data" / "timetable" / "timetable_current.json"
    
    def poll(self) -> Tuple[str, Optional[Set[str]], Optional[str]]:
        """
        Poll timetable file and return trading_date, enabled streams, and hash.
        
        Returns:
            Tuple of (trading_date, enabled_streams_set, timetable_hash)
            
        Failure behavior:
            - If file missing or parse error → return (computed_trading_date, None, None)
            - Do not throw exceptions
            - Log warning/error for failures
        """
        # Always compute trading_date using CME rollover (even on failure)
        chicago_now = datetime.now(CHICAGO_TZ)
        trading_date = compute_timetable_trading_date(chicago_now)
        
        # Try to load timetable file
        if not self._timetable_path.exists():
            logger.warning(
                f"TIMETABLE_POLL_FAIL: trading_date={trading_date} computed, "
                f"but timetable file missing: {self._timetable_path} (fail-open mode)"
            )
            return (trading_date, None, None)
        
        try:
            # Read file contents for hashing
            with open(self._timetable_path, 'rb') as f:
                file_contents = f.read()
            
            # Compute hash
            timetable_hash = hashlib.sha256(file_contents).hexdigest()
            
            # Parse JSON
            try:
                timetable = json.loads(file_contents.decode('utf-8'))
            except json.JSONDecodeError as e:
                logger.error(
                    f"TIMETABLE_POLL_FAIL: trading_date={trading_date} computed, "
                    f"but timetable file parse error: {e} (fail-open mode)"
                )
                return (trading_date, None, None)
            
            # Extract enabled streams
            enabled_streams = self._extract_enabled_streams(timetable)
            
            return (trading_date, enabled_streams, timetable_hash)
            
        except Exception as e:
            # Never throw - log and return safe defaults
            logger.error(
                f"TIMETABLE_POLL_FAIL: trading_date={trading_date} computed, "
                f"but unexpected error reading timetable: {e} (fail-open mode)",
                exc_info=True
            )
            return (trading_date, None, None)
    
    def _extract_enabled_streams(self, timetable: dict) -> Set[str]:
        """
        Extract enabled stream IDs from timetable.
        
        Args:
            timetable: Parsed timetable dictionary
            
        Returns:
            Set of enabled stream IDs
        """
        enabled_streams = set()
        
        streams = timetable.get('streams', [])
        if not isinstance(streams, list):
            logger.warning(f"Timetable 'streams' is not a list: {type(streams)}")
            return enabled_streams
        
        for stream_entry in streams:
            if not isinstance(stream_entry, dict):
                continue
            
            stream_id = stream_entry.get('stream')
            enabled = stream_entry.get('enabled', False)
            
            if stream_id and enabled:
                enabled_streams.add(stream_id)
        
        return enabled_streams
