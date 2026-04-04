#!/usr/bin/env python3
"""
Replay verification: gate clear, adoption grace → success, disconnect complete (legacy + IEA alias).

Run: python -m pytest modules/watchdog/tests/test_state_consistency_paths.py -v
"""
from __future__ import annotations

import sys
from datetime import datetime, timezone
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[3]))

from modules.watchdog.event_processor import EventProcessor
from modules.watchdog.state_manager import WatchdogStateManager

TS = "2026-03-31T15:00:00+00:00"
RUN = "test_run_state_consistency"


def _ev(seq: int, **kwargs) -> dict:
    ev = {"run_id": RUN, "event_seq": seq, "data": {}}
    ev.update(kwargs)
    return ev


def _prime_engine_alive(sm: WatchdogStateManager) -> None:
    """execution_safe requires compute_engine_alive(); tests without ticks would otherwise read stalled."""
    sm._last_engine_tick_utc = datetime.now(timezone.utc)
    sm._last_engine_alive_value = False
    assert sm.compute_engine_alive() is True


def _prime_timetable_ok(sm: WatchdogStateManager) -> None:
    """execution_safe requires a validated timetable snapshot (parity with live watchdog)."""
    sm.update_timetable_streams(
        {"ES1"},
        "2026-03-31",
        "testhash",
        datetime.now(timezone.utc),
        enabled_streams_metadata={
            "ES1": {
                "instrument": "ES",
                "session": "S1",
                "slot_time": "09:00",
                "enabled": True,
            }
        },
    )


def test_timetable_drift_blocks_execution_when_engine_ok():
    sm = WatchdogStateManager()
    _prime_engine_alive(sm)
    _prime_timetable_ok(sm)
    sm._recovery_state = "CONNECTED_OK"
    sm._reconciliation_gate_state = "OK"
    sm.update_timetable_streams(
        {"ES1"},
        "2026-03-31",
        "hash_system_content",
        datetime.now(timezone.utc),
        enabled_streams_metadata={
            "ES1": {
                "instrument": "ES",
                "session": "S1",
                "slot_time": "09:00",
                "enabled": True,
            }
        },
        timetable_identity_hash="hash_system_identity",
    )
    sm.update_robot_heartbeat_timetable(
        "hash_robot_stale",
        "2026-03-31",
        datetime.now(timezone.utc),
    )
    assert sm.compute_timetable_drift() is True
    st = sm.compute_watchdog_status()
    assert st["execution_safe"] is False
    assert st["timetable_drift"] is True
    assert sm.compute_risk_gate_status()["execution_safe"] is False


def test_execution_unsafe_when_timetable_unknown_even_if_engine_ok():
    sm = WatchdogStateManager()
    _prime_engine_alive(sm)
    sm._recovery_state = "CONNECTED_OK"
    sm._reconciliation_gate_state = "OK"
    assert sm._enabled_streams_unknown is True
    assert sm.compute_watchdog_status()["execution_safe"] is False
    assert sm.compute_risk_gate_status()["execution_safe"] is False


def test_gate_fail_closed_then_cleared_with_field_aliases():
    sm = WatchdogStateManager()
    ep = EventProcessor(sm)
    ep.process_event(_ev(1, event="RECONCILIATION_MISMATCH_FAIL_CLOSED", ts_utc=TS))
    assert sm._reconciliation_gate_state == "FAIL_CLOSED"
    rg = sm.compute_risk_gate_status()
    assert rg["recovery_state_allowed"] is False
    assert rg["execution_safe"] is False

    ep.process_event(_ev(2, **{"@event": "RECONCILIATION_MISMATCH_CLEARED", "timestamp": TS}))
    assert sm._reconciliation_gate_state == "OK"
    _prime_engine_alive(sm)
    _prime_timetable_ok(sm)
    rg2 = sm.compute_risk_gate_status()
    assert rg2["recovery_state_allowed"] is True
    assert rg2["execution_safe"] is True


def test_adoption_grace_expired_then_adoption_success_aliases():
    sm = WatchdogStateManager()
    ep = EventProcessor(sm)
    ep.process_event(_ev(1, event_type="ADOPTION_GRACE_EXPIRED_UNOWNED", timestamp_utc=TS))
    assert sm._adoption_grace_expired_active is True
    assert sm.compute_risk_gate_status()["execution_safe"] is False

    ep.process_event(_ev(2, event="ADOPTION_SUCCESS", ts_utc=TS, instrument="ES"))
    assert sm._adoption_grace_expired_active is False
    _prime_engine_alive(sm)
    _prime_timetable_ok(sm)
    assert sm.compute_risk_gate_status()["execution_safe"] is True


def test_disconnect_recovery_legacy_complete():
    sm = WatchdogStateManager()
    ep = EventProcessor(sm)
    ep.process_event(_ev(1, event_type="DISCONNECT_RECOVERY_STARTED", timestamp_utc=TS))
    assert sm._recovery_state == "RECOVERY_RUNNING"
    ep.process_event(_ev(2, event_type="DISCONNECT_RECOVERY_COMPLETE", timestamp_utc=TS))
    assert sm._recovery_state == "RECOVERY_COMPLETE"


def test_disconnect_recovery_iea_resolved_event_maps_to_complete():
    sm = WatchdogStateManager()
    ep = EventProcessor(sm)
    ep.process_event(_ev(1, event_type="DISCONNECT_RECOVERY_STARTED", timestamp_utc=TS))
    assert sm._recovery_state == "RECOVERY_RUNNING"
    ep.process_event(_ev(2, event="CONNECTION_RECOVERY_RESOLVED", ts_utc=TS))
    assert sm._recovery_state == "RECOVERY_COMPLETE"
    ts_dt = datetime.fromisoformat(TS.replace("Z", "+00:00")).replace(tzinfo=timezone.utc)
    direct = WatchdogStateManager()
    direct.update_recovery_state("CONNECTION_RECOVERY_RESOLVED", ts_dt)
    assert direct._recovery_state == "RECOVERY_COMPLETE"
