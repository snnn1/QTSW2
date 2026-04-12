"""
Reliability Metrics

Computes statistics from incidents.jsonl for connection, engine, data, forced flatten,
and reconciliation incidents. Read-only; optionally cached with TTL.
"""
import json
import logging
from datetime import datetime, timezone, timedelta
from pathlib import Path
from typing import Dict, List, Optional

from .config import INCIDENTS_FILE

logger = logging.getLogger(__name__)

# Cache TTL seconds (5 min)
_METRICS_CACHE_TTL_SECONDS = 300
_metrics_cache: Optional[Dict] = None
_cache_timestamp: Optional[datetime] = None


def _parse_ts(s: str) -> Optional[datetime]:
    """Parse ISO timestamp string."""
    try:
        dt = datetime.fromisoformat(s.replace("Z", "+00:00"))
        if dt.tzinfo is None:
            dt = dt.replace(tzinfo=timezone.utc)
        return dt
    except Exception:
        return None


def _load_incidents_in_window(
    incidents_path: Path,
    window_start: datetime,
    window_end: datetime,
) -> List[Dict]:
    """Load incidents from file that fall within [window_start, window_end]."""
    if not incidents_path.exists():
        return []
    try:
        lines = incidents_path.read_text(encoding="utf-8").strip().split("\n")
        lines = [ln for ln in lines if ln.strip()]
    except Exception as e:
        logger.warning(f"reliability_metrics: failed to read incidents: {e}")
        return []

    records: List[Dict] = []
    for line in lines:
        try:
            rec = json.loads(line)
        except json.JSONDecodeError:
            continue
        start_str = rec.get("start_ts")
        if not start_str:
            continue
        start_ts = _parse_ts(start_str)
        if not start_ts:
            continue
        if window_start <= start_ts <= window_end:
            records.append(rec)
    return records


def get_reliability_metrics(
    window_hours: float = 24,
    incidents_path: Optional[Path] = None,
    use_cache: bool = True,
) -> Dict:
    """
    Compute reliability metrics from incidents.jsonl for the last window_hours.

    Returns dict with:
    - connection: disconnect_incidents, avg_disconnect_duration, max_disconnect_duration, uptime_percent
    - engine: engine_stalls, avg_stall_duration, max_stall_duration
    - data: data_stalls, avg_data_stall_duration
    - forced_flatten: forced_flatten_count
    - reconciliation: reconciliation_mismatch_count
    """
    global _metrics_cache, _cache_timestamp
    path = incidents_path or INCIDENTS_FILE

    if use_cache and _metrics_cache is not None and _cache_timestamp is not None:
        age = (datetime.now(timezone.utc) - _cache_timestamp).total_seconds()
        if age < _METRICS_CACHE_TTL_SECONDS:
            return dict(_metrics_cache)

    now = datetime.now(timezone.utc)
    window_start = now - timedelta(hours=window_hours)
    incidents = _load_incidents_in_window(path, window_start, now)
    window_seconds = window_hours * 3600

    # Connection metrics
    conn_incidents = [i for i in incidents if i.get("type") == "CONNECTION_LOST"]
    conn_durations = [
        max(0, i.get("duration_sec", 0))
        for i in conn_incidents
        if isinstance(i.get("duration_sec"), (int, float))
    ]
    total_conn_duration = sum(conn_durations)
    uptime_percent = 100.0 * (1 - total_conn_duration / window_seconds) if window_seconds > 0 else 100.0
    uptime_percent = max(0.0, min(100.0, uptime_percent))

    # Engine metrics
    engine_incidents = [i for i in incidents if i.get("type") == "ENGINE_STALLED"]
    engine_durations = [
        max(0, i.get("duration_sec", 0))
        for i in engine_incidents
        if isinstance(i.get("duration_sec"), (int, float))
    ]

    # Data metrics
    data_incidents = [i for i in incidents if i.get("type") == "DATA_STALL"]
    data_durations = [
        max(0, i.get("duration_sec", 0))
        for i in data_incidents
        if isinstance(i.get("duration_sec"), (int, float))
    ]

    # Forced flatten
    forced_flatten_incidents = [i for i in incidents if i.get("type") == "FORCED_FLATTEN"]

    # Reconciliation
    recon_incidents = [i for i in incidents if i.get("type") == "RECONCILIATION_QTY_MISMATCH"]

    result = {
        "connection": {
            "disconnect_incidents": len(conn_incidents),
            "avg_disconnect_duration": total_conn_duration / len(conn_incidents) if conn_incidents else 0,
            "max_disconnect_duration": max(conn_durations) if conn_durations else 0,
            "uptime_percent": round(uptime_percent, 2),
        },
        "engine": {
            "engine_stalls": len(engine_incidents),
            "avg_stall_duration": sum(engine_durations) / len(engine_incidents) if engine_incidents else 0,
            "max_stall_duration": max(engine_durations) if engine_durations else 0,
        },
        "data": {
            "data_stalls": len(data_incidents),
            "avg_data_stall_duration": sum(data_durations) / len(data_incidents) if data_incidents else 0,
        },
        "forced_flatten": {
            "forced_flatten_count": len(forced_flatten_incidents),
        },
        "reconciliation": {
            "reconciliation_mismatch_count": len(recon_incidents),
        },
        "window_hours": window_hours,
        "window_start": window_start.strftime("%Y-%m-%dT%H:%M:%SZ"),
        "window_end": now.strftime("%Y-%m-%dT%H:%M:%SZ"),
    }

    _metrics_cache = result
    _cache_timestamp = now
    return result
