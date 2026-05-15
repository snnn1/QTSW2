"""
Persistent Alert Ledger for Watchdog Phase 1.

Append-only JSONL storage for alert audit trail.
Active alerts tracked in-memory; resolution written as separate record.
"""
import json
import logging
import uuid
from datetime import datetime, timezone, timedelta
from pathlib import Path
from typing import Any, Dict, List, Optional

from .config import ALERT_LEDGER_PATH

logger = logging.getLogger(__name__)

# Default retention: 30 days (debugging, reliability review, missed-trade investigation)
DEFAULT_RETENTION_DAYS = 30


def _is_completed_incident_alert(record: Dict[str, Any]) -> bool:
    """Incident alerts are notifications about completed episodes, not active latches."""
    alert_type = str(record.get("alert_type") or "")
    if not alert_type.startswith("INCIDENT_"):
        return False
    context = record.get("context")
    return isinstance(context, dict) and bool(context.get("end_ts"))


def _parse_utc_timestamp(value: Any) -> Optional[datetime]:
    if not value:
        return None
    try:
        dt = datetime.fromisoformat(str(value).replace("Z", "+00:00"))
        if dt.tzinfo is None:
            dt = dt.replace(tzinfo=timezone.utc)
        return dt.astimezone(timezone.utc)
    except (TypeError, ValueError):
        return None


def _effective_alert_timestamp(record: Dict[str, Any]) -> Optional[datetime]:
    candidates = [
        _parse_utc_timestamp(record.get("resolved_utc")),
        _parse_utc_timestamp(record.get("last_delivery_utc")),
        _parse_utc_timestamp(record.get("last_seen_utc")),
        _parse_utc_timestamp(record.get("first_seen_utc")),
        _parse_utc_timestamp(record.get("_ledger_updated_utc")),
    ]
    candidates = [dt for dt in candidates if dt is not None]
    return max(candidates) if candidates else None


