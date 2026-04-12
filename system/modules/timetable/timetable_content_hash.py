"""
Content-only timetable hash aligned with RobotCore TimetableContentHasher.ComputeFromTimetable.

Excludes as_of and source (metadata). Used for publish ledger correlation with robot TIMETABLE_UPDATED hashes.
"""

from __future__ import annotations

import hashlib
import json
from datetime import datetime
from typing import Any, Dict, List


def _streams_canonical_order_for_hash(streams: List[Dict[str, Any]]) -> List[Dict[str, Any]]:
    """
    Same ordering as execution publish: latest slot_time first, then stream id (case-insensitive).
    Missing/invalid slot_time sorts last within this rule.
    """
    items = [x for x in streams if isinstance(x, dict)]

    def sort_key(s: Dict[str, Any]) -> tuple:
        raw = (s.get("slot_time") or "").strip()
        try:
            if not raw:
                mins = -1
            else:
                t = datetime.strptime(raw, "%H:%M")
                mins = t.hour * 60 + t.minute
        except Exception:
            mins = -1
        return (-mins, (s.get("stream") or "").lower())

    return sorted(items, key=sort_key)


def compute_timetable_hash_sorted(
    session_trading_date: str,
    timezone: str,
    streams: List[Dict[str, Any]],
) -> str:
    """
    SHA-256 over canonical JSON (sort_keys=True) for version / skip-if-unchanged.
    Stream order in the payload is canonical: **slot_time descending**, then stream id
    (matches ``timetable_engine`` publish order). Document/array order on disk is ignored
    for hashing so only semantic content + this canonical order affect the digest.
    """
    streams_sorted = _streams_canonical_order_for_hash(streams)
    stream_objs = [
        {
            "stream": s.get("stream") or "",
            "slot_time": s.get("slot_time") or "",
            "enabled": bool(s.get("enabled", False)),
            "block_reason": s.get("block_reason") or "",
        }
        for s in streams_sorted
    ]
    payload = {
        "session_trading_date": session_trading_date or "",
        "streams": stream_objs,
        "timezone": timezone or "",
    }
    return hashlib.sha256(
        json.dumps(payload, sort_keys=True, ensure_ascii=False).encode("utf-8")
    ).hexdigest()


def timetable_hash_from_document(doc: Dict[str, Any]) -> str:
    """Derive sorted-key content hash from a saved execution document."""
    sd = (doc.get("session_trading_date") or doc.get("trading_date") or "").strip()
    tz = doc.get("timezone") or ""
    streams_raw = doc.get("streams") or []
    if not isinstance(streams_raw, list):
        streams_raw = []
    return compute_timetable_hash_sorted(sd, tz, streams_raw)


def compute_content_hash_from_document(doc: Dict[str, Any]) -> str:
    """
    Build the same logical payload as C# TimetableContentForHash + TimetableStreamForHash, then SHA-256 hex.

    Streams are ordered **slot_time descending**, then stream id (case-insensitive), matching publish.
    """
    streams_raw = doc.get("streams") or []
    if not isinstance(streams_raw, list):
        streams_raw = []

    streams_sorted = _streams_canonical_order_for_hash(streams_raw)

    payload = {
        "session_trading_date": (doc.get("session_trading_date") or doc.get("trading_date") or ""),
        "timezone": doc.get("timezone") or "",
        "streams": [
            {
                "stream": s.get("stream") or "",
                "slot_time": s.get("slot_time") or "",
                "enabled": bool(s.get("enabled", False)),
                "block_reason": s.get("block_reason") or "",
            }
            for s in streams_sorted
        ],
    }
    # Compact JSON; key order stable for dict insertion order (Py 3.7+)
    raw = json.dumps(payload, ensure_ascii=False, separators=(",", ":"))
    return hashlib.sha256(raw.encode("utf-8")).hexdigest()
