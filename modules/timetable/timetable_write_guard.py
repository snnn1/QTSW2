"""
Fail-closed validation before writing execution timetable JSON (timetable_current.json).

Prevents bad data from reaching the robot (incident 2026-03-20: ES1/NG1 received YM's S1 07:30 slot).
"""

from __future__ import annotations

import logging
import os
from typing import Any, Dict, List, Mapping, Optional

logger = logging.getLogger(__name__)

try:
    from modules.matrix.config import (
        S1_EARLY_OPEN_SLOT_TIME,
        S1_INSTRUMENTS_ALLOWED_EARLY_OPEN_SLOT,
        SLOT_ENDS,
    )
    from modules.timetable.stream_id_derived import (
        instrument_from_stream_id,
        session_from_stream_id,
    )
except ImportError:
    from matrix.config import (  # type: ignore
        S1_EARLY_OPEN_SLOT_TIME,
        S1_INSTRUMENTS_ALLOWED_EARLY_OPEN_SLOT,
        SLOT_ENDS,
    )
    from timetable.stream_id_derived import (  # type: ignore
        instrument_from_stream_id,
        session_from_stream_id,
    )


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

    Checks:
      1) Each enabled stream's slot_time is in SLOT_ENDS[session] (parity with robot spec).
         Disabled streams may omit slot_time when the execution builder left no valid candidates.
      2) S1 @ S1_EARLY_OPEN_SLOT_TIME only for instruments in S1_INSTRUMENTS_ALLOWED_EARLY_OPEN_SLOT.

    Set QTSW2_SKIP_TIMETABLE_INSTRUMENT_SLOT_GUARD=1 to bypass check (2) only — not recommended.
    """
    allowed_by_session = session_time_slots or SLOT_ENDS
    skip_instrument_guard = os.environ.get("QTSW2_SKIP_TIMETABLE_INSTRUMENT_SLOT_GUARD", "").strip().lower() in (
        "1",
        "true",
        "yes",
        "on",
    )

    early_norm = _normalize_hhmm(S1_EARLY_OPEN_SLOT_TIME)

    for s in streams:
        stream_id = s.get("stream") or "?"
        session = (s.get("session") or "").strip().upper()
        instrument = (s.get("instrument") or "").strip().upper()
        if not session:
            session = session_from_stream_id(str(stream_id))
        if not instrument:
            instrument = instrument_from_stream_id(str(stream_id)).upper()
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

        if (
            not skip_instrument_guard
            and session == "S1"
            and slot_norm == early_norm
            and instrument not in S1_INSTRUMENTS_ALLOWED_EARLY_OPEN_SLOT
        ):
            raise ValueError(
                "TIMETABLE_WRITE_REJECTED_INSTRUMENT_SLOT_MISMATCH: "
                f"stream={stream_id} instrument={instrument!r} has S1 slot {S1_EARLY_OPEN_SLOT_TIME!r}, "
                f"which is only valid for instruments {sorted(S1_INSTRUMENTS_ALLOWED_EARLY_OPEN_SLOT)}. "
                "This usually indicates a matrix/export row merge error (see incident 2026-03-20). "
                "Fix the master matrix or export; to override set QTSW2_SKIP_TIMETABLE_INSTRUMENT_SLOT_GUARD=1."
            )

    if skip_instrument_guard:
        logger.error(
            "TIMETABLE_WRITE_GUARD_BYPASS_ACTIVE: QTSW2_SKIP_TIMETABLE_INSTRUMENT_SLOT_GUARD is set — "
            "S1 early-slot / instrument pairing check is DISABLED. ES/NG/etc. may be published at YM-only early slot. "
            "Remove this env var for production."
        )
