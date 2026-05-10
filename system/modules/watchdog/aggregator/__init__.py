"""Watchdog aggregator package: WatchdogAggregator + event-derived session-flatten state."""

from modules.watchdog.aggregator_main import (
    WatchdogAggregator,
    _chicago_now_past_timetable_slot,
    _derive_stream_state_reference_utc,
    _latest_prior_watchdog_info_for_stream,
    _read_last_lines,
    _slot_end_no_trade_expectation_gap,
    _slot_missing_summary_expectation_gap,
    _watchdog_info_for_stream_today,
)

__all__ = [
    "WatchdogAggregator",
    "_read_last_lines",
    "_chicago_now_past_timetable_slot",
    "_derive_stream_state_reference_utc",
    "_latest_prior_watchdog_info_for_stream",
    "_watchdog_info_for_stream_today",
    "_slot_end_no_trade_expectation_gap",
    "_slot_missing_summary_expectation_gap",
]
