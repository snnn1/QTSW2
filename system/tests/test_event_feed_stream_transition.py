"""event_feed: STREAM_STATE_TRANSITION must pass through when robot sends previous_state."""
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent.parent))

from modules.watchdog.event_feed import EventFeedGenerator


def _base_ev():
    return {
        "event": "STREAM_STATE_TRANSITION",
        "run_id": "test-run",
        "timestamp_utc": "2026-03-24T16:00:00Z",
        "trading_date": "2026-03-24",
        "stream": "CL2",
        "instrument": "CL",
        "session": "S2",
        "slot_time_chicago": "11:00",
    }


def test_stream_state_transition_passes_with_previous_state():
    gen = EventFeedGenerator()
    ev = _base_ev()
    ev["data"] = {"previous_state": "ARMED", "new_state": "RANGE_BUILDING"}
    out = gen._process_event(ev)
    assert out is not None
    assert out.get("event_type") == "STREAM_STATE_TRANSITION"
    assert out.get("stream") == "CL2"


def test_stream_state_transition_passes_with_old_state_alias():
    gen = EventFeedGenerator()
    ev = _base_ev()
    ev["data"] = {"old_state": "ARMED", "new_state": "RANGE_BUILDING"}
    out = gen._process_event(ev)
    assert out is not None


def test_stream_state_transition_filtered_when_unknown():
    gen = EventFeedGenerator()
    ev = _base_ev()
    ev["data"] = {"previous_state": "UNKNOWN", "new_state": "PRE_HYDRATION"}
    assert gen._process_event(ev) is None