class AlertLedger:
    """Append-only alert ledger with resolution records and retention."""

    def __init__(self, ledger_path: Optional[Path] = None, retention_days: int = DEFAULT_RETENTION_DAYS):
        self._ledger_path = ledger_path or ALERT_LEDGER_PATH
        self._retention_days = retention_days
        self._active_alerts: Dict[str, Dict[str, Any]] = {}  # dedupe_key -> alert record
        self._rehydrate_active_alerts()

    def _rehydrate_active_alerts(self) -> None:
        """
        Phase 2: Restore active alerts from ledger on startup.
        Alerts without a resolution record are treated as still active.
        """
        if not self._ledger_path.exists():
            return
        cutoff = datetime.now(timezone.utc) - timedelta(days=self._retention_days)
        resolved_keys: set = set()
        try:
            with open(self._ledger_path, "r", encoding="utf-8") as f:
                for line in f:
                    line = line.strip()
                    if not line:
                        continue
                    try:
                        rec = json.loads(line)
                    except json.JSONDecodeError:
                        continue
                    if not isinstance(rec, dict):
                        continue
                    if rec.get("event") == "resolved":
                        dedupe_key = rec.get("dedupe_key")
                        if dedupe_key:
                            resolved_keys.add(dedupe_key)
                            self._active_alerts.pop(dedupe_key, None)
                    elif "dedupe_key" in rec and "event" not in rec:
                        # Full alert record
                        dedupe_key = rec.get("dedupe_key")
                        first_seen = rec.get("first_seen_utc")
                        if not dedupe_key:
                            continue
                        if first_seen:
                            try:
                                dt = datetime.fromisoformat(first_seen.replace("Z", "+00:00"))
                                if dt.tzinfo is None:
                                    dt = dt.replace(tzinfo=timezone.utc)
                                if dt < cutoff:
                                    continue
                            except (ValueError, TypeError):
                                pass
                        if _is_completed_incident_alert(rec):
                            continue
                        if dedupe_key not in resolved_keys:
                            self._active_alerts[dedupe_key] = rec.copy()
            if self._active_alerts:
                logger.info(f"Rehydrated {len(self._active_alerts)} active alert(s) from ledger")
        except Exception as e:
            logger.warning(f"Failed to rehydrate active alerts: {e}", exc_info=True)

    def _ensure_dir(self) -> None:
        self._ledger_path.parent.mkdir(parents=True, exist_ok=True)

    def _append_line(self, record: Dict[str, Any]) -> None:
        """Append a single JSON line to the ledger file."""
        self._ensure_dir()
        line = json.dumps(record, default=str) + "\n"
        try:
            with open(self._ledger_path, "a", encoding="utf-8") as f:
                f.write(line)
        except Exception as e:
            logger.error(f"Failed to append to alert ledger: {e}", exc_info=True)

    def append_alert(
        self,
        alert_id: str,
        alert_type: str,
        severity: str,
        context: Dict[str, Any],
        dedupe_key: str,
    ) -> None:
        """Append a new alert record. Caller provides alert_id (UUID)."""
        now = datetime.now(timezone.utc)
        record = {
            "alert_id": alert_id,
            "alert_type": alert_type,
            "severity": severity,
            "first_seen_utc": now.isoformat(),
            "last_seen_utc": now.isoformat(),
            "active": True,
            "resolved_utc": None,
            "dedupe_key": dedupe_key,
            "context": context,
            "delivery_status": "pending",
            "delivery_channel": None,
            "delivery_attempts": 0,
            "last_delivery_utc": None,
        }
        self._append_line(record)
        if _is_completed_incident_alert(record):
            resolution_record = {
                "event": "resolved",
                "alert_id": alert_id,
                "alert_type": alert_type,
                "dedupe_key": dedupe_key,
                "resolved_utc": now.isoformat(),
                "reason": "completed_incident_notification",
            }
            self._append_line(resolution_record)
        else:
            self._active_alerts[dedupe_key] = record.copy()

    def update_delivery(
        self,
        dedupe_key: str,
        delivery_status: str,
        delivery_channel: str,
        delivery_attempts: int,
    ) -> None:
        """Update delivery status for active alert. Writes resolution-style update record."""
        if dedupe_key not in self._active_alerts:
            return
        now = datetime.now(timezone.utc)
        alert = self._active_alerts[dedupe_key]
        alert["delivery_status"] = delivery_status
        alert["delivery_channel"] = delivery_channel
        alert["delivery_attempts"] = delivery_attempts
        alert["last_delivery_utc"] = now.isoformat()
        # Append update record (simplified: we don't rewrite; we append delivery update)
        update_record = {
            "event": "delivery_update",
            "alert_id": alert.get("alert_id"),
            "dedupe_key": dedupe_key,
            "delivery_status": delivery_status,
            "delivery_channel": delivery_channel,
            "delivery_attempts": delivery_attempts,
            "updated_utc": now.isoformat(),
        }
        self._append_line(update_record)

    def resolve_alert(self, dedupe_key: str) -> None:
        """Mark alert as resolved. Append resolution record, remove from active set."""
        if dedupe_key not in self._active_alerts:
            return
        now = datetime.now(timezone.utc)
        alert = self._active_alerts[dedupe_key]
        resolution_record = {
            "event": "resolved",
            "alert_id": alert.get("alert_id"),
            "alert_type": alert.get("alert_type"),
            "dedupe_key": dedupe_key,
            "resolved_utc": now.isoformat(),
        }
        self._append_line(resolution_record)
        del self._active_alerts[dedupe_key]

    def get_active_alert(self, dedupe_key: str) -> Optional[Dict[str, Any]]:
        """Get active alert by dedupe_key."""
        return self._active_alerts.get(dedupe_key)

    def is_alert_active(self, dedupe_key: str) -> bool:
        """Check if alert is currently active."""
        return dedupe_key in self._active_alerts

    def update_active_alert_last_seen(self, dedupe_key: str, context: Optional[Dict[str, Any]] = None) -> None:
        """Update last_seen_utc and optionally context for active alert."""
        if dedupe_key not in self._active_alerts:
            return
        now = datetime.now(timezone.utc)
        self._active_alerts[dedupe_key]["last_seen_utc"] = now.isoformat()
        if context is not None:
            self._active_alerts[dedupe_key]["context"] = context

    def get_active_alerts(self) -> List[Dict[str, Any]]:
        """Return list of active alert records."""
        return list(self._active_alerts.values())

    def read_recent(
        self,
        active_only: bool = False,
        since_hours: Optional[float] = None,
        limit: int = 200,
    ) -> List[Dict[str, Any]]:
        """
        Read effective alert records from ledger for API.

        The ledger is append-only and stores resolution/delivery updates as
        separate rows. Operator views should not see an old full alert row as
        still active after a later resolved row exists, so this folds update
        rows into the latest full alert record before filtering.
        """
        if not self._ledger_path.exists():
            return []
        cutoff = None
        if since_hours is not None:
            cutoff = datetime.now(timezone.utc) - timedelta(hours=since_hours)
        records_by_alert_id: Dict[str, Dict[str, Any]] = {}
        latest_alert_id_by_dedupe: Dict[str, str] = {}
        fallback_index = 0
        try:
            with open(self._ledger_path, "r", encoding="utf-8") as f:
                lines = f.readlines()
            for line in lines:
                line = line.strip()
                if not line:
                    continue
                try:
                    rec = json.loads(line)
                except json.JSONDecodeError:
                    continue
                if not isinstance(rec, dict):
                    continue
                if "event" in rec:
                    dedupe_key = str(rec.get("dedupe_key") or "")
                    alert_id = rec.get("alert_id") or latest_alert_id_by_dedupe.get(dedupe_key)
                    if not alert_id:
                        continue
                    alert = records_by_alert_id.get(str(alert_id))
                    if alert is None:
                        continue
                    event_type = rec.get("event")
                    if event_type == "resolved":
                        alert["active"] = False
                        alert["resolved_utc"] = rec.get("resolved_utc")
                        if rec.get("reason"):
                            alert["resolution_reason"] = rec.get("reason")
                        alert["_ledger_updated_utc"] = rec.get("resolved_utc")
                    elif event_type == "delivery_update":
                        alert["delivery_status"] = rec.get("delivery_status")
                        alert["delivery_channel"] = rec.get("delivery_channel")
                        alert["delivery_attempts"] = rec.get("delivery_attempts")
                        alert["last_delivery_utc"] = rec.get("updated_utc")
                        alert["_ledger_updated_utc"] = rec.get("updated_utc")
                    continue

                alert_id = str(rec.get("alert_id") or "")
                if not alert_id:
                    fallback_index += 1
                    alert_id = f"ledger-row-{fallback_index}"
                alert = rec.copy()
                records_by_alert_id[alert_id] = alert
                dedupe_key = str(alert.get("dedupe_key") or "")
                if dedupe_key:
                    latest_alert_id_by_dedupe[dedupe_key] = alert_id
        except Exception as e:
            logger.error(f"Failed to read alert ledger: {e}", exc_info=True)

        records = list(records_by_alert_id.values())
        if active_only:
            records = [rec for rec in records if rec.get("active", True)]
        if cutoff is not None:
            records = [
                rec for rec in records
                if (_effective_alert_timestamp(rec) is None or _effective_alert_timestamp(rec) >= cutoff)
            ]
        records.sort(key=lambda rec: _effective_alert_timestamp(rec) or datetime.min.replace(tzinfo=timezone.utc))
        if limit > 0:
            records = records[-limit:]
        for rec in records:
            rec.pop("_ledger_updated_utc", None)
        return records

    def trim_old_records(self) -> int:
        """Remove lines older than retention period. Rewrites file."""
        if not self._ledger_path.exists():
            return 0
        cutoff = datetime.now(timezone.utc) - timedelta(days=self._retention_days)
        kept: List[str] = []
        removed = 0
        try:
            with open(self._ledger_path, "r", encoding="utf-8") as f:
                for line in f:
                    line = line.strip()
                    if not line:
                        continue
                    try:
                        rec = json.loads(line)
                    except json.JSONDecodeError:
                        kept.append(line)
                        continue
                    ts = rec.get("first_seen_utc") or rec.get("resolved_utc") or rec.get("updated_utc")
                    if ts:
                        try:
                            dt = datetime.fromisoformat(ts.replace("Z", "+00:00"))
                            if dt.tzinfo is None:
                                dt = dt.replace(tzinfo=timezone.utc)
                            if dt < cutoff:
                                removed += 1
                                continue
                        except (ValueError, TypeError):
                            pass
                    kept.append(line)
            if removed > 0:
                with open(self._ledger_path, "w", encoding="utf-8") as f:
                    for line in kept:
                        f.write(line + "\n")
        except Exception as e:
            logger.error(f"Failed to trim alert ledger: {e}", exc_info=True)
        return removed


def generate_alert_id() -> str:
    """Generate a new UUID for an alert."""
    return str(uuid.uuid4())
