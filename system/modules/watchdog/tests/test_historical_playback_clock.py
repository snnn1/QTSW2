from __future__ import annotations

import sys
from datetime import datetime, timezone
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[3]))

from modules.watchdog.state_manager import WatchdogStateManager


def test_historical_status_uses_session_reference_for_stuck_streams():
    sm = WatchdogStateManager()
    sm.update_timetable_state(True, "2026-04-13")

    sm.update_stream_state(
        "2026-04-13",
        "NG1",
        "RANGE_LOCKED",
        state_entry_time_utc=datetime(2026, 4, 13, 12, 30, tzinfo=timezone.utc),
    )
    sm.update_stream_state(
        "2026-04-13",
        "ES1",
        "DONE",
        committed=True,
        state_entry_time_utc=datetime(2026, 4, 13, 20, 55, tzinfo=timezone.utc),
    )

    sm._stream_states[("2026-04-13", "NG1")].instrument = "NG"
    sm._stream_states[("2026-04-13", "ES1")].instrument = "ES"

    status = sm.compute_watchdog_status()

    assert status["stuck_streams"]
    ng1 = next(item for item in status["stuck_streams"] if item["stream"] == "NG1")
    assert 30000 <= ng1["stuck_duration_seconds"] <= 30600
