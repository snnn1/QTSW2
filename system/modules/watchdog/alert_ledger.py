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

logger = logging.getLogger(__name__)

# Default retention: 30 days (debugging, reliability review, missed-trade investigation)
DEFAULT_RETENTION_DAYS = 30


class AlertLedger:
    """Append-only alert ledger with resolution records and retention."""

    def __init__(self, ledger_path: Optional[Path] = None, retention_days: int = DEFAULT_RETENTION_DAYS):
        self._ledger_path = ledger_path or (Path(__file__).parent.parent.parent / "data" / "watchdog" / "alert_ledger.jsonl")
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
        Read recent records from ledger for API.
        Parses file from end, returns in chronological order.
        """
        if not self._ledger_path.exists():
            return []
        records: List[Dict[str, Any]] = []
        cutoff = None
        if since_hours is not None:
            cutoff = datetime.now(timezone.utc) - timedelta(hours=since_hours)
        try:
            with open(self._ledger_path, "r", encoding="utf-8") as f:
                lines = f.readlines()
            # Process from end (most recent last)
            for line in reversed(lines):
                line = line.strip()
                if not line:
                    continue
                try:
                    rec = json.loads(line)
                except json.JSONDecodeError:
                    continue
                if "event" in rec:
                    # Resolution or delivery update - include for history
                    if active_only:
                        continue
                    ts = rec.get("resolved_utc") or rec.get("updated_utc")
                    if ts and cutoff:
                        try:
                            dt = datetime.fromisoformat(ts.replace("Z", "+00:00"))
                            if dt.tzinfo is None:
                                dt = dt.replace(tzinfo=timezone.utc)
                            if dt < cutoff:
                                continue
                        except (ValueError, TypeError):
                            pass
                    records.append(rec)
                else:
                    # Full alert record
                    if active_only and not rec.get("active", True):
                        continue
                    first_seen = rec.get("first_seen_utc")
                    if first_seen and cutoff:
                        try:
                            dt = datetime.fromisoformat(first_seen.replace("Z", "+00:00"))
                            if dt.tzinfo is None:
                                dt = dt.replace(tzinfo=timezone.utc)
                            if dt < cutoff:
                                continue
                        except (ValueError, TypeError):
                            pass
                    records.append(rec)
                if len(records) >= limit:
                    break
        except Exception as e:
            logger.error(f"Failed to read alert ledger: {e}", exc_info=True)
        records.reverse()
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
