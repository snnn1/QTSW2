"""
Incident Recorder

Tracks operational incidents (CONNECTION_LOST, ENGINE_STALLED, DATA_STALL, etc.)
as structured start/end events with duration. Purely observational - does not
modify existing monitoring logic.

Writes to data/watchdog/incidents.jsonl (append-only).
"""
import json
import logging
from datetime import datetime, timezone
from pathlib import Path
from typing import Dict, List, Optional, Set
from uuid import uuid4

from .config import ACTIVE_INCIDENTS_FILE, INCIDENTS_FILE

logger = logging.getLogger(__name__)

# Incident start events -> incident type
INCIDENT_START_EVENTS: Dict[str, str] = {
    "CONNECTION_LOST": "CONNECTION_LOST",
    "CONNECTION_LOST_SUSTAINED": "CONNECTION_LOST",
    "ENGINE_TICK_STALL_DETECTED": "ENGINE_STALLED",
    "DATA_LOSS_DETECTED": "DATA_STALL",
    "DATA_STALL_DETECTED": "DATA_STALL",
    "FORCED_FLATTEN_TRIGGERED": "FORCED_FLATTEN",
    "RECONCILIATION_QTY_MISMATCH": "RECONCILIATION_QTY_MISMATCH",
    # Instant incidents (same event starts and ends)
    "FORCED_FLATTEN_FAILED": "FORCED_FLATTEN_FAILED",
    "DUPLICATE_INSTANCE_DETECTED": "DUPLICATE_INSTANCE_DETECTED",
    "EXECUTION_POLICY_VALIDATION_FAILED": "EXECUTION_POLICY_VALIDATION_FAILED",
    "EXECUTION_JOURNAL_CORRUPTION": "EXECUTION_JOURNAL_CORRUPTION",
    "EXECUTION_JOURNAL_ERROR": "EXECUTION_JOURNAL_ERROR",
}

# Phase 9: Incident type -> severity (for dashboard coloring)
INCIDENT_SEVERITY: Dict[str, str] = {
    "CONNECTION_LOST": "WARNING",
    "ENGINE_STALLED": "CRITICAL",
    "DATA_STALL": "WARNING",
    "FORCED_FLATTEN": "CRITICAL",
    "RECONCILIATION_QTY_MISMATCH": "WARNING",
    "FORCED_FLATTEN_FAILED": "CRITICAL",
    "DUPLICATE_INSTANCE_DETECTED": "CRITICAL",
    "EXECUTION_POLICY_VALIDATION_FAILED": "CRITICAL",
    "EXECUTION_JOURNAL_CORRUPTION": "CRITICAL",
    "EXECUTION_JOURNAL_ERROR": "CRITICAL",
}

# Recovery events -> incident type to end
INCIDENT_END_EVENTS: Dict[str, str] = {
    "CONNECTION_RECOVERED": "CONNECTION_LOST",
    "CONNECTION_RECOVERED_NOTIFICATION": "CONNECTION_LOST",
    "CONNECTION_CONFIRMED": "CONNECTION_LOST",  # NinjaTrader restart = connection restored
    "ENGINE_ALIVE": "ENGINE_STALLED",
    "ENGINE_TICK_STALL_RECOVERED": "ENGINE_STALLED",
    "ENGINE_TICK_CALLSITE": "ENGINE_STALLED",
    "ENGINE_TIMER_HEARTBEAT": "ENGINE_STALLED",
    "DATA_STALL_RECOVERED": "DATA_STALL",
    "FORCED_FLATTEN_POSITION_CLOSED": "FORCED_FLATTEN",
    "SESSION_FORCED_FLATTENED": "FORCED_FLATTEN",
    "RECONCILIATION_PASS_SUMMARY": "RECONCILIATION_QTY_MISMATCH",
    # Instant incidents (same event starts and ends)
    "FORCED_FLATTEN_FAILED": "FORCED_FLATTEN_FAILED",
    "DUPLICATE_INSTANCE_DETECTED": "DUPLICATE_INSTANCE_DETECTED",
    "EXECUTION_POLICY_VALIDATION_FAILED": "EXECUTION_POLICY_VALIDATION_FAILED",
    "EXECUTION_JOURNAL_CORRUPTION": "EXECUTION_JOURNAL_CORRUPTION",
    "EXECUTION_JOURNAL_ERROR": "EXECUTION_JOURNAL_ERROR",
}


