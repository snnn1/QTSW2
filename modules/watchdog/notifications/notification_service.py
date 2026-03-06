"""
Watchdog Notification Service.

Queued, non-blocking alert delivery. Single entry point for all watchdog alerts.
"""
import asyncio
import logging
from collections import deque
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Any, Dict, Optional

from ..alert_ledger import AlertLedger, generate_alert_id
from .pushover_client import pushover_send

logger = logging.getLogger(__name__)

# Alert type to human-readable label
ALERT_TYPE_LABELS = {
    "NINJATRADER_PROCESS_STOPPED": "NinjaTrader Process Stopped",
    "ROBOT_HEARTBEAT_LOST": "Robot Heartbeat Lost",
    "CONNECTION_LOST_SUSTAINED": "Connection Lost Sustained",
    "ENGINE_TICK_STALL": "Engine Tick Stall",
    "POTENTIAL_ORPHAN_POSITION": "Potential Orphan Position",
    "CONFIRMED_ORPHAN_POSITION": "Confirmed Orphan Position",
    "LOG_FILE_STALLED": "Log File Stalled",
}

# Severity tiers: critical, high, warning, info (enables sound/priority/escalation without redesign)
# Pushover priority: 2=critical, 1=high/warning, 0=info
SEVERITY_PRIORITY = {
    "critical": 2,
    "high": 1,
    "warning": 1,
    "info": 0,
}


