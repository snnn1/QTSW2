"""
Content-only timetable hash aligned with RobotCore TimetableContentHasher.ComputeFromTimetable.

Excludes as_of and source (metadata). Used for publish ledger correlation with robot TIMETABLE_UPDATED hashes.
"""

from __future__ import annotations

import hashlib
import json
from typing import Any, Dict, List


def compute_content_hash_from_document(doc: Dict[str, Any]) -> str:
    """
    Build the same logical payload as C# TimetableContentForHash + TimetableStreamForHash, then SHA-256 hex.

    Streams are ordered by stream id (OrdinalIgnoreCase equivalent: case-insensitive sort).
    """
    streams_raw = doc.get("streams") or []
    if not isinstance(streams_raw, list):
        streams_raw = []

    def stream_key(s: Dict[str, Any]) -> str:
        return (s.get("stream") or "").lower()

    streams_sorted: List[Dict[str, Any]] = sorted(
        [x for x in streams_raw if isinstance(x, dict)],
        key=stream_key,
    )

    payload = {
        "trading_date": doc.get("trading_date") or "",
        "timezone": doc.get("timezone") or "",
        "streams": [
            {
                "stream": s.get("stream") or "",
                "instrument": s.get("instrument") or "",
                "session": s.get("session") or "",
                "slot_time": s.get("slot_time") or "",
                "enabled": bool(s.get("enabled", False)),
                "block_reason": s.get("block_reason") or "",
                "decision_time": s.get("decision_time") or "",
            }
            for s in streams_sorted
        ],
    }
    # Compact JSON; key order stable for dict insertion order (Py 3.7+)
    raw = json.dumps(payload, ensure_ascii=False, separators=(",", ":"))
    return hashlib.sha256(raw.encode("utf-8")).hexdigest()
