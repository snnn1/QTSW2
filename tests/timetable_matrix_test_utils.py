"""Shared helpers for matrix-backed timetable tests."""

from __future__ import annotations

from modules.timetable.timetable_engine import (
    TimetableEngine,
    _execution_slot_order_and_display_map,
)


def matrix_time_valid_for_execution(eng: TimetableEngine, stream_id: str) -> str:
    """First session slot (display string) for the stream's session — same list the matrix must use."""
    session = "S1" if stream_id.endswith("1") else "S2"
    session_slots = eng.session_time_slots.get(session, [])
    order_norm, norm_to_display = _execution_slot_order_and_display_map(session_slots)
    for n in order_norm:
        return norm_to_display[n]
    return session_slots[0] if session_slots else "09:30"
