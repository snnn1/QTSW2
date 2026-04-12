"""SLOT_END_SUMMARY: payload promotion and StreamStateInfo updates."""
from __future__ import annotations

import json
from pathlib import Path

import pytest

from modules.watchdog.event_processor import EventProcessor
from modules.watchdog.slot_end_payload import promote_slot_end_summary_fields_from_payload
from modules.watchdog.state_manager import StreamStateInfo, WatchdogStateManager


def _ng1_feed_event_from_repo() -> dict:
    """Load NG1 2026-04-06 SLOT_END_SUMMARY line from frontend_feed.jsonl if present."""
    root = Path(__file__).resolve().parents[3]
    feed = root / "logs" / "robot" / "frontend_feed.jsonl"
    if not feed.exists():
        pytest.skip("frontend_feed.jsonl not in workspace")
    target = None
    with open(feed, "r", encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            try:
                o = json.loads(line)
            except json.JSONDecodeError:
                continue
            if (
                o.get("event_type") == "SLOT_END_SUMMARY"
                and o.get("stream") == "NG1"
                and o.get("trading_date") == "2026-04-06"
            ):
                target = o
                break
    if not target:
        pytest.skip("NG1 SLOT_END_SUMMARY for 2026-04-06 not found in feed")
    return target


def test_promote_from_payload_sets_trade_executed_and_reason():
    data = {
        "message": "Slot 09:00 summary",
        "payload": (
            "{ slot_time_chicago = 09:00, range_status = RANGE_VALID, range_locked = True, "
            "trade_executed = False, reason = Range locked, awaiting signal, range_high = 2.867, "
            "range_low = 2.769, live_bar_count = 351 }"
        ),
    }
    promote_slot_end_summary_fields_from_payload(data)
    assert data["trade_executed"] is False
    assert data["reason"] == "Range locked, awaiting signal"


def test_slot_end_summary_processor_populates_stream_state():
    event = _ng1_feed_event_from_repo()
    data = event.get("data") or {}
    assert isinstance(data, dict)
    assert "trade_executed" not in data, "feed fixture should only nest in payload"

    sm = WatchdogStateManager()
    sm._trading_date = "2026-04-06"
    key = ("2026-04-06", "NG1")
    sm._stream_states[key] = StreamStateInfo(
        trading_date="2026-04-06",
        stream="NG1",
        state="RANGE_LOCKED",
        committed=False,
        commit_reason=None,
    )

    EventProcessor(sm).process_event(event)

    info = sm._stream_states[key]
    assert info.trade_executed is False
    assert info.slot_reason and "Range locked" in info.slot_reason
    # v2 gap slot_missing_summary returns early when trade_executed is not None (aggregator_main)
    assert getattr(info, "trade_executed", None) is not None
