"""Derive execution session / instrument from stream id (e.g. ES1 → ES + S1) when not in JSON."""

from __future__ import annotations


def session_from_stream_id(stream_id: str) -> str:
    s = (stream_id or "").strip()
    if len(s) >= 2 and s[-1] == "2":
        return "S2"
    return "S1"


def instrument_from_stream_id(stream_id: str) -> str:
    s = (stream_id or "").strip()
    if len(s) < 2:
        return s.upper()
    i = len(s) - 1
    while i > 0 and s[i].isdigit():
        i -= 1
    root = s[: i + 1].strip().upper()
    return root if root else s.upper()
