"""Tests for timetable write-time validation (incident 2026-03-20 prevention)."""

import os
import pytest

from modules.timetable.timetable_write_guard import validate_streams_before_execution_write


SLOTS = {"S1": ["07:30", "08:00", "09:00"], "S2": ["09:30", "10:00", "10:30", "11:00"]}


def test_valid_ym_early_and_es_standard():
    streams = [
        {"stream": "YM1", "instrument": "YM", "session": "S1", "slot_time": "07:30", "enabled": True},
        {"stream": "ES1", "instrument": "ES", "session": "S1", "slot_time": "09:00", "enabled": True},
    ]
    validate_streams_before_execution_write(streams, session_time_slots=SLOTS)


def test_rejects_es1_with_0730():
    streams = [
        {"stream": "ES1", "instrument": "ES", "session": "S1", "slot_time": "07:30", "enabled": True},
    ]
    with pytest.raises(ValueError, match="TIMETABLE_WRITE_REJECTED_INSTRUMENT_SLOT_MISMATCH"):
        validate_streams_before_execution_write(streams, session_time_slots=SLOTS)


def test_rejects_bad_slot_time():
    streams = [
        {"stream": "ES1", "instrument": "ES", "session": "S1", "slot_time": "07:45", "enabled": True},
    ]
    with pytest.raises(ValueError, match="TIMETABLE_WRITE_REJECTED"):
        validate_streams_before_execution_write(streams, session_time_slots=SLOTS)


def test_disabled_stream_may_have_empty_slot_time():
    """Execution builder can leave slot empty when the stream is disabled (no valid candidates)."""
    streams = [
        {
            "stream": "NQ1",
            "instrument": "NQ",
            "session": "S1",
            "slot_time": "",
            "enabled": False,
            "block_reason": "no_valid_slot_after_exclusions",
        },
    ]
    validate_streams_before_execution_write(streams, session_time_slots=SLOTS)


def test_skip_guard_env_allows_es_0730(monkeypatch):
    monkeypatch.setenv("QTSW2_SKIP_TIMETABLE_INSTRUMENT_SLOT_GUARD", "1")
    streams = [
        {"stream": "ES1", "instrument": "ES", "session": "S1", "slot_time": "07:30", "enabled": True},
    ]
    try:
        validate_streams_before_execution_write(streams, session_time_slots=SLOTS)
    finally:
        monkeypatch.delenv("QTSW2_SKIP_TIMETABLE_INSTRUMENT_SLOT_GUARD", raising=False)
