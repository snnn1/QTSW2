"""
Phase 1 + Phase 2: Deterministic Operator Snapshot Engine

Pure derivation layer — converts Watchdog inputs into per-instrument operator snapshot.
Read-only, no side effects. Robot remains the sole decision engine.

Phase 2 adds: status, action_label, confidence (deterministic, rule-based).

ANCHOR: state_manager + journals FIRST, events SECOND.

INSTRUMENT SCOPE (v1): We use instrument root (e.g. MES, MNQ) not full contract (MES 03-26).
This means MES 03-26 and MES 06-26 collapse to MES. Risk: unmatched on one contract
could be incorrectly cleared or ownership marked when the other contract resolves.
Long-term: move to full contract or include expiry dimension. Acceptable for v1.
"""
import re
from typing import Any, Dict, List, Optional, Set
from datetime import datetime

# Event types that affect snapshot derivation (used as secondary refinement)
_GLOBAL_SYSTEM_EVENTS = frozenset({
    "CONNECTION_LOST",
    "CONNECTION_LOST_SUSTAINED",
    "CONNECTION_RECOVERED",
    "CONNECTION_RECOVERED_NOTIFICATION",
    "CONNECTION_CONFIRMED",
    "DISCONNECT_FAIL_CLOSED_ENTERED",
    "DISCONNECT_RECOVERY_STARTED",
    "DISCONNECT_RECOVERY_COMPLETE",
    "DISCONNECT_RECOVERY_ABORTED",
    "RECOVERY_DECISION_HALT",
    "BOOTSTRAP_DECISION_HALT",
    "MISMATCH_FAIL_CLOSED",
    "RECONCILIATION_MISMATCH_FAIL_CLOSED",
    "STATE_CONSISTENCY_GATE_RECOVERY_FAILED",
    "STATE_CONSISTENCY_GATE_ENGAGED",
    "RECONCILIATION_MISMATCH_CLEARED",
    "STATE_CONSISTENCY_GATE_RELEASED",
    "ADOPTION_GRACE_EXPIRED_UNOWNED",
})

_INSTRUMENT_SNAPSHOT_EVENTS = frozenset({
    "RECOVERY_POSITION_UNMATCHED",
    "ADOPTION_SUCCESS",
    "RECONCILIATION_RECOVERY_ADOPTION_SUCCESS",
    "FORCED_FLATTEN_FAILED",
    "FORCED_FLATTEN_EXPOSURE_REMAINING",
    "MANUAL_FLATTEN_REQUIRED",
    "PROTECTIVE_ORDERS_SUBMITTED",
    "PROTECTIVE_ORDERS_SUBMITTED_FROM_RECOVERY_QUEUE",
    "PROTECTIVE_ORDERS_FAILED_FLATTENED",
    "PROTECTIVE_DRIFT_DETECTED",
    "RECOVERY_DECISION_RESUME",
    "RECOVERY_DECISION_ADOPT",
    "RECOVERY_DECISION_FLATTEN",
    "RECOVERY_DECISION_HALT",
    "BOOTSTRAP_DECISION_RESUME",
    "BOOTSTRAP_DECISION_ADOPT",
    "BOOTSTRAP_DECISION_FLATTEN",
    "BOOTSTRAP_DECISION_HALT",
})


def _instrument_root(instrument: str) -> str:
    """Extract root symbol from instrument (e.g. 'MES 03-26' -> 'MES')."""
    if not instrument:
        return ""
    return str(instrument).strip().split()[0].upper()


# Execution root -> timetable/canonical root (journals use MYM; streams/timetable use YM / YM1).
# Do not map MES↔ES or MNQ↔NQ — different contracts; only true aliases belong here.
_MICRO_TO_CANONICAL_ROOT = {
    "MYM": "YM",
}


def _operator_instrument_bucket(instrument: str) -> str:
    """One bucket per product so YM1 + MYM journal + YM events merge (fixes false zero exposure / UNKNOWN)."""
    r = _instrument_root(instrument)
    # Strip stream disambiguator: YM1, NQ2, ES1 -> YM, NQ, ES (matches timetable stream_id pattern).
    m = re.match(r"^([A-Z]{1,6})(\d+)$", r)
    if m:
        r = m.group(1)
    return _MICRO_TO_CANONICAL_ROOT.get(r, r)


def _unresolved_affects_instrument(unresolved: Set[str], instrument: str) -> bool:
    if not unresolved:
        return False
    b = _operator_instrument_bucket(instrument)
    for u in unresolved:
        if _operator_instrument_bucket(u) == b:
            return True
    return False


