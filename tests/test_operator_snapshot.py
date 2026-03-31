"""
Unit tests for Phase 1: Deterministic Operator Snapshot Engine.
"""
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).parent.parent))

from modules.watchdog.state.operator_snapshot import build_operator_snapshot


def _mk_event(event_type: str, instrument: str = None, **kwargs) -> dict:
    data = dict(kwargs)
    if instrument and "instrument" not in data:
        data["instrument"] = instrument
    return {
        "event_type": event_type,
        "event": event_type,
        "timestamp_utc": "2026-03-18T12:00:00Z",
        "instrument": instrument,
        "data": data,
    }


def _mk_state(
    connection_status: str = "Connected",
    recovery_state: str = "CONNECTED_OK",
    intent_exposures: dict = None,
    protective_events: dict = None,
    unresolved_unmatched: set = None,
) -> object:
    s = type("State", (), {})()
    s._connection_status = connection_status
    s._recovery_state = recovery_state
    s._intent_exposures = intent_exposures or {}
    s._stream_states = {}
    s._protective_events = protective_events or {}
    s._protective_failure_events = []
    s._unresolved_unmatched_instruments = unresolved_unmatched or set()
    return s


def _mk_exposure(instrument: str, entry_qty: int = 1, exit_qty: int = 0) -> object:
    e = type("Exp", (), {})()
    e.instrument = instrument
    e.entry_filled_qty = entry_qty
    e.exit_filled_qty = exit_qty
    e.state = "ACTIVE"
    e.direction = "Long"
    e.stream_id = f"{instrument}1"
    e.trading_date = "2026-03-18"
    return e


