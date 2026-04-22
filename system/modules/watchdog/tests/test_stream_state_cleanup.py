from __future__ import annotations

from datetime import datetime, timedelta, timezone
from pathlib import Path
import sys
import uuid

sys.path.insert(0, str(Path(__file__).resolve().parents[3]))

import json

from modules.watchdog.state_manager import StreamStateInfo, WatchdogStateManager


def _workspace_temp_dir() -> Path:
    base = Path.cwd() / "tmp" / "pytest_watchdog"
    base.mkdir(parents=True, exist_ok=True)
    path = base / uuid.uuid4().hex
    path.mkdir(parents=True, exist_ok=False)
    return path


def test_engine_start_cleanup_preserves_terminal_same_day_streams():
    sm = WatchdogStateManager()
    now = datetime.now(timezone.utc)
    trading_date = "2026-04-13"

    sm._stream_states[(trading_date, "ES1")] = StreamStateInfo(
        trading_date=trading_date,
        stream="ES1",
        state="DONE",
        committed=True,
        commit_reason="TARGET",
        state_entry_time_utc=now - timedelta(hours=3),
        execution_instrument="ES 06-26",
    )
    sm._stream_states[(trading_date, "NG1")] = StreamStateInfo(
        trading_date=trading_date,
        stream="NG1",
        state="RANGE_LOCKED",
        committed=False,
        state_entry_time_utc=now - timedelta(minutes=10),
        execution_instrument="NG 06-26",
    )

    sm.cleanup_stale_streams(trading_date, now, clear_all_for_date=True)

    assert (trading_date, "ES1") in sm._stream_states
    assert sm._stream_states[(trading_date, "ES1")].state == "DONE"
    assert (trading_date, "NG1") not in sm._stream_states


def test_trade_completed_journal_recreates_done_row_when_missing():
    sm = WatchdogStateManager()
    sm._trading_date = "2026-04-13"
    journals_dir = _workspace_temp_dir() / "execution_journals"
    journals_dir.mkdir(parents=True, exist_ok=True)

    (journals_dir / "2026-04-13_ES2_intent123.json").write_text(
        json.dumps(
            {
                "EntryFilled": True,
                "TradeCompleted": True,
                "Instrument": "ES 06-26",
                "CompletionReason": "TARGET",
                "CompletedAtUtc": "2026-04-13T16:00:00Z",
            }
        ),
        encoding="utf-8",
    )

    hydrated = sm.hydrate_intent_exposures_from_journals(journals_dir)

    assert hydrated == 0
    key = ("2026-04-13", "ES2")
    assert key in sm._stream_states
    assert sm._stream_states[key].state == "DONE"
    assert sm._stream_states[key].committed is True
    assert sm._stream_states[key].commit_reason == "TARGET"
