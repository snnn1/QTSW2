"""
Timetable Poller

Polls timetable_current.json and extracts trading_date from timetable file (authoritative).
Falls back to CME rollover computation only when timetable is unavailable or trading_date is invalid.
"""
import json
import hashlib
import logging
from pathlib import Path
from typing import Tuple, Optional, Set, Dict
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
    
    def poll(self) -> Tuple[str, Optional[Set[str]], Optional[str], Optional[Dict[str, Dict]]]:
        """
        Poll timetable file and return trading_date, enabled streams, hash, and stream metadata.
        
        Returns:
            Tuple of (trading_date, enabled_streams_set, timetable_hash, enabled_streams_metadata)
            enabled_streams_metadata: Dict[str, Dict] mapping stream_id -> {instrument, session, slot_time, enabled}
            
        Trading date priority:
            - First: Extract trading_date from timetable file (authoritative)
            - Fallback: Compute using CME rollover rule (17:00 CT)
            
        Failure behavior:
            - If file missing or parse error → return (computed_trading_date, None, None, None)
            - If trading_date invalid → return (computed_trading_date, enabled_streams, hash, metadata) with WARN
            - Do not throw exceptions
            - Log warning/error for failures
        """
        # Compute fallback trading_date using CME rollover (used if timetable unavailable)
        chicago_now = datetime.now(CHICAGO_TZ)
        computed_trading_date = compute_timetable_trading_date(chicago_now)
        
        # Try to load timetable file
        if not self._timetable_path.exists():
            logger.warning(
                f"TIMETABLE_POLL_FAIL: trading_date={computed_trading_date} (computed fallback), "
                f"timetable file missing: {self._timetable_path} (fail-open mode)"
            )
            return (computed_trading_date, None, None, None)
        
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
                    f"TIMETABLE_POLL_FAIL: trading_date={computed_trading_date} (computed fallback), "
                    f"timetable file parse error: {e} (fail-open mode)"
                )
                return (computed_trading_date, None, None, None)
            
            # Extract trading_date from timetable (authoritative source)
            timetable_trading_date = timetable.get('trading_date')
            trading_date_source = "timetable"
            
            if timetable_trading_date and self._validate_trading_date(timetable_trading_date):
                # Use timetable's trading_date (authoritative)
                trading_date = timetable_trading_date
            else:
                # Fallback to computed date
                trading_date = computed_trading_date
                trading_date_source = "computed fallback"
                
                # Refinement 2: Use WARN level (not INFO) - this is a pipeline contract violation
                if timetable_trading_date:
                    logger.warning(
                        f"TIMETABLE_INVALID_TRADING_DATE: Invalid trading_date in timetable: '{timetable_trading_date}', "
                        f"using computed fallback: {trading_date}. This indicates an upstream pipeline bug."
                    )
                else:
                    logger.warning(
                        f"TIMETABLE_MISSING_TRADING_DATE: trading_date field missing in timetable, "
                        f"using computed fallback: {trading_date}."
                    )
            
            # Extract enabled streams
            enabled_streams = self._extract_enabled_streams(timetable)
            
            # Extract full stream metadata for enabled streams
            enabled_streams_metadata = self._extract_enabled_streams_metadata(timetable)
            
            # Log successful poll with trading_date source
            if enabled_streams is not None:
                logger.info(
                    f"TIMETABLE_POLL_OK: trading_date={trading_date} (from {trading_date_source}), "
                    f"enabled_count={len(enabled_streams)}, hash={timetable_hash[:8] if timetable_hash else 'N/A'}"
                )
            
            return (trading_date, enabled_streams, timetable_hash, enabled_streams_metadata)
            
        except Exception as e:
            # Never throw - log and return safe defaults
            logger.error(
                f"TIMETABLE_POLL_FAIL: trading_date={computed_trading_date} (computed fallback), "
                f"unexpected error reading timetable: {e} (fail-open mode)",
                exc_info=True
            )
            return (computed_trading_date, None, None, None)
    
    def _validate_trading_date(self, trading_date: str) -> bool:
        """
        Validate trading_date format (YYYY-MM-DD).
        
        Args:
            trading_date: Trading date string to validate
            
        Returns:
            True if valid YYYY-MM-DD format, False otherwise
        """
        if not isinstance(trading_date, str):
            return False
        
        try:
            # Validate YYYY-MM-DD format
            datetime.strptime(trading_date, '%Y-%m-%d')
            return True
        except (ValueError, TypeError):
            return False
    
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
    
    def _extract_enabled_streams_metadata(self, timetable: dict) -> Dict[str, Dict]:
        """
        Extract full metadata for enabled streams from timetable.
        
        Args:
            timetable: Parsed timetable dictionary
            
        Returns:
            Dict mapping stream_id -> {instrument, session, slot_time, enabled}
        """
        metadata = {}
        
        streams = timetable.get('streams', [])
        if not isinstance(streams, list):
            logger.warning(f"Timetable 'streams' is not a list: {type(streams)}")
            return metadata
        
        for stream_entry in streams:
            if not isinstance(stream_entry, dict):
                continue
            
            stream_id = stream_entry.get('stream')
            enabled = stream_entry.get('enabled', False)
            
            if stream_id and enabled:
                metadata[stream_id] = {
                    'instrument': stream_entry.get('instrument', ''),
                    'session': stream_entry.get('session', ''),
                    'slot_time': stream_entry.get('slot_time', ''),
                    'enabled': True
                }
        
        return metadata