"""
Phase 9: Incident replay helpers.

- Tail-only read: read last N bytes from feed to avoid full-file scan.
- No early break: scan all lines (handles out-of-order events).
"""
import json
from datetime import datetime, timezone, timedelta
from pathlib import Path
from typing import Dict, List, Optional

from .config import FRONTEND_FEED_FILE, REPLAY_TAIL_BYTES


def _parse_ev_dt(ev: Dict) -> Optional[datetime]:
    """Parse event timestamp. Returns None if missing/invalid."""
    ts_str = ev.get("timestamp_utc") or ev.get("ts_utc") or ev.get("timestamp")
    if not ts_str:
        return None
    try:
        dt = datetime.fromisoformat(str(ts_str).replace("Z", "+00:00"))
        if dt.tzinfo is None:
            dt = dt.replace(tzinfo=timezone.utc)
        return dt
    except Exception:
        return None


def load_incident_events(
    window_start: datetime,
    window_end: datetime,
    feed_path: Optional[Path] = None,
    tail_bytes: Optional[int] = None,
) -> List[Dict]:
    """
    Load events from frontend_feed.jsonl in [window_start, window_end].

    Phase 9: Reads only last tail_bytes (default 20MB) from file end to avoid
    scanning entire log. Uses continue instead of break to handle out-of-order events.
    """
    path = feed_path or FRONTEND_FEED_FILE
    tail = tail_bytes if tail_bytes is not None else REPLAY_TAIL_BYTES
    events: List[Dict] = []

    if not path.exists():
        return []

    try:
        with open(path, "rb") as f:
            size = f.seek(0, 2)
            start_pos = max(0, size - tail)
            f.seek(start_pos)
            data = f.read()
        text = data.decode("utf-8", errors="replace")
        lines = text.split("\n")
        if start_pos > 0 and lines:
            lines = lines[1:]  # Skip partial first line
    except Exception:
        return []

    for line in lines:
        line = line.strip() if isinstance(line, str) else str(line)
        if not line:
            continue
        try:
            ev = json.loads(line)
        except json.JSONDecodeError:
            continue
        ev_dt = _parse_ev_dt(ev)
        if ev_dt is None:
            continue
        if window_start <= ev_dt <= window_end:
            events.append(ev)
        # Phase 9: No break on ev_dt > window_end - continue to handle out-of-order events

    events.sort(key=lambda e: (_parse_ev_dt(e) or datetime.min.replace(tzinfo=timezone.utc)).timestamp())
    return events
