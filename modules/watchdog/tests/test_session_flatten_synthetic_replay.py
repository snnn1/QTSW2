#!/usr/bin/env python3
"""
Synthetic session/flatten replay expectations (see session_flatten_cases.jsonl).

Run: python -m pytest modules/watchdog/tests/test_session_flatten_synthetic_replay.py -v
"""
from __future__ import annotations

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[3]))

from modules.watchdog.replay_session_flatten import replay_engine_log

_CASES = Path(__file__).resolve().parent / "session_flatten_cases.jsonl"


def _row(tracker, trading_date: str, session_class: str = "S1"):
    key = (trading_date, session_class, "__engine__")
    return tracker._rows.get(key)


def test_synthetic_cases_replay():
    tracker, collector, _eval_ts, _trace = replay_engine_log(_CASES, target_date=None)
    r01 = _row(tracker, "2026-06-01")
    assert r01 is not None
    assert r01.flatten_status == "NOT_TRIGGERED"
    assert r01.flatten_required is False
    assert r01.alert_emitted is False

    r02 = _row(tracker, "2026-06-02")
    assert r02 is not None
    assert r02.flatten_status == "CONFIRMED"
    assert r02.flatten_required is True
    assert r02.alert_emitted is False

    r03 = _row(tracker, "2026-06-03")
    assert r03.flatten_status == "TIMEOUT"
    assert r03.flatten_required is True
    assert r03.alert_emitted is True
    assert any("SESSION_FLATTEN_NOT_CONFIRMED" in k for k, _ in collector.critical)

    r04 = _row(tracker, "2026-06-04")
    assert r04.flatten_status == "FAILED"
    assert r04.alert_emitted is True

    r05 = _row(tracker, "2026-06-05")
    assert r05.flatten_status == "TRIGGERED"
    assert r05.flatten_required is True
    assert r05.alert_emitted is True

    r06 = _row(tracker, "2026-06-06")
    assert r06.flatten_status == "CONFIRMED"
    assert r06.flatten_required is True
    assert r06.alert_emitted is False

    r07 = _row(tracker, "2026-06-07")
    assert r07.flatten_status == "EXPOSURE_REMAINS"
    assert r07.flatten_required is True
    assert r07.alert_emitted is True

    r08 = _row(tracker, "2026-06-08")
    assert r08.flatten_status == "EXPOSURE_REMAINS"
    assert r08.alert_emitted is True
