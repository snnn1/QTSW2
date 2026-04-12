"""
Alert Engine (Phase 4)

Incident-based alerts with rules, thresholds, and cooldowns.
Called when incident_recorder writes a completed incident.
Uses existing NotificationService for delivery (Pushover).
"""
import json
import logging
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Dict, Optional

from .config import ALERTS_CONFIG_PATH

logger = logging.getLogger(__name__)


def _load_config(path: Optional[Path] = None) -> Dict[str, Any]:
    """Load alerts config."""
    p = path or ALERTS_CONFIG_PATH
    try:
        if p.exists():
            with open(p, "r", encoding="utf-8") as f:
                return json.load(f)
    except Exception as e:
        logger.warning(f"Failed to load alerts config: {e}")
    return {"enabled": True, "rules": {}}


def _is_market_hours() -> bool:
    """Check if currently during market hours (simplified: 8:30-16:00 Chicago)."""
    try:
        import pytz
        chicago = pytz.timezone("America/Chicago")
        now = datetime.now(chicago).time()
        from datetime import time
        open_t = time(8, 30)
        close_t = time(16, 0)
        return open_t <= now <= close_t
    except Exception:
        return True  # Default to True if we can't determine


def on_incident(
    record: Dict[str, Any],
    notification_service: Optional[Any],
    config_path: Optional[Path] = None,
) -> None:
    """
    Called when a completed incident is written. Check rules and optionally send alert.
    Never throws. notification_service may be None (e.g. not yet initialized).
    """
    if not notification_service:
        return
    try:
        config = _load_config(config_path)
        if not config.get("enabled", True):
            return

        incident_type = record.get("type", "")
        duration_sec = record.get("duration_sec", 0) or 0
        # Phase 5: Only alert for primary incidents (suppress cascaded/secondary)
        if record.get("primary") is False:
            logger.debug(f"Skipping alert for secondary incident {incident_type} (root_cause={record.get('root_cause')})")
            return
        rules = config.get("rules", {})
        rule = rules.get(incident_type)
        if not rule:
            return

        min_duration = rule.get("min_duration_seconds", 0)
        if duration_sec < min_duration:
            return

        during_market_only = rule.get("during_market_hours_only", False)
        if during_market_only and not _is_market_hours():
            return

        # Cooldown disabled: every alert passes through (fail-loud for live trading)
        severity = rule.get("severity", "high")
        instruments = record.get("instruments", [])
        inst_str = ", ".join(instruments[:5]) if instruments else "—"
        if len(instruments) > 5:
            inst_str += f" (+{len(instruments) - 5})"

        context = {
            "incident_id": record.get("incident_id"),
            "duration_sec": duration_sec,
            "instruments": instruments,
            "start_ts": record.get("start_ts"),
            "end_ts": record.get("end_ts"),
        }

        alert_type = f"INCIDENT_{incident_type}"
        dedupe_key = f"INCIDENT_{incident_type}_{record.get('incident_id', '')}"

        notification_service.raise_alert(
            alert_type=alert_type,
            severity=severity,
            context=context,
            dedupe_key=dedupe_key,
            min_resend_interval_seconds=0,  # No cooldown: fail-loud
        )

        logger.info(f"Incident alert sent: {incident_type} duration={duration_sec}s")
    except Exception as e:
        logger.debug(f"Alert engine on_incident error: {e}")
