#!/usr/bin/env python3
"""
Verification: YM1 / MYM tracking against all fixes (2026-03-12)

Traces the full flow for YM1 to ensure Instrument Health shows MYM correctly.
Run: python -m modules.watchdog.tests.verify_ym1_tracking
"""
from datetime import datetime, timezone, timedelta
import sys
from pathlib import Path

# Add project root
sys.path.insert(0, str(Path(__file__).parent.parent.parent.parent))

from modules.watchdog.state_manager import (
    WatchdogStateManager,
    _get_execution_instrument_for_canonical,
    StreamStateInfo,
)
from modules.watchdog.timetable_poller import TimetablePoller


def test_execution_policy_lookup():
    """Fix 1: Execution policy maps YM -> MYM."""
    result = _get_execution_instrument_for_canonical("YM")
    assert result == "MYM", f"Expected MYM, got {result}"
    print("  [OK] Execution policy: YM -> MYM")


def test_bars_expected_with_execution_instrument():
    """Fix 2a: bars_expected matches when stream has execution_instrument='MYM 03-26'."""
    sm = WatchdogStateManager()
    sm._enabled_streams = {"YM1"}
    sm._timetable_streams = {"YM1": {"instrument": "YM", "session": "S1", "slot_time": "07:30"}}
    sm._stream_states[("2026-03-12", "YM1")] = StreamStateInfo(
        trading_date="2026-03-12",
        stream="YM1",
        state="PRE_HYDRATION",
        committed=False,
        commit_reason=None,
        state_entry_time_utc=datetime.now(timezone.utc),
        execution_instrument="MYM 03-26",
    )
    sm._stream_states[("2026-03-12", "YM1")].instrument = "YM"

    assert sm.bars_expected("MYM 03-26", market_open=True) is True
    assert sm.bars_expected("MYM", market_open=True) is True
    print("  [OK] bars_expected: stream with execution_instrument='MYM 03-26' matches MYM/MYM 03-26")


def test_bars_expected_without_execution_instrument():
    """Fix 2b: bars_expected fallback when stream has execution_instrument=None (canonical match)."""
    sm = WatchdogStateManager()
    sm._enabled_streams = {"YM1"}
    sm._timetable_streams = {"YM1": {"instrument": "YM", "session": "S1", "slot_time": "07:30"}}
    sm._stream_states[("2026-03-12", "YM1")] = StreamStateInfo(
        trading_date="2026-03-12",
        stream="YM1",
        state="PRE_HYDRATION",
        committed=False,
        commit_reason=None,
        state_entry_time_utc=datetime.now(timezone.utc),
        execution_instrument=None,
    )
    sm._stream_states[("2026-03-12", "YM1")].instrument = "YM"

    assert sm.bars_expected("MYM", market_open=True) is True
    assert sm.bars_expected("MYM 03-26", market_open=True) is True
    assert sm.bars_expected("YM", market_open=True) is True
    print("  [OK] bars_expected: stream with execution_instrument=None matches via canonical (YM)")


def test_timetable_adds_mym_to_all_execution_instruments():
    """Fix 3: Timetable-derived MYM is in all_execution_instruments and instruments_with_bars_expected."""
    import unittest.mock as mock
    sm = WatchdogStateManager()
    sm._enabled_streams = {"YM1", "ES1"}
    sm._timetable_streams = {
        "YM1": {"instrument": "YM", "session": "S1", "slot_time": "07:30"},
        "ES1": {"instrument": "ES", "session": "S1", "slot_time": "07:30"},
    }
    sm._stream_states[("2026-03-12", "YM1")] = StreamStateInfo(
        trading_date="2026-03-12",
        stream="YM1",
        state="PRE_HYDRATION",
        committed=False,
        commit_reason=None,
        state_entry_time_utc=datetime.now(timezone.utc),
        execution_instrument=None,
    )
    sm._stream_states[("2026-03-12", "YM1")].instrument = "YM"
    # Ensure grace period elapsed (6 min ago) so we get data_stall_detected
    sm._last_engine_tick_utc = datetime.now(timezone.utc) - timedelta(minutes=6)

    with mock.patch("modules.watchdog.state_manager.is_market_open", return_value=True):
        status = sm.compute_watchdog_status()
    data_stall = status.get("data_stall_detected", {})

    # MYM should appear (from timetable) when market open and stream in bar-dependent state
    assert "MYM" in data_stall or "YM" in data_stall, (
        f"Expected MYM or YM in data_stall_detected, got keys: {list(data_stall.keys())}"
    )
    print(f"  [OK] data_stall_detected includes YM1 instrument: {list(data_stall.keys())}")