def _collect_instruments(events: List[Dict], state_manager: Any) -> Set[str]:
    """Collect all instruments from state_manager (journals) first, then events."""
    instruments: Set[str] = set()
    # PRIMARY: journals / state_manager
    for exp in getattr(state_manager, "_intent_exposures", {}).values():
        inst = getattr(exp, "instrument", None)
        if inst:
            instruments.add(_operator_instrument_bucket(inst))
    for info in getattr(state_manager, "_stream_states", {}).values():
        inst = getattr(info, "execution_instrument", None) or getattr(info, "instrument", None)
        if inst:
            instruments.add(_operator_instrument_bucket(inst))
    # SECONDARY: events (for instruments not yet in state)
    for ev in events:
        inst = ev.get("instrument") or (ev.get("data") or {}).get("instrument")
        exec_inst = ev.get("execution_instrument") or (ev.get("data") or {}).get("execution_instrument")
        for x in (inst, exec_inst):
            if x:
                instruments.add(_operator_instrument_bucket(x))
    return instruments


def _event_affects_instrument(ev: Dict, instrument: str, state_manager: Any = None) -> bool:
    """True if event applies to this instrument (or is global)."""
    event_type = ev.get("event_type") or ev.get("event", "")
    if event_type in _GLOBAL_SYSTEM_EVENTS:
        return True
    if event_type not in _INSTRUMENT_SNAPSHOT_EVENTS:
        return False
    data = ev.get("data") or {}
    inst = ev.get("instrument") or data.get("instrument")
    exec_inst = ev.get("execution_instrument") or data.get("execution_instrument")
    bucket = _operator_instrument_bucket(instrument)
    for x in (inst, exec_inst):
        if x and _operator_instrument_bucket(x) == bucket:
            return True
    intent_id = data.get("intent_id") or ev.get("intent_id")
    if intent_id and state_manager:
        exp = getattr(state_manager, "_intent_exposures", {}).get(intent_id)
        if exp and _operator_instrument_bucket(getattr(exp, "instrument", "")) == bucket:
            return True
    return False


def _parse_ts(s: Optional[str]) -> Optional[datetime]:
    """Parse ISO timestamp."""
    if not s:
        return None
    try:
        from datetime import timezone
        dt = datetime.fromisoformat(str(s).replace("Z", "+00:00"))
        if dt.tzinfo is None:
            dt = dt.replace(tzinfo=timezone.utc)
        return dt
    except Exception:
        return None


def _journal_exposure_for_instrument(state_manager: Any, instrument: str) -> int:
    """Sum journal-derived exposure quantity for instrument (Long positive, Short negative)."""
    total = 0
    for exp in getattr(state_manager, "_intent_exposures", {}).values():
        if getattr(exp, "state", "") != "ACTIVE":
            continue
        if _operator_instrument_bucket(getattr(exp, "instrument", "")) != _operator_instrument_bucket(instrument):
            continue
        entry = getattr(exp, "entry_filled_qty", 0) or 0
        exit_qty = getattr(exp, "exit_filled_qty", 0) or 0
        direction = getattr(exp, "direction", "Long") or "Long"
        net = entry - exit_qty
        if direction and "short" in str(direction).lower():
            net = -net
        total += net
    return total


def _protective_status_for_instrument(
    events: List[Dict],
    state_manager: Any,
    instrument: str,
) -> str:
    """Derive protectives: VALID | UNKNOWN | FAILED."""
    failed = False
    valid = False
    for ev in events:
        if not _event_affects_instrument(ev, instrument, state_manager):
            continue
        t = ev.get("event_type") or ev.get("event", "")
        if t == "PROTECTIVE_ORDERS_FAILED_FLATTENED" or t == "PROTECTIVE_DRIFT_DETECTED":
            failed = True
        elif t in ("PROTECTIVE_ORDERS_SUBMITTED", "PROTECTIVE_ORDERS_SUBMITTED_FROM_RECOVERY_QUEUE"):
            valid = True
    if not valid and not failed:
        bucket = _operator_instrument_bucket(instrument)
        for ev in events:
            t = ev.get("event_type") or ev.get("event", "")
            if t != "ORDER_SUBMIT_SUCCESS":
                continue
            ed = ev.get("data") or {}
            ot = str(ed.get("order_type", "") or "").upper()
            if not ("TARGET" in ot or "STOP" in ot or "PROTECTIVE" in ot):
                continue
            inst = ev.get("instrument") or ed.get("instrument")
            exec_inst = ev.get("execution_instrument") or ed.get("execution_instrument")
            matched = False
            for x in (inst, exec_inst):
                if x and _operator_instrument_bucket(x) == bucket:
                    matched = True
                    break
            iid = ed.get("intent_id") or ev.get("intent_id")
            if not matched and iid and state_manager:
                exp = getattr(state_manager, "_intent_exposures", {}).get(str(iid))
                if exp and _operator_instrument_bucket(getattr(exp, "instrument", "")) == bucket:
                    matched = True
            if matched:
                valid = True
                break
    if failed:
        return "FAILED"
    if valid:
        return "VALID"
    return "UNKNOWN"