def _extract_instruments(event: Dict) -> Set[str]:
    """Extract instrument identifiers from event payload."""
    instruments: Set[str] = set()
    data = event.get("data") or {}
    if not isinstance(data, dict):
        return instruments
    for key in ("execution_instrument_full_name", "instrument", "execution_instrument_key"):
        val = data.get(key) or event.get(key)
        if val and isinstance(val, str) and val.strip():
            instruments.add(val.strip())
    return instruments


class IncidentRecorder:
    """
    Tracks incident start/end and writes completed incidents to incidents.jsonl.
    Phase 9: Persists active incidents to disk for restart safety.
    Never throws, never blocks. Fail silently on disk write errors.
    """

    def __init__(self, incidents_path: Optional[Path] = None, active_incidents_path: Optional[Path] = None):
        self._incidents_path = incidents_path or INCIDENTS_FILE
        self._active_incidents_path = active_incidents_path or ACTIVE_INCIDENTS_FILE
        self._active_incidents: Dict[str, Dict] = {}  # incident_type -> {incident_id, start_ts, instruments}
        self._load_active_incidents()

    def _load_active_incidents(self) -> None:
        """Phase 9: Load active incidents from disk on startup. Never throws."""
        try:
            if not self._active_incidents_path.exists():
                return
            data = json.loads(self._active_incidents_path.read_text(encoding="utf-8"))
            if not isinstance(data, dict):
                return
            for incident_type, info in data.items():
                if not isinstance(info, dict):
                    continue
                start_str = info.get("start_ts")
                if not start_str:
                    continue
                try:
                    ts = datetime.fromisoformat(start_str.replace("Z", "+00:00"))
                    if ts.tzinfo is None:
                        ts = ts.replace(tzinfo=timezone.utc)
                except Exception:
                    continue
                instruments = info.get("instruments", [])
                inst_set: Set[str] = set(i for i in instruments if isinstance(i, str))
                self._active_incidents[incident_type] = {
                    "incident_id": str(info.get("incident_id", uuid4())),
                    "start_ts": ts,
                    "instruments": inst_set,
                }
            if self._active_incidents:
                logger.info(f"Loaded {len(self._active_incidents)} active incident(s) from disk")
        except Exception as e:
            logger.debug(f"IncidentRecorder._load_active_incidents failed: {e}")

    def _save_active_incidents(self) -> None:
        """Phase 9: Persist active incidents to disk. Never throws."""
        try:
            self._active_incidents_path.parent.mkdir(parents=True, exist_ok=True)
            data: Dict[str, Dict] = {}
            for incident_type, info in self._active_incidents.items():
                start_ts = info.get("start_ts")
                if start_ts is None:
                    continue
                data[incident_type] = {
                    "incident_id": info.get("incident_id"),
                    "start_ts": start_ts.isoformat() if hasattr(start_ts, "isoformat") else str(start_ts),
                    "instruments": sorted(info.get("instruments", [])),
                }
            tmp = self._active_incidents_path.with_suffix(".json.tmp")
            tmp.write_text(json.dumps(data, indent=2), encoding="utf-8")
            tmp.replace(self._active_incidents_path)
        except Exception as e:
            logger.debug(f"IncidentRecorder._save_active_incidents failed: {e}")

    def process_event(self, event: Dict) -> None:
        """
        Observe event and update incident state. Call after each event is processed.
        Does not modify event or any external state.
        """
        try:
            event_type = event.get("event_type", "")
            timestamp_utc_str = event.get("timestamp_utc", "")
            if not event_type or not timestamp_utc_str:
                return

            try:
                ts = datetime.fromisoformat(timestamp_utc_str.replace("Z", "+00:00"))
                if ts.tzinfo is None:
                    ts = ts.replace(tzinfo=timezone.utc)
            except Exception:
                return

            instruments = _extract_instruments(event)

            # Check for start
            if event_type in INCIDENT_START_EVENTS:
                incident_type = INCIDENT_START_EVENTS[event_type]
                if incident_type in self._active_incidents:
                    return  # Already active, do nothing
                incident_id = str(uuid4())
                self._active_incidents[incident_type] = {
                    "incident_id": incident_id,
                    "start_ts": ts,
                    "instruments": instruments,
                }
                self._save_active_incidents()
                return

            # Check for end
            if event_type in INCIDENT_END_EVENTS:
                incident_type = INCIDENT_END_EVENTS[event_type]
                active = self._active_incidents.pop(incident_type, None)
                if not active:
                    return
                start_ts = active["start_ts"]
                end_ts = ts
                duration_sec = int((end_ts - start_ts).total_seconds())
                instruments_list = sorted(active["instruments"] | instruments)

                record = {
                    "incident_id": active["incident_id"],
                    "type": incident_type,
                    "severity": INCIDENT_SEVERITY.get(incident_type, "WARNING"),
                    "start_ts": start_ts.strftime("%Y-%m-%dT%H:%M:%SZ"),
                    "end_ts": end_ts.strftime("%Y-%m-%dT%H:%M:%SZ"),
                    "duration_sec": duration_sec,
                    "instruments": instruments_list,
                }
                # Phase 5: Tag primary vs secondary (cascade correlation)
                from .incident_correlator import tag_incident_record
                active_types = set(self._active_incidents.keys())
                tag_incident_record(record, active_types)
                self._write_incident(record)
                self._save_active_incidents()
        except Exception as e:
            logger.debug(f"IncidentRecorder.process_event swallowed error: {e}")

    def set_on_incident_callback(self, callback) -> None:
        """Set callback invoked after each incident is written (Phase 4 alert integration)."""
        self._on_incident_callback = callback

    def _write_incident(self, record: Dict) -> None:
        """Append incident to incidents.jsonl. Never throws."""
        try:
            self._incidents_path.parent.mkdir(parents=True, exist_ok=True)
            line = json.dumps(record, default=str) + "\n"
            with open(self._incidents_path, "a", encoding="utf-8") as f:
                f.write(line)
            # Phase 4: Invoke alert engine callback if set
            cb = getattr(self, "_on_incident_callback", None)
            if cb:
                try:
                    cb(record)
                except Exception as e:
                    logger.warning(f"Incident callback error (alert may not have been sent): {e}")
        except Exception as e:
            logger.debug(f"IncidentRecorder._write_incident failed: {e}")

    def get_active_incidents(self) -> Dict[str, Dict]:
        """Return copy of active incidents (for API)."""
        try:
            return {k: dict(v) for k, v in self._active_incidents.items()}
        except Exception:
            return {}

    def get_incident_by_id(self, incident_id: str) -> Optional[Dict]:
        """Find incident by id. Returns None if not found (Phase 7)."""
        try:
            if not self._incidents_path.exists():
                return None
            lines = self._incidents_path.read_text(encoding="utf-8").strip().split("\n")
            for line in lines:
                if not line.strip():
                    continue
                try:
                    rec = json.loads(line)
                    if rec.get("incident_id") == incident_id:
                        return rec
                except json.JSONDecodeError:
                    continue
            return None
        except Exception as e:
            logger.debug(f"IncidentRecorder.get_incident_by_id failed: {e}")
            return None

    def get_recent_incidents(self, limit: int = 50) -> List[Dict]:
        """Read last N incidents from file. Returns empty list on error."""
        try:
            if not self._incidents_path.exists():
                return []
            lines = self._incidents_path.read_text(encoding="utf-8").strip().split("\n")
            lines = [ln for ln in lines if ln.strip()]
            records = []
            for line in reversed(lines[-limit:]):
                try:
                    records.append(json.loads(line))
                except json.JSONDecodeError:
                    continue
            return records
        except Exception as e:
            logger.debug(f"IncidentRecorder.get_recent_incidents failed: {e}")
            return []


# Module-level singleton for use by event_processor
_recorder: Optional[IncidentRecorder] = None


def get_incident_recorder() -> IncidentRecorder:
    """Get or create the singleton IncidentRecorder."""
    global _recorder
    if _recorder is None:
        _recorder = IncidentRecorder()
    return _recorder


def process_event(event: Dict) -> None:
    """Convenience: process event through singleton recorder."""
    get_incident_recorder().process_event(event)


def get_recent_incidents(limit: int = 50) -> List[Dict]:
    """Convenience: get recent incidents from singleton."""
    return get_incident_recorder().get_recent_incidents(limit)


def get_active_incidents() -> Dict[str, Dict]:
    """Convenience: get active incidents from singleton."""
    return get_incident_recorder().get_active_incidents()


def get_incident_by_id(incident_id: str) -> Optional[Dict]:
    """Convenience: get incident by id (Phase 7)."""
    return get_incident_recorder().get_incident_by_id(incident_id)