class TestOperatorSnapshot:
    """Tests for build_operator_snapshot."""

    def test_normal_healthy_no_exposure(self):
        events = [_mk_event("STREAM_STATE_TRANSITION", "MES", new_state="RANGE_LOCKED")]
        state = _mk_state()
        snap = build_operator_snapshot(events, state)
        assert "MES" in snap
        assert snap["MES"]["system_state"] == "ACTIVE"
        assert snap["MES"]["action_required"] == "NONE"
        assert snap["MES"]["exposure"]["quantity"] == 0

    def test_normal_healthy_with_exposure(self):
        events = [_mk_event("INTENT_EXPOSURE_REGISTERED", "MES")]
        state = _mk_state(intent_exposures={"i1": _mk_exposure("MES", 1, 0)})
        snap = build_operator_snapshot(events, state)
        assert "MES" in snap
        assert snap["MES"]["system_state"] == "ACTIVE"
        assert snap["MES"]["action_required"] == "NONE"
        assert snap["MES"]["exposure"]["quantity"] == 1
        assert snap["MES"]["exposure"]["source"] == "journal"

    def test_disconnect_fail_closed(self):
        events = [_mk_event("DISCONNECT_FAIL_CLOSED_ENTERED")]  # global
        state = _mk_state(recovery_state="DISCONNECT_FAIL_CLOSED")
        events.append(_mk_event("STREAM_STATE_TRANSITION", "MES", new_state="RANGE_LOCKED"))
        snap = build_operator_snapshot(events, state)
        assert "MES" in snap
        assert snap["MES"]["system_state"] == "FAIL_CLOSED"
        assert snap["MES"]["action_required"] == "WAIT"

    def test_recovery_position_unmatched(self):
        events = [_mk_event("RECOVERY_POSITION_UNMATCHED", "MES")]
        state = _mk_state(
            intent_exposures={"i1": _mk_exposure("MES", 2, 0)},
            unresolved_unmatched={"MES"},  # Event processor would have set this
        )
        snap = build_operator_snapshot(events, state)
        assert "MES" in snap
        assert snap["MES"]["ownership"] == "UNKNOWN"
        assert snap["MES"]["action_required"] == "FLATTEN"

    def test_forced_flatten_failed(self):
        events = [_mk_event("FORCED_FLATTEN_FAILED", "MES")]
        state = _mk_state(intent_exposures={"i1": _mk_exposure("MES", 1, 0)})
        snap = build_operator_snapshot(events, state)
        assert "MES" in snap
        assert snap["MES"]["action_required"] == "FLATTEN"

    def test_adoption_success(self):
        events = [_mk_event("ADOPTION_SUCCESS", "MES")]
        state = _mk_state(intent_exposures={"i1": _mk_exposure("MES", 1, 0)})
        snap = build_operator_snapshot(events, state)
        assert "MES" in snap
        assert snap["MES"]["ownership"] == "PROVEN"

    def test_protective_orders_submitted(self):
        events = [_mk_event("PROTECTIVE_ORDERS_SUBMITTED", "MES", intent_id="i1")]
        exp = _mk_exposure("MES", 1, 0)
        state = _mk_state(
            intent_exposures={"i1": exp},
            protective_events={"i1": {"2026-03-18T12:00:00Z"}},
        )
        snap = build_operator_snapshot(events, state)
        assert "MES" in snap
        assert snap["MES"]["protectives"] == "VALID"

    def test_order_submit_success_protective_stop_counts_as_valid(self):
        """Robot often emits ORDER_SUBMIT_SUCCESS(PROTECTIVE_STOP) instead of PROTECTIVE_ORDERS_SUBMITTED."""
        events = [_mk_event(
            "ORDER_SUBMIT_SUCCESS",
            "MES",
            intent_id="i1",
            order_type="PROTECTIVE_STOP",
            broker_order_id="b1",
        )]
        state = _mk_state(intent_exposures={"i1": _mk_exposure("MES", 1, 0)})
        snap = build_operator_snapshot(events, state)
        assert "MES" in snap
        assert snap["MES"]["protectives"] == "VALID"

    def test_protective_drift_detected(self):
        events = [_mk_event("PROTECTIVE_DRIFT_DETECTED", "MES")]
        state = _mk_state(intent_exposures={"i1": _mk_exposure("MES", 1, 0)})
        snap = build_operator_snapshot(events, state)
        assert "MES" in snap
        assert snap["MES"]["protectives"] == "FAILED"

    def test_recovery_decision_halt(self):
        events = [_mk_event("RECOVERY_DECISION_HALT", "MES")]
        state = _mk_state(intent_exposures={"i1": _mk_exposure("MES", 1, 0)})
        snap = build_operator_snapshot(events, state)
        assert "MES" in snap
        assert snap["MES"]["action_required"] == "RESTART"
        assert snap["MES"]["system_state"] == "FAIL_CLOSED"

    def test_bootstrap_decision_halt(self):
        events = [_mk_event("BOOTSTRAP_DECISION_HALT", "MNQ")]
        state = _mk_state(intent_exposures={"i1": _mk_exposure("MNQ", 1, 0)})
        snap = build_operator_snapshot(events, state)
        assert "MNQ" in snap
        assert snap["MNQ"]["action_required"] == "RESTART"

    def test_precedence_unmatched_over_fail_closed(self):
        """RECOVERY_POSITION_UNMATCHED (persisted in state) takes precedence over DISCONNECT_FAIL_CLOSED for action."""
        events = [_mk_event("DISCONNECT_FAIL_CLOSED_ENTERED")]
        state = _mk_state(
            recovery_state="DISCONNECT_FAIL_CLOSED",
            intent_exposures={"i1": _mk_exposure("MES", 1, 0)},
            unresolved_unmatched={"MES"},  # Persisted from prior RECOVERY_POSITION_UNMATCHED
        )
        snap = build_operator_snapshot(events, state)
        assert "MES" in snap
        assert snap["MES"]["action_required"] == "FLATTEN"
        assert snap["MES"]["system_state"] == "FAIL_CLOSED"

    def test_exposure_hydration(self):
        """Journal-derived exposure populates quantity correctly."""
        exp = _mk_exposure("MES", 2, 1)
        state = _mk_state(intent_exposures={"i1": exp})
        events = [_mk_event("INTENT_EXPOSURE_REGISTERED", "MES")]
        snap = build_operator_snapshot(events, state)
        assert "MES" in snap
        assert snap["MES"]["exposure"]["quantity"] == 1
        assert snap["MES"]["exposure"]["source"] == "journal"

    def test_journal_open_exposure_implies_proven_ownership(self):
        """Active journal exposure means robot-tracked → PROVEN (avoids false CRITICAL on open trades)."""
        state = _mk_state(intent_exposures={"i1": _mk_exposure("MES", 1, 0)})
        events = []  # No ADOPTION_SUCCESS
        snap = build_operator_snapshot(events, state)
        assert "MES" in snap
        assert snap["MES"]["ownership"] == "PROVEN"

    def test_ym_mym_buckets_merge_exposure(self):
        """MYM journal + YM1-style events collapse to YM bucket so exposure is not zero on one row."""
        exp = _mk_exposure("MYM 03-26", 1, 0)
        state = _mk_state(intent_exposures={"i1": exp})
        events = [_mk_event("STREAM_STATE_TRANSITION", "YM1", new_state="RANGE_LOCKED")]
        snap = build_operator_snapshot(events, state)
        assert "YM" in snap
        assert snap["YM"]["exposure"]["quantity"] == 1
        assert snap["YM"]["ownership"] == "PROVEN"

    def test_recovery_started(self):
        events = [_mk_event("DISCONNECT_RECOVERY_STARTED")]
        state = _mk_state(recovery_state="RECOVERY_RUNNING")
        events.append(_mk_event("STREAM_STATE_TRANSITION", "MES", new_state="RANGE_LOCKED"))
        snap = build_operator_snapshot(events, state)
        assert "MES" in snap
        assert snap["MES"]["system_state"] == "RECOVERY"

    def test_connection_lost(self):
        events = [_mk_event("CONNECTION_LOST")]
        state = _mk_state(connection_status="ConnectionLost")
        events.append(_mk_event("STREAM_STATE_TRANSITION", "MES", new_state="RANGE_LOCKED"))
        snap = build_operator_snapshot(events, state)
        assert "MES" in snap
        assert snap["MES"]["system_state"] == "DISCONNECTED"

    def test_schema_fields(self):
        """Verify exact schema per instrument (Phase 1 + Phase 2)."""
        events = [_mk_event("ADOPTION_SUCCESS", "MNQ")]
        state = _mk_state(intent_exposures={"i1": _mk_exposure("MNQ", 1, 0)})
        snap = build_operator_snapshot(events, state)
        assert "MNQ" in snap
        s = snap["MNQ"]
        assert set(s.keys()) == {
            "instrument",
            "system_state",
            "exposure",
            "ownership",
            "protectives",
            "last_event",
            "action_required",
            "status",
            "action_label",
            "confidence",
        }
        assert set(s["exposure"].keys()) == {"quantity", "source"}
        assert s["exposure"]["source"] == "journal"
        assert s["status"] in ("SAFE", "WARNING", "CRITICAL")
        assert s["action_label"] in ("NO ACTION", "WAIT", "FLATTEN NOW", "RESTART ROBOT")
        assert s["confidence"] in ("HIGH", "MEDIUM", "LOW")


