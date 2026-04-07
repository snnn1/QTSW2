"""Shared parsing for SLOT_END_SUMMARY data['payload'] (C# anonymous object string)."""
from __future__ import annotations

import re
from typing import Any, Dict, Optional


def _reason_value_from_robot_payload(payload_str: str) -> Optional[str]:
    """Extract reason= value; value may contain commas before the next key = pair."""
    m = re.search(r"reason\s*=\s*", payload_str, re.IGNORECASE)
    if not m:
        return None
    rest = payload_str[m.end() :]
    m2 = re.search(r",\s*[a-z_][a-z0-9_]*\s*=", rest, re.IGNORECASE)
    if m2:
        return rest[: m2.start()].strip().rstrip(",").strip()
    s = rest.strip()
    if s.endswith("}"):
        s = s[:-1].strip()
    return s or None


def promote_slot_end_summary_fields_from_payload(data: Dict[str, Any]) -> None:
    """
    Mutates data in place: set trade_executed and reason from data['payload'] when those keys
    are absent at top level (robot / frontend_feed often nest them only in the payload string).
    """
    if not isinstance(data, dict):
        return
    payload_str = data.get("payload")
    if not isinstance(payload_str, str) or not payload_str.strip():
        return

    if "trade_executed" not in data:
        try:
            trade_match = re.search(r"trade_executed\s*=\s*(True|False)", payload_str, re.IGNORECASE)
            if trade_match:
                data["trade_executed"] = trade_match.group(1).lower() == "true"
        except Exception:
            pass

    if "reason" not in data:
        try:
            reason = _reason_value_from_robot_payload(payload_str)
            if reason is not None:
                data["reason"] = reason
        except Exception:
            pass
