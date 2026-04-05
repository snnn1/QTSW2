"""
Fail-closed validation before writing execution timetable JSON (timetable_current.json).

Ensures each enabled stream's ``slot_time`` is in the configured list for its session
(``SLOT_ENDS`` / engine ``session_time_slots``). All session slots are valid for every
instrument; the matrix chooses which slot to use.
"""

from __future__ import annotations

from typing import Any, Dict, List, Mapping, Optional

try:
    from modules.matrix.config import SLOT_ENDS
    from modules.timetable.stream_id_derived import session_from_stream_id
except ImportError:
    from matrix.config import SLOT_ENDS  # type: ignore
    from timetable.stream_id_derived import session_from_stream_id  # type: ignore


def _normalize_hhmm(slot: Optional[str]) -> str:
    if not slot or not str(slot).strip():
        return ""
    parts = str(slot).strip().split(":")
    if len(parts) >= 2:
        try:
            return f"{int(parts[0]):02d}:{int(parts[1]):02d}"
        except ValueError:
            return str(slot).strip()
    return str(slot).strip()


def validate_streams_before_execution_write(
    streams: List[Dict[str, Any]],
    *,
    session_time_slots: Optional[Mapping[str, List[str]]] = None,
) -> None:
    """
    Raises ValueError if the execution contract is invalid. Call immediately before atomic write.

    Each enabled stream's slot_time must be in ALLOWED[session] (from ``session_time_slots`` or SLOT_ENDS).
    Disabled streams may omit slot_time when the execution builder left no valid candidates.
    """
    allowed_by_session = session_time_slots or SLOT_ENDS

    for s in streams:
        stream_id = s.get("stream") or "?"
        session = (s.get("session") or "").strip().upper()
        if not session:
            session = session_from_stream_id(str(stream_id))
        slot_raw = s.get("slot_time")
        slot_norm = _normalize_hhmm(slot_raw if slot_raw is not None else "")
        enabled = s.get("enabled", True)

        if not session:
            raise ValueError(f"TIMETABLE_WRITE_REJECTED: stream={stream_id} missing session")
        # Disabled streams may have no slot (e.g. every execution candidate excluded by exclude_times).
        if not slot_norm and enabled is False:
            continue
        if not slot_norm:
            raise ValueError(f"TIMETABLE_WRITE_REJECTED: stream={stream_id} missing slot_time")

        session_slots = allowed_by_session.get(session)
        if not session_slots:
            raise ValueError(
                f"TIMETABLE_WRITE_REJECTED: stream={stream_id} unknown session={session!r} "
                f"(expected one of {list(allowed_by_session.keys())})"
            )
        allowed_norm = {_normalize_hhmm(x) for x in session_slots}
        if slot_norm not in allowed_norm:
            raise ValueError(
                f"TIMETABLE_WRITE_REJECTED: stream={stream_id} session={session} slot_time={slot_raw!r} "
                f"not in allowed {session_slots}"
            )