class TestOperatorSnapshotPhase2:
    """Phase 2: status, action_label, confidence derivation."""

    def test_phase2_healthy_active_safe(self):
        """Healthy active instrument (proven ownership, no exposure) → SAFE / NO ACTION / MEDIUM or LOW."""
        events = [_mk_event("ADOPTION_SUCCESS", "MES")]
        state = _mk_state(intent_exposures={"i1": _mk_exposure("MES", 1, 1)})  # closed, 0 net
        snap = build_operator_snapshot(events, state)
        assert snap["MES"]["status"] == "SAFE"
        assert snap["MES"]["action_label"] == "NO ACTION"
        assert snap["MES"]["confidence"] in ("MEDIUM", "LOW", "HIGH")  # adoption gives HIGH

    def test_phase2_unmatched_critical_flatten_high(self):
        """Unmatched exposure → CRITICAL / FLATTEN NOW / HIGH."""
        events = [_mk_event("RECOVERY_POSITION_UNMATCHED", "MES")]
        state = _mk_state(
            intent_exposures={"i1": _mk_exposure("MES", 2, 0)},
            unresolved_unmatched={"MES"},
        )
        snap = build_operator_snapshot(events, state)
        assert snap["MES"]["status"] == "CRITICAL"
        assert snap["MES"]["action_label"] == "FLATTEN NOW"
        assert snap["MES"]["confidence"] == "HIGH"

    def test_phase2_forced_flatten_failed_critical_high(self):
        """Forced flatten failed → CRITICAL / FLATTEN NOW / HIGH."""
        events = [_mk_event("FORCED_FLATTEN_FAILED", "MES")]
        state = _mk_state(intent_exposures={"i1": _mk_exposure("MES", 1, 0)})
        snap = build_operator_snapshot(events, state)
        assert snap["MES"]["status"] == "CRITICAL"
        assert snap["MES"]["action_label"] == "FLATTEN NOW"
        assert snap["MES"]["confidence"] == "HIGH"

    def test_phase2_fail_closed_critical_wait_high(self):
        """Fail-closed disconnect → CRITICAL / WAIT / HIGH."""
        events = [_mk_event("DISCONNECT_FAIL_CLOSED_ENTERED")]
        state = _mk_state(recovery_state="DISCONNECT_FAIL_CLOSED")
        events.append(_mk_event("STREAM_STATE_TRANSITION", "MES", new_state="RANGE_LOCKED"))
        snap = build_operator_snapshot(events, state)
        assert snap["MES"]["status"] == "CRITICAL"
        assert snap["MES"]["action_label"] == "WAIT"
        assert snap["MES"]["confidence"] == "HIGH"

    def test_phase2_recovery_state_warning(self):
        """Recovery state only → WARNING / WAIT or NONE / MEDIUM."""
        events = [_mk_event("DISCONNECT_RECOVERY_STARTED")]
        state = _mk_state(recovery_state="RECOVERY_RUNNING")
        events.append(_mk_event("STREAM_STATE_TRANSITION", "MES", new_state="RANGE_LOCKED"))
        snap = build_operator_snapshot(events, state)
        assert snap["MES"]["status"] == "WARNING"
        assert snap["MES"]["action_label"] == "NO ACTION"
        assert snap["MES"]["confidence"] == "MEDIUM"

    def test_phase2_protective_failure_critical(self):
        """Protective failure + exposure → CRITICAL / FLATTEN NOW / HIGH (Fix 3 escalation)."""
        events = [_mk_event("PROTECTIVE_DRIFT_DETECTED", "MES")]
        state = _mk_state(intent_exposures={"i1": _mk_exposure("MES", 1, 0)})
        snap = build_operator_snapshot(events, state)
        assert snap["MES"]["status"] == "CRITICAL"
        assert snap["MES"]["action_required"] == "FLATTEN"
        assert snap["MES"]["action_label"] == "FLATTEN NOW"
        assert snap["MES"]["confidence"] == "HIGH"

    def test_phase2_journal_exposure_proven_warning_protectives_unknown(self):
        """Journal-backed open trade: PROVEN ownership; protectives unknown + qty>0 → WARNING (not CRITICAL)."""
        state = _mk_state(intent_exposures={"i1": _mk_exposure("MES", 1, 0)})
        events = []  # No ADOPTION_SUCCESS
        snap = build_operator_snapshot(events, state)
        assert snap["MES"]["ownership"] == "PROVEN"
        assert snap["MES"]["exposure"]["quantity"] == 1
        assert snap["MES"]["status"] == "WARNING"

    def test_phase2_unknown_ownership_without_exposure_safe(self):
        """Unknown ownership without exposure → SAFE (rule removed per Fix 1)."""
        state = _mk_state()
        events = [_mk_event("STREAM_STATE_TRANSITION", "MES", new_state="RANGE_LOCKED")]
        snap = build_operator_snapshot(events, state)
        assert snap["MES"]["ownership"] == "UNKNOWN"
        assert snap["MES"]["exposure"]["quantity"] == 0
        assert snap["MES"]["status"] == "SAFE"

    def test_phase2_restart_required_critical(self):
        """Restart required → CRITICAL / RESTART ROBOT / HIGH."""
        events = [_mk_event("RECOVERY_DECISION_HALT", "MES")]
        state = _mk_state(intent_exposures={"i1": _mk_exposure("MES", 1, 0)})
        snap = build_operator_snapshot(events, state)
        assert snap["MES"]["status"] == "CRITICAL"
        assert snap["MES"]["action_label"] == "RESTART ROBOT"
        assert snap["MES"]["confidence"] == "HIGH"

    def test_phase2_precedence_flatten_over_wait(self):
        """If both WAIT and FLATTEN conditions exist, CRITICAL + FLATTEN NOW wins."""
        events = [_mk_event("DISCONNECT_FAIL_CLOSED_ENTERED"), _mk_event("RECOVERY_POSITION_UNMATCHED", "MES")]
        state = _mk_state(
            recovery_state="DISCONNECT_FAIL_CLOSED",
            intent_exposures={"i1": _mk_exposure("MES", 1, 0)},
            unresolved_unmatched={"MES"},
        )
        snap = build_operator_snapshot(events, state)
        assert snap["MES"]["status"] == "CRITICAL"
        assert snap["MES"]["action_label"] == "FLATTEN NOW"
        assert snap["MES"]["action_required"] == "FLATTEN"

    def test_phase4a_forced_flatten_exposure_remaining_critical_blocked(self):
        """Phase 4A D: Flatten verify failure (exposure remains) → CRITICAL / FLATTEN NOW / HIGH."""
        events = [_mk_event("FORCED_FLATTEN_EXPOSURE_REMAINING", "MES", quantity=1)]
        state = _mk_state(intent_exposures={"i1": _mk_exposure("MES", 1, 0)})
        snap = build_operator_snapshot(events, state)
        assert snap["MES"]["status"] == "CRITICAL"
        assert snap["MES"]["action_required"] == "FLATTEN"
        assert snap["MES"]["action_label"] == "FLATTEN NOW"
        assert snap["MES"]["confidence"] == "HIGH"
