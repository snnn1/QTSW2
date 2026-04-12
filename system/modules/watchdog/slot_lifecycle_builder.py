"""
Slot Lifecycle Builder

Computes slot lifecycle state (forced flatten, reentry, slot expiry) from
watchdog event stream. In-memory only, no persistence.
"""
from datetime import datetime, timezone
from typing import Any, Dict, List, Optional
import pytz

CHICAGO_TZ = pytz.timezone("America/Chicago")

# Flatten events
FLATTEN_TRIGGERED_EVENTS = frozenset({
    "FORCED_FLATTEN_TRIGGERED",
    "SESSION_FORCED_FLATTEN_TRIGGERED",
})
FLATTEN_COMPLETED_EVENTS = frozenset({
    "SESSION_FORCED_FLATTENED",
    "FORCED_FLATTEN_POSITION_CLOSED",
    "FORCED_FLATTEN_MARKET_CLOSE",
})

# Reentry events
REENTRY_SUBMITTED_EVENTS = frozenset({"REENTRY_SUBMITTED"})

# Expiry events
SLOT_EXPIRED_EVENTS = frozenset({"SLOT_EXPIRED"})

# Reentry fill: EXECUTION_FILLED with intent_id containing "_REENTRY"
REENTRY_FILL_INTENT_SUFFIX = "_REENTRY"


def _extract_time_chicago(ev: Dict) -> Optional[str]:
    """Extract Chicago time as HH:MM:SS from event."""
    ts = ev.get("timestamp_chicago") or ev.get("timestamp_utc") or ev.get("ts_utc")
    if not ts:
        return None
    try:
        if isinstance(ts, str) and "T" in ts:
            dt = datetime.fromisoformat(ts.replace("Z", "+00:00"))
            if dt.tzinfo is None:
                dt = dt.replace(tzinfo=timezone.utc)
            chicago = dt.astimezone(CHICAGO_TZ)
            return chicago.strftime("%H:%M:%S")
        return str(ts)[:8] if len(str(ts)) >= 8 else str(ts)
    except Exception:
        return None


def _slot_key(ev: Dict) -> Optional[tuple]:
    """Extract (instrument, stream, slot_time, trading_date) from event."""
    data = ev.get("data") or {}
    if not isinstance(data, dict):
        data = {}

    instrument = (
        ev.get("instrument")
        or ev.get("canonical_instrument")
        or ev.get("execution_instrument")
        or data.get("instrument")
        or data.get("canonical_instrument")
    )
    stream = ev.get("stream") or data.get("stream") or data.get("stream_id")
    slot_time = (
        ev.get("slot_time_chicago")
        or data.get("slot_time_chicago")
        or data.get("slot_time")
    )
    trading_date = ev.get("trading_date") or data.get("trading_date")

    if not stream and not instrument:
        return None

    # Strip contract suffix for display (e.g. "MES 03-26" -> "MES")
    if instrument and isinstance(instrument, str):
        instrument = instrument.split()[0] if " " in instrument else instrument

    return (
        str(instrument or ""),
        str(stream or ""),
        str(slot_time or "") if slot_time else "",
        str(trading_date or "") if trading_date else "",
    )


def _session_slot_key(ev: Dict) -> Optional[tuple]:
    """For session-level events (FORCED_FLATTEN_TRIGGERED), use (session, trading_date) and infer slots."""
    data = ev.get("data") or {}
    if not isinstance(data, dict):
        data = {}
    trading_date = ev.get("trading_date") or data.get("trading_date")
    session = data.get("session") or ev.get("session")
    if not trading_date:
        return None
    return (str(session or ""), str(trading_date))