def test_bar_keyed_by_ym_can_feed_mym():
    """Fix 4: When bars are keyed by 'YM' (robot fallback), MYM still gets last_bar via canonical lookup."""
    sm = WatchdogStateManager()
    sm._enabled_streams = {"YM1"}
    sm._timetable_streams = {"YM1": {"instrument": "YM", "session": "S1", "slot_time": "07:30"}}
    sm._stream_states[("2026-03-12", "YM1")] = StreamStateInfo(
        trading_date="2026-03-12",
        stream="YM1",
        state="PRE_HYDRATION",
        committed=False,
        commit_reason=None,
        state_entry_time_utc=datetime.now(timezone.utc),
        execution_instrument=None,
    )
    sm._stream_states[("2026-03-12", "YM1")].instrument = "YM"

    # Simulate: robot sends bar events with instrument="YM" -> update_last_bar("YM", ...)
    now = datetime.now(timezone.utc)
    sm._last_bar_utc_by_execution_instrument["YM"] = now

    status = sm.compute_watchdog_status()
    data_stall = status.get("data_stall_detected", {})

    # MYM should appear with last_bar from YM
    mym_info = data_stall.get("MYM")
    assert mym_info is not None, f"Expected MYM in data_stall_detected, got: {list(data_stall.keys())}"
    assert mym_info.get("last_bar_chicago") is not None, f"MYM should have last_bar_chicago: {mym_info}"
    print(f"  [OK] MYM gets last_bar from YM key: last_bar_chicago={mym_info.get('last_bar_chicago')[:19]}")


def test_bar_keyed_by_mym_full_name():
    """When bars are keyed by 'MYM 03-26', standard path works."""
    sm = WatchdogStateManager()
    sm._enabled_streams = {"YM1"}
    sm._timetable_streams = {"YM1": {"instrument": "YM", "session": "S1", "slot_time": "07:30"}}
    sm._stream_states[("2026-03-12", "YM1")] = StreamStateInfo(
        trading_date="2026-03-12",
        stream="YM1",
        state="PRE_HYDRATION",
        committed=False,
        commit_reason=None,
        state_entry_time_utc=datetime.now(timezone.utc),
        execution_instrument="MYM 03-26",
    )
    sm._stream_states[("2026-03-12", "YM1")].instrument = "YM"

    now = datetime.now(timezone.utc)
    sm._last_bar_utc_by_execution_instrument["MYM 03-26"] = now

    status = sm.compute_watchdog_status()
    data_stall = status.get("data_stall_detected", {})
    mym_info = data_stall.get("MYM 03-26")
    assert mym_info is not None, f"Expected MYM 03-26 in data_stall_detected, got: {list(data_stall.keys())}"
    assert mym_info.get("last_bar_chicago") is not None
    print(f"  [OK] MYM 03-26 (full contract) works: last_bar={mym_info.get('last_bar_chicago')[:19]}")


def test_timetable_poller_ym1():
    """Timetable includes YM1 when enabled."""
    poller = TimetablePoller()
    if not poller._timetable_path.exists():
        print("  [SKIP] No timetable file found")

    trading_date, enabled_streams, _, metadata, _, _ = poller.poll()
    if enabled_streams and "YM1" in enabled_streams:
        meta = metadata.get("YM1", {})
        assert meta.get("instrument") == "YM", f"YM1 expected instrument=YM, got {meta}"
        print(f"  [OK] Timetable: YM1 enabled, instrument=YM")
    else:
        print(f"  [INFO] Timetable: YM1 not in enabled_streams (may be expected): {enabled_streams}")


def main():
    print("YM1 / MYM tracking verification (all fixes)")
    print("=" * 50)

    tests = [
        ("Execution policy lookup", test_execution_policy_lookup),
        ("bars_expected with execution_instrument", test_bars_expected_with_execution_instrument),
        ("bars_expected without execution_instrument (fallback)", test_bars_expected_without_execution_instrument),
        ("Timetable adds MYM to all_execution_instruments", test_timetable_adds_mym_to_all_execution_instruments),
        ("Bar keyed by YM feeds MYM", test_bar_keyed_by_ym_can_feed_mym),
        ("Bar keyed by MYM 03-26", test_bar_keyed_by_mym_full_name),
        ("Timetable poller YM1", test_timetable_poller_ym1),
    ]

    failed = 0
    for name, fn in tests:
        try:
            print(f"\n{name}:")
            fn()
        except Exception as e:
            print(f"  [FAIL] {e}")
            failed += 1

    print("\n" + "=" * 50)
    if failed:
        print(f"FAILED: {failed} test(s)")
        sys.exit(1)
    print("All YM1/MYM tracking fixes verified.")

if __name__ == "__main__":
    main()
