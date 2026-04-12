"""
Metrics History (Phase 8)

Long-term reliability monitoring: aggregate incidents by week/month,
store rolling metrics in metrics_history.jsonl.
"""
import json
import logging
from datetime import datetime, timezone, timedelta
from pathlib import Path
from typing import Dict, List, Optional

from .config import INCIDENTS_FILE, METRICS_HISTORY_FILE

logger = logging.getLogger(__name__)


def _parse_ts(s: str) -> Optional[datetime]:
    try:
        dt = datetime.fromisoformat(s.replace("Z", "+00:00"))
        if dt.tzinfo is None:
            dt = dt.replace(tzinfo=timezone.utc)
        return dt
    except Exception:
        return None


def _week_start(dt: datetime) -> datetime:
    """Monday 00:00 UTC."""
    return (dt - timedelta(days=dt.weekday())).replace(hour=0, minute=0, second=0, microsecond=0)


def _month_start(dt: datetime) -> datetime:
    """First of month 00:00 UTC."""
    return dt.replace(day=1, hour=0, minute=0, second=0, microsecond=0)


def aggregate_incidents_by_week(incidents_path: Optional[Path] = None) -> List[Dict]:
    """Aggregate incidents by week. Returns list of {week_start, disconnect_incidents, engine_stalls, ...}."""
    path = incidents_path or INCIDENTS_FILE
    if not path.exists():
        return []
    try:
        lines = path.read_text(encoding="utf-8").strip().split("\n")
    except Exception as e:
        logger.warning(f"metrics_history: failed to read incidents: {e}")
        return []

    by_week: Dict[datetime, Dict] = {}
    for line in lines:
        if not line.strip():
            continue
        try:
            rec = json.loads(line)
        except json.JSONDecodeError:
            continue
        start_str = rec.get("start_ts")
        if not start_str:
            continue
        start_dt = _parse_ts(start_str)
        if not start_dt:
            continue
        week = _week_start(start_dt)
        if week not in by_week:
            by_week[week] = {
                "week_start": week.strftime("%Y-%m-%d"),
                "disconnect_incidents": 0,
                "engine_stalls": 0,
                "data_stalls": 0,
                "forced_flatten_count": 0,
                "reconciliation_mismatch_count": 0,
                "total_disconnect_duration_sec": 0,
            }
        t = rec.get("type", "")
        d = rec.get("duration_sec", 0) or 0
        if t == "CONNECTION_LOST":
            by_week[week]["disconnect_incidents"] += 1
            by_week[week]["total_disconnect_duration_sec"] += d
        elif t == "ENGINE_STALLED":
            by_week[week]["engine_stalls"] += 1
        elif t == "DATA_STALL":
            by_week[week]["data_stalls"] += 1
        elif t == "FORCED_FLATTEN":
            by_week[week]["forced_flatten_count"] += 1
        elif t == "RECONCILIATION_QTY_MISMATCH":
            by_week[week]["reconciliation_mismatch_count"] += 1

    return sorted([v for v in by_week.values()], key=lambda x: x["week_start"])


def aggregate_incidents_by_month(incidents_path: Optional[Path] = None) -> List[Dict]:
    """Aggregate incidents by month."""
    path = incidents_path or INCIDENTS_FILE
    if not path.exists():
        return []
    try:
        lines = path.read_text(encoding="utf-8").strip().split("\n")
    except Exception as e:
        logger.warning(f"metrics_history: failed to read incidents: {e}")
        return []

    by_month: Dict[datetime, Dict] = {}
    for line in lines:
        if not line.strip():
            continue
        try:
            rec = json.loads(line)
        except json.JSONDecodeError:
            continue
        start_str = rec.get("start_ts")
        if not start_str:
            continue
        start_dt = _parse_ts(start_str)
        if not start_dt:
            continue
        month = _month_start(start_dt)
        if month not in by_month:
            by_month[month] = {
                "month_start": month.strftime("%Y-%m"),
                "disconnect_incidents": 0,
                "engine_stalls": 0,
                "data_stalls": 0,
                "forced_flatten_count": 0,
                "reconciliation_mismatch_count": 0,
                "total_disconnect_duration_sec": 0,
            }
        t = rec.get("type", "")
        d = rec.get("duration_sec", 0) or 0
        if t == "CONNECTION_LOST":
            by_month[month]["disconnect_incidents"] += 1
            by_month[month]["total_disconnect_duration_sec"] += d
        elif t == "ENGINE_STALLED":
            by_month[month]["engine_stalls"] += 1
        elif t == "DATA_STALL":
            by_month[month]["data_stalls"] += 1
        elif t == "FORCED_FLATTEN":
            by_month[month]["forced_flatten_count"] += 1
        elif t == "RECONCILIATION_QTY_MISMATCH":
            by_month[month]["reconciliation_mismatch_count"] += 1

    return sorted([v for v in by_month.values()], key=lambda x: x["month_start"])


def append_weekly_snapshot(incidents_path: Optional[Path] = None, history_path: Optional[Path] = None) -> None:
    """Compute current week aggregate and append to metrics_history.jsonl. Never throws."""
    try:
        weeks = aggregate_incidents_by_week(incidents_path)
        if not weeks:
            return
        latest = weeks[-1]
        record = {
            "computed_at": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
            "granularity": "week",
            **latest,
        }
        path = history_path or METRICS_HISTORY_FILE
        path.parent.mkdir(parents=True, exist_ok=True)
        with open(path, "a", encoding="utf-8") as f:
            f.write(json.dumps(record, default=str) + "\n")
    except Exception as e:
        logger.debug(f"metrics_history append failed: {e}")


def get_metrics_history(
    limit: int = 52,
    history_path: Optional[Path] = None,
) -> List[Dict]:
    """Read last N entries from metrics_history.jsonl."""
    path = history_path or METRICS_HISTORY_FILE
    if not path.exists():
        return []
    try:
        lines = path.read_text(encoding="utf-8").strip().split("\n")
        lines = [ln for ln in lines if ln.strip()]
        records = []
        for line in reversed(lines[-limit:]):
            try:
                records.append(json.loads(line))
            except json.JSONDecodeError:
                continue
        return list(reversed(records))
    except Exception as e:
        logger.warning(f"metrics_history get failed: {e}")
        return []