def build_slot_lifecycle(events: List[Dict]) -> List[Dict]:
    """
    Build slot lifecycle state from events.

    Groups by (instrument, stream, slot_time, trading_date) and computes:
    - flatten_triggered_time, flatten_completed_time
    - reentry_submitted_time, reentry_filled_time
    - slot_expiry_time
    - status: ACTIVE | FLATTENED | REENTERED | EXPIRED | BLOCKED | ERROR

    O(n) single pass.
    """
    # slot_key -> { times, status }
    slots: Dict[tuple, Dict[str, Any]] = {}

    for ev in events:
        event_type = ev.get("event_type", "")
        ts_chicago = _extract_time_chicago(ev)
        if not ts_chicago:
            continue

        # Flatten triggered (engine/session level - may not have stream)
        if event_type in FLATTEN_TRIGGERED_EVENTS:
            key = _slot_key(ev)
            if key is None:
                sess_key = _session_slot_key(ev)
                if sess_key:
                    # Session-level: create placeholder keys for known streams in that session
                    # We don't have stream list here; use a synthetic key
                    for sk, info in list(slots.items()):
                        if sk[3] == sess_key[1]:  # same trading_date
                            if not info.get("flatten_triggered_time"):
                                info["flatten_triggered_time"] = ts_chicago
                    continue
            if key and key[0] and key[1]:
                if key not in slots:
                    slots[key] = _empty_slot(key)
                if not slots[key].get("flatten_triggered_time"):
                    slots[key]["flatten_triggered_time"] = ts_chicago

        # Flatten completed
        elif event_type in FLATTEN_COMPLETED_EVENTS:
            key = _slot_key(ev)
            if key and key[0] and key[1]:
                if key not in slots:
                    slots[key] = _empty_slot(key)
                if not slots[key].get("flatten_completed_time"):
                    slots[key]["flatten_completed_time"] = ts_chicago

        # Reentry submitted
        elif event_type in REENTRY_SUBMITTED_EVENTS:
            key = _slot_key(ev)
            if key and key[0] and key[1]:
                if key not in slots:
                    slots[key] = _empty_slot(key)
                if not slots[key].get("reentry_submitted_time"):
                    slots[key]["reentry_submitted_time"] = ts_chicago

        # Reentry fill
        elif event_type == "EXECUTION_FILLED":
            data = ev.get("data") or {}
            intent_id = (data if isinstance(data, dict) else {}).get("intent_id") or ""
            if REENTRY_FILL_INTENT_SUFFIX in str(intent_id):
                key = _slot_key(ev)
                if key and key[0] and key[1]:
                    if key not in slots:
                        slots[key] = _empty_slot(key)
                    if not slots[key].get("reentry_filled_time"):
                        slots[key]["reentry_filled_time"] = ts_chicago

        # Slot expiry
        elif event_type in SLOT_EXPIRED_EVENTS:
            key = _slot_key(ev)
            if key and key[0] and key[1]:
                if key not in slots:
                    slots[key] = _empty_slot(key)
                if not slots[key].get("slot_expiry_time"):
                    slots[key]["slot_expiry_time"] = ts_chicago

    # Derive status and build response
    result = []
    for (instrument, stream, slot_time, trading_date), info in slots.items():
        if not stream:
            continue
        status = _derive_status(info)
        result.append({
            "stream": stream,
            "instrument": instrument or "-",
            "slot_time": slot_time or "-",
            "trading_date": trading_date or "-",
            "flatten_triggered_time": info.get("flatten_triggered_time"),
            "flatten_completed_time": info.get("flatten_completed_time"),
            "reentry_submitted_time": info.get("reentry_submitted_time"),
            "reentry_filled_time": info.get("reentry_filled_time"),
            "slot_expiry_time": info.get("slot_expiry_time"),
            "status": status,
        })

    # Sort by trading_date desc, then stream, then slot_time
    result.sort(
        key=lambda r: (
            r["trading_date"],
            r["stream"],
            r["slot_time"] or "",
        ),
        reverse=True,
    )
    return result


def _empty_slot(key: tuple) -> Dict[str, Any]:
    instrument, stream, slot_time, trading_date = key
    return {
        "instrument": instrument,
        "stream": stream,
        "slot_time": slot_time,
        "trading_date": trading_date,
        "flatten_triggered_time": None,
        "flatten_completed_time": None,
        "reentry_submitted_time": None,
        "reentry_filled_time": None,
        "slot_expiry_time": None,
    }


def _derive_status(info: Dict[str, Any]) -> str:
    """Derive status from lifecycle times."""
    expiry = info.get("slot_expiry_time")
    reentry_filled = info.get("reentry_filled_time")
    reentry_submitted = info.get("reentry_submitted_time")
    flatten_completed = info.get("flatten_completed_time")
    flatten_triggered = info.get("flatten_triggered_time")

    if expiry:
        return "EXPIRED"
    if reentry_filled:
        return "REENTERED"
    if flatten_completed and reentry_submitted and not reentry_filled:
        return "BLOCKED"  # Reentry submitted but not filled
    if flatten_completed:
        return "FLATTENED"
    if flatten_triggered and not flatten_completed:
        return "FLATTENED"  # Triggered, in progress
    return "ACTIVE"