class NotificationService:
    """Queued notification service with Pushover channel."""

    def __init__(
        self,
        config_path: Optional[Path] = None,
        secrets_path: Optional[Path] = None,
        ledger: Optional[AlertLedger] = None,
    ):
        self._config_path = config_path or (Path(__file__).parent.parent.parent / "configs" / "watchdog" / "notifications.json")
        self._secrets_path = secrets_path or (Path(__file__).parent.parent.parent / "configs" / "watchdog" / "notifications.secrets.json")
        self._ledger = ledger or AlertLedger()
        self._config: Dict[str, Any] = {}
        self._secrets: Dict[str, Any] = {}
        self._enabled = False
        self._queue: asyncio.Queue = asyncio.Queue(maxsize=100)
        self._worker_task: Optional[asyncio.Task] = None
        self._running = False
        self._last_delivery_by_key: Dict[str, datetime] = {}
        self._alerts_this_hour: deque = deque(maxlen=100)
        self._load_config()

    def _load_config(self) -> None:
        """Load config and secrets."""
        try:
            if self._config_path.exists():
                import json
                with open(self._config_path, "r", encoding="utf-8") as f:
                    self._config = json.load(f)
            else:
                self._config = {}
        except Exception as e:
            logger.warning(f"Failed to load notification config: {e}")
            self._config = {}

        try:
            if self._secrets_path.exists():
                import json
                with open(self._secrets_path, "r", encoding="utf-8") as f:
                    self._secrets = json.load(f)
            else:
                self._secrets = {}
        except Exception as e:
            logger.warning(f"Failed to load notification secrets: {e}")
            self._secrets = {}

        pushover_cfg = self._config.get("channels", {}).get("pushover", {})
        pushover_secrets = self._secrets.get("pushover", {})
        self._enabled = (
            self._config.get("enabled", False)
            and pushover_cfg.get("enabled", False)
            and bool(pushover_secrets.get("user_key"))
            and bool(pushover_secrets.get("app_token"))
        )
        if not self._enabled:
            logger.info("Watchdog notifications disabled (config or secrets missing)")

    def raise_alert(
        self,
        alert_type: str,
        severity: str,
        context: Dict[str, Any],
        dedupe_key: str,
        min_resend_interval_seconds: int = 300,
    ) -> None:
        """
        Enqueue an alert for delivery. Non-blocking.
        Dedupe and rate limiting applied before enqueue.
        """
        if not self._enabled:
            return

        # Dedupe: if already active, update last_seen and maybe resend
        if self._ledger.is_alert_active(dedupe_key):
            self._ledger.update_active_alert_last_seen(dedupe_key, context)
            # Check resend interval
            last = self._last_delivery_by_key.get(dedupe_key)
            if last and (datetime.now(timezone.utc) - last).total_seconds() < min_resend_interval_seconds:
                return
            # Resend: put on queue (worker will send again)
            alert = self._ledger.get_active_alert(dedupe_key)
            if alert:
                try:
                    self._queue.put_nowait({
                        "alert_id": alert.get("alert_id"),
                        "alert_type": alert_type,
                        "severity": severity,
                        "context": context,
                        "dedupe_key": dedupe_key,
                        "first_seen_utc": alert.get("first_seen_utc"),
                        "last_seen_utc": datetime.now(timezone.utc).isoformat(),
                    })
                except asyncio.QueueFull:
                    logger.warning("Notification queue full, dropping resend")
            return

        # Rate limit: max alerts per hour
        rate_limits = self._config.get("rate_limits", {})
        max_per_hour = rate_limits.get("max_alerts_per_hour", 20)
        now = datetime.now(timezone.utc)
        hour_ago = now - timedelta(hours=1)
        recent = sum(1 for t in self._alerts_this_hour if t > hour_ago)
        if recent >= max_per_hour:
            logger.warning(f"Alert rate limit reached ({max_per_hour}/hour), dropping {alert_type}")
            return

        # New alert: append to ledger, enqueue
        alert_id = generate_alert_id()
        self._ledger.append_alert(alert_id, alert_type, severity, context, dedupe_key)

        try:
            self._queue.put_nowait({
                "alert_id": alert_id,
                "alert_type": alert_type,
                "severity": severity,
                "context": context,
                "dedupe_key": dedupe_key,
                "first_seen_utc": now.isoformat(),
                "last_seen_utc": now.isoformat(),
            })
        except asyncio.QueueFull:
            logger.warning("Notification queue full, dropping new alert")

    def resolve_alert(self, dedupe_key: str) -> None:
        """Mark alert as resolved. Removes from active set, appends resolution record."""
        self._ledger.resolve_alert(dedupe_key)

    def send_restored_notification(self, title: str, message: str) -> None:
        """
        Send a one-off info notification (e.g. heartbeat restored).
        Does not create a ledger entry. Priority 0 (lowest).
        """
        if not self._enabled:
            return
        restored = self._config.get("restored_notifications", {})
        if restored.get("enabled", True) is False:
            return
        try:
            self._queue.put_nowait({"type": "info", "title": title, "message": message})
        except asyncio.QueueFull:
            logger.debug("Notification queue full, dropping restored notification")

    def is_alert_active(self, dedupe_key: str) -> bool:
        """Check if alert is currently active."""
        return self._ledger.is_alert_active(dedupe_key)

    def get_ledger(self) -> AlertLedger:
        """Get the alert ledger for API/read access."""
        return self._ledger

    def _format_message(self, alert_type: str, severity: str, context: Dict[str, Any], first_seen: str, last_seen: str) -> str:
        """Format message body for Pushover (max ~1024 chars)."""
        label = ALERT_TYPE_LABELS.get(alert_type, alert_type)
        ctx_str = str(context)[:400] if context else ""
        return f"{severity.upper()}: {label}\nContext: {ctx_str}\nFirst seen: {first_seen}\nLast seen: {last_seen}"

    async def _worker_loop(self) -> None:
        """Background worker: dequeue and send."""
        pushover_cfg = self._config.get("channels", {}).get("pushover", {})
        pushover_secrets = self._secrets.get("pushover", {})
        user_key = pushover_secrets.get("user_key", "")
        app_token = pushover_secrets.get("app_token", "")
        retry_count = pushover_cfg.get("retry_count", 2)
        retry_delay = pushover_cfg.get("retry_delay_seconds", 5)
        timeout = pushover_cfg.get("timeout_seconds", 12)
        priority_critical = pushover_cfg.get("priority_critical", 2)
        priority_default = pushover_cfg.get("priority_default", 1)

        while self._running:
            try:
                item = await asyncio.wait_for(self._queue.get(), timeout=1.0)
            except asyncio.TimeoutError:
                continue

            # One-off info notification (restored, etc.) - no ledger
            if item.get("type") == "info":
                title = item.get("title", "[Watchdog] Restored")
                message = item.get("message", "")
                pushover_send(user_key, app_token, title, message, priority=0, timeout=timeout)
                continue

            alert_type = item.get("alert_type", "")
            severity = item.get("severity", "critical")
            context = item.get("context", {})
            dedupe_key = item.get("dedupe_key", "")
            first_seen = item.get("first_seen_utc", "")
            last_seen = item.get("last_seen_utc", "")

            label = ALERT_TYPE_LABELS.get(alert_type, alert_type)
            title = f"[Watchdog] {label}"
            message = self._format_message(alert_type, severity, context, first_seen, last_seen)

            priority = SEVERITY_PRIORITY.get(severity, priority_critical if severity == "critical" else priority_default)

            success = False
            attempts = 0
            for attempt in range(retry_count + 1):
                attempts += 1
                ok, status, err = pushover_send(user_key, app_token, title, message, priority=priority, timeout=timeout)
                if ok:
                    success = True
                    break
                logger.warning(f"Pushover send failed (attempt {attempts}): {err}")
                if attempt < retry_count:
                    await asyncio.sleep(retry_delay)

            self._ledger.update_delivery(
                dedupe_key,
                "sent" if success else "failed",
                "pushover",
                attempts,
            )
            if success:
                self._last_delivery_by_key[dedupe_key] = datetime.now(timezone.utc)
                self._alerts_this_hour.append(datetime.now(timezone.utc))

    async def start(self) -> None:
        """Start the notification worker."""
        if not self._enabled:
            return
        self._running = True
        self._worker_task = asyncio.create_task(self._worker_loop())
        logger.info("Watchdog notification service started")

    async def stop(self) -> None:
        """Stop the notification worker."""
        self._running = False
        if self._worker_task:
            self._worker_task.cancel()
            try:
                await self._worker_task
            except asyncio.CancelledError:
                pass
            self._worker_task = None
        logger.info("Watchdog notification service stopped")