def _intent_has_protective_submitted(state_manager: Any, instrument: str) -> bool:
    """True if any active intent for instrument has protective submitted."""
    for intent_id, exp in getattr(state_manager, "_intent_exposures", {}).items():
        if getattr(exp, "state", "") != "ACTIVE":
            continue
        if _operator_instrument_bucket(getattr(exp, "instrument", "")) != _operator_instrument_bucket(instrument):
            continue
        if getattr(state_manager, "_protective_events", {}).get(intent_id):
            return True
    return False


def _derive_status(
    action_required: str,
    system_state: str,
    protectives: str,
    ownership: str,
    exposure_qty: int,
) -> str:
    """
    Phase 2: Deterministic status. Precedence: CRITICAL > WARNING > SAFE.
    """
    # CRITICAL
    if action_required == "FLATTEN":
        return "CRITICAL"
    if action_required == "RESTART":
        return "CRITICAL"
    if system_state == "FAIL_CLOSED":
        return "CRITICAL"
    if protectives == "FAILED":
        return "CRITICAL"
    if ownership == "UNKNOWN" and exposure_qty > 0:
        return "CRITICAL"

    # WARNING
    if system_state == "RECOVERY":
        return "WARNING"
    if action_required == "WAIT":
        return "WARNING"
    if protectives == "UNKNOWN" and exposure_qty > 0:
        return "WARNING"

    return "SAFE"


def _derive_action_label(action_required: str) -> str:
    """Phase 2: Map action_required to human-readable label."""
    mapping = {
        "NONE": "NO ACTION",
        "WAIT": "WAIT",
        "FLATTEN": "FLATTEN NOW",
        "RESTART": "RESTART ROBOT",
    }
    return mapping.get(action_required, "NO ACTION")


def _derive_confidence(
    event_types: Set[str],
    instrument: str,
    state_manager: Any,
    system_state: str,
    exposure_qty: int,
) -> str:
    """
    Phase 2: Observability-confidence classification (rule-based, not probabilistic).
    Bias toward HIGH when explicit state exists.
    """
    unresolved = getattr(state_manager, "_unresolved_unmatched_instruments", set())

    # HIGH: explicit critical events or persistent watchdog state
    if _unresolved_affects_instrument(unresolved, instrument):
        return "HIGH"
    if "RECOVERY_POSITION_UNMATCHED" in event_types:
        return "HIGH"
    if "FORCED_FLATTEN_FAILED" in event_types or "FORCED_FLATTEN_EXPOSURE_REMAINING" in event_types or "MANUAL_FLATTEN_REQUIRED" in event_types:
        return "HIGH"
    if "DISCONNECT_FAIL_CLOSED_ENTERED" in event_types:
        return "HIGH"
    if event_types & {"RECOVERY_DECISION_HALT", "BOOTSTRAP_DECISION_HALT"}:
        return "HIGH"
    if event_types & {"ADOPTION_SUCCESS", "RECONCILIATION_RECOVERY_ADOPTION_SUCCESS"}:
        return "HIGH"
    if event_types & {"PROTECTIVE_DRIFT_DETECTED", "PROTECTIVE_ORDERS_FAILED_FLATTENED"}:
        return "HIGH"

    # MEDIUM: recovery state without direct critical event
    if system_state == "RECOVERY":
        return "MEDIUM"

    # LOW: instrument in snapshot but insufficient evidence
    return "LOW"


