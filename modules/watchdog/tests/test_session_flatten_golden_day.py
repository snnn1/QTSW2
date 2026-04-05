#!/usr/bin/env python3
"""
Golden engine-day JSONL: full SESSION_RESOLVED → … → FLATTEN_BROKER_FLAT_CONFIRMED chain.

Committed because repository archives predate these event names (harness logs used
SESSION_CLOSE_RESOLVED). Keeps replay/tab semantics aligned with RobotEngine emissions.

Run: python -m pytest modules/watchdog/tests/test_session_flatten_golden_day.py -v
"""
from __future__ import annotations

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[3]))

from modules.watchdog.replay_session_flatten import replay_engine_log

_GOLDEN = Path(__file__).resolve().parent / "session_flatten_golden_engine_day.jsonl"


def test_golden_engine_day_confirm_path():
    tracker, collector, _e, _t = replay_engine_log(_GOLDEN, target_date="2026-07-20")
    key = ("2026-07-20", "S1", "__engine__")
    row = tracker._rows.get(key)
    assert row is not None
    assert row.has_session is True
    assert row.source == "NT_TIMETABLE"
    assert row.flatten_status == "CONFIRMED"
    assert row.flatten_required is True
    assert row.session_close_utc.startswith("2026-07-20")
    assert row.flatten_trigger_utc.startswith("2026-07-20")
    assert row.alert_emitted is False
    assert not collector.critical
