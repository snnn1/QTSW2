"""Expectation gaps: v1 slot_end_no_trade; v2 slot_missing_summary (timetable + Chicago slot wall)."""

from __future__ import annotations

import sys
from datetime import datetime
from pathlib import Path
from types import SimpleNamespace

import pytz

sys.path.insert(0, str(Path(__file__).resolve().parents[3]))

from modules.watchdog.aggregator import (
    _chicago_now_past_timetable_slot,
    _slot_end_no_trade_expectation_gap,
    _slot_missing_summary_expectation_gap,
)

CHI = pytz.timezone("America/Chicago")


def test_emits_gap_when_trade_executed_false_same_day():
    info = SimpleNamespace(
        trading_date="2026-03-31",
        trade_executed=False,
        state="DONE",
        slot_reason="no_signal",
    )
    g = _slot_end_no_trade_expectation_gap(info, "2026-03-31", "ES2", "ES", "S1")
    assert g is not None
    assert g["stream_id"] == "ES2"
    assert g["expected"] == "trade_in_slot"
    assert g["actual"] == "none_executed"
    assert g["gap_type"] == "slot_end_no_trade"


def test_no_gap_when_trade_executed_true():
    info = SimpleNamespace(trading_date="2026-03-31", trade_executed=True, state="OPEN")
    assert _slot_end_no_trade_expectation_gap(info, "2026-03-31", "ES2", "ES", "S1") is None


def test_no_gap_when_trade_executed_unknown():
    info = SimpleNamespace(trading_date="2026-03-31", trade_executed=None, state="RANGE_LOCKED")
    assert _slot_end_no_trade_expectation_gap(info, "2026-03-31", "ES2", "ES", "S1") is None


def test_no_gap_when_watchdog_trading_date_prior_session():
    info = SimpleNamespace(trading_date="2026-03-28", trade_executed=False, state="DONE")
    assert _slot_end_no_trade_expectation_gap(info, "2026-03-31", "ES2", "ES", "S1") is None


def test_chicago_past_slot_wall():
    assert not _chicago_now_past_timetable_slot(
        "2026-03-31", "23:59",
        now_chicago=CHI.localize(datetime(2026, 3, 31, 9, 0, 0)),
    )
    assert _chicago_now_past_timetable_slot(
        "2026-03-31", "09:00",
        now_chicago=CHI.localize(datetime(2026, 3, 31, 10, 0, 0)),
    )


def test_slot_missing_summary_when_past_slot_and_trade_executed_unset():
    info_today = SimpleNamespace(state="RANGE_LOCKED", trade_executed=None, slot_reason=None)
    d = {("2026-03-31", "ES2"): info_today}
    now = CHI.localize(datetime(2026, 3, 31, 10, 0, 0))
    g = _slot_missing_summary_expectation_gap(
        d, "2026-03-31", "ES2", "ES", "S1", "09:00", now_chicago=now
    )
    assert g is not None
    assert g["gap_type"] == "slot_missing_summary"
    assert g["expected"] == "slot_end_summary"
    assert g["actual"] == "not_received"


def test_v2_suppressed_when_v1_would_apply():
    from datetime import datetime

    info_today = SimpleNamespace(
        state="DONE", trade_executed=False, slot_reason="x", trading_date="2026-03-31"
    )
    d = {("2026-03-31", "ES2"): info_today}
    now = CHI.localize(datetime(2026, 3, 31, 10, 0, 0))
    assert (
        _slot_missing_summary_expectation_gap(d, "2026-03-31", "ES2", "ES", "S1", "09:00", now_chicago=now)
        is None
    )


def test_v2_none_when_no_state_for_today():
    d = {}
    now = CHI.localize(datetime(2026, 3, 31, 10, 0, 0))
    assert (
        _slot_missing_summary_expectation_gap(d, "2026-03-31", "ES2", "ES", "S1", "09:00", now_chicago=now)
        is None
    )