def _derive_snapshot_for_instrument(
    instrument: str,
    events: List[Dict],
    state_manager: Any,
) -> Dict[str, Any]:
    """Derive snapshot for a single instrument. State/journals first, events second."""
    relevant = [e for e in events if _event_affects_instrument(e, instrument, state_manager)]
    relevant.sort(
        key=lambda e: (_parse_ts(e.get("timestamp_utc") or e.get("ts_utc")) or datetime.min),
        reverse=True,
    )
    event_types = {(e.get("event_type") or e.get("event", "")) for e in relevant}

    # A. system_state — state_manager FIRST, then events. Precedence: 1.FAIL_CLOSED 2.DISCONNECTED 3.RECOVERY 4.ACTIVE
    system_state = "ACTIVE"
    recovery_state = getattr(state_manager, "_recovery_state", "CONNECTED_OK")
    connection_status = getattr(state_manager, "_connection_status", "Unknown")
    reconciliation_gate = getattr(state_manager, "_reconciliation_gate_state", "OK")
    adoption_grace = getattr(state_manager, "_adoption_grace_expired_active", False)
    if (
        adoption_grace
        or reconciliation_gate == "FAIL_CLOSED"
        or recovery_state == "DISCONNECT_FAIL_CLOSED"
        or "DISCONNECT_FAIL_CLOSED_ENTERED" in event_types
        or event_types & {"RECOVERY_DECISION_HALT", "BOOTSTRAP_DECISION_HALT"}
    ):
        system_state = "FAIL_CLOSED"
    elif connection_status == "ConnectionLost" or "CONNECTION_LOST" in event_types or "CONNECTION_LOST_SUSTAINED" in event_types:
        system_state = "DISCONNECTED"
    elif (
        recovery_state == "RECOVERY_RUNNING"
        or "DISCONNECT_RECOVERY_STARTED" in event_types
        or reconciliation_gate == "ENGAGED"
    ):
        system_state = "RECOVERY"

    # B. exposure — journal only (state_manager)
    qty = _journal_exposure_for_instrument(state_manager, instrument)
    exposure = {"quantity": qty, "source": "journal"}

    # C. ownership — UNMATCHED → UNKNOWN; adoption → PROVEN; active journal exposure → PROVEN (robot-tracked).
    #    Previously only adoption events set PROVEN, so normal YM1/MYM fills stayed UNKNOWN and hit
    #    CRITICAL via _derive_status(ownership UNKNOWN + qty > 0).
    unresolved = getattr(state_manager, "_unresolved_unmatched_instruments", set())
    if "RECOVERY_POSITION_UNMATCHED" in event_types or _unresolved_affects_instrument(unresolved, instrument):
        ownership = "UNKNOWN"
    elif "ADOPTION_SUCCESS" in event_types or "RECONCILIATION_RECOVERY_ADOPTION_SUCCESS" in event_types:
        ownership = "PROVEN"
    elif qty > 0:
        ownership = "PROVEN"
    else:
        ownership = "UNKNOWN"

    # D. protectives — state_manager first (_protective_events), then events
    protectives = _protective_status_for_instrument(events, state_manager, instrument)
    if protectives == "UNKNOWN" and _intent_has_protective_submitted(state_manager, instrument):
        protectives = "VALID"

    # E. last_event — diagnostic only, not logic-driving (can regress if event window shifts)
    last_event = "NONE"
    for ev in relevant:
        t = ev.get("event_type") or ev.get("event", "")
        if t in _GLOBAL_SYSTEM_EVENTS or t in _INSTRUMENT_SNAPSHOT_EVENTS:
            last_event = t
            break

    # F. action_required — PERSISTENT from state_manager first, then events
    if _unresolved_affects_instrument(unresolved, instrument):
        action_required = "FLATTEN"
    elif "FORCED_FLATTEN_FAILED" in event_types or "FORCED_FLATTEN_EXPOSURE_REMAINING" in event_types or "MANUAL_FLATTEN_REQUIRED" in event_types:
        action_required = "FLATTEN"
    elif protectives == "FAILED" and qty > 0:
        action_required = "FLATTEN"
    elif event_types & {"RECOVERY_DECISION_HALT", "BOOTSTRAP_DECISION_HALT"}:
        action_required = "RESTART"
    elif system_state == "FAIL_CLOSED":
        action_required = "WAIT"
    else:
        action_required = "NONE"

    # Phase 2: status, action_label, confidence (deterministic derivation)
    status = _derive_status(
        action_required=action_required,
        system_state=system_state,
        protectives=protectives,
        ownership=ownership,
        exposure_qty=qty,
    )
    action_label = _derive_action_label(action_required)
    confidence = _derive_confidence(
        event_types=event_types,
        instrument=instrument,
        state_manager=state_manager,
        system_state=system_state,
        exposure_qty=qty,
    )

    return {
        "instrument": instrument,
        "system_state": system_state,
        "exposure": exposure,
        "ownership": ownership,
        "protectives": protectives,
        "last_event": last_event,
        "action_required": action_required,
        "status": status,
        "action_label": action_label,
        "confidence": confidence,
    }


def build_operator_snapshot(events: List[Dict], state_manager: Any) -> Dict[str, Dict[str, Any]]:
    """
    Build deterministic per-instrument operator snapshot.

    ANCHOR: state_manager + journals FIRST, events SECOND.

    Args:
        events: Processed Watchdog events (secondary refinement).
        state_manager: WatchdogStateManager instance (primary source).

    Returns:
        Dict keyed by instrument root (e.g. "MES", "MNQ") with snapshot per instrument.
    """
    instruments = _collect_instruments(events, state_manager)
    if not instruments:
        return {}
    return {
        inst: _derive_snapshot_for_instrument(inst, events, state_manager)
        for inst in sorted(instruments)
    }
