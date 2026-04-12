"""Synthetic tests for daily audit (deterministic pairing + dedupe)."""
from typing import Optional

from modules.watchdog.audit import daily_audit_builder as dab


def _ev(ts: str, et: str, stream: Optional[str] = "ES1", run: str = "r1") -> dict:
    return {
        "ts_utc": ts,
        "event": et,
        "run_id": run,
        "trading_date": "2026-01-02",
        "stream": stream,
        "instrument": "ES",
    }


def test_dedupe_and_fail_closed_interval():
    raw = [
        _ev("2026-01-02T10:00:00+00:00", "MISMATCH_FAIL_CLOSED"),
        _ev("2026-01-02T10:00:00+00:00", "MISMATCH_FAIL_CLOSED"),  # dupe
        _ev("2026-01-02T10:05:00+00:00", "RECONCILIATION_MISMATCH_CLEARED"),
    ]
    events = dab.normalize_events(raw)
    assert len(events) == 2
    intervals = dab.compute_intervals(events, "2026-01-02")["reconciliation_fail_closed"]
    assert len(intervals) == 1
    d = (intervals[0][1] - intervals[0][0]).total_seconds()
    assert d == 300


def test_recovery_duration_fifo():
    raw = [
        _ev("2026-01-02T12:00:00+00:00", "DISCONNECT_RECOVERY_STARTED"),
        _ev("2026-01-02T12:00:30+00:00", "DISCONNECT_RECOVERY_COMPLETE"),
    ]
    events = dab.normalize_events(raw)
    iv = dab.compute_intervals(events, "2026-01-02")["recovery"]
    assert len(iv) == 1
    assert (iv[0][1] - iv[0][0]).total_seconds() == 30


def test_open_interval_closes_at_eod():
    raw = [_ev("2026-01-02T23:00:00+00:00", "MISMATCH_FAIL_CLOSED")]
    events = dab.normalize_events(raw)
    iv = dab.compute_intervals(events, "2026-01-02")["reconciliation_fail_closed"]
    assert len(iv) == 1
    eod = dab.trading_date_end_utc_bound("2026-01-02")
    assert iv[0][1] == eod


def test_normalize_events_mixed_event_type_field():
    raw = [
        {"timestamp_utc": "2026-01-02T10:00:00Z", "event_type": "CONNECTION_LOST", "trading_date": "2026-01-02"},
    ]
    events = dab.normalize_events(raw)
    assert len(events) == 1
    assert events[0].event_type == "CONNECTION_LOST"


def test_compute_metrics_adoption_stuck():
    raw = [
        _ev("2026-01-02T10:00:00+00:00", "ADOPTION_GRACE_EXPIRED_UNOWNED"),
        _ev("2026-01-02T10:01:00+00:00", "ADOPTION_GRACE_EXPIRED_UNOWNED"),
        _ev("2026-01-02T10:02:00+00:00", "ADOPTION_SUCCESS"),
    ]
    events = dab.normalize_events(raw)
    inc = []
    m = dab.compute_metrics("2026-01-02", events, inc)
    assert m["adoption_grace_expired_count"] == 2
    assert m["adoption_success_count"] == 1
    assert m["adoption_stuck_count"] == 1
    assert m["audit_version"] == dab.AUDIT_VERSION
    assert m["day_boundary_mode"] == dab.DAY_BOUNDARY_MODE
    assert m["metrics_quality"]["adoption_open_intervals"] == 1
    assert m["metrics_quality"]["execution_blocked_time_source"] == dab.EXECUTION_BLOCKED_TIME_SOURCE
    assert "data_integrity_score" in m
    assert isinstance(m["data_integrity_flags"], list)


def test_normalize_events_with_stats_dedupe():
    raw = [
        _ev("2026-01-02T10:00:00+00:00", "CONNECTION_LOST"),
        _ev("2026-01-02T10:00:00+00:00", "CONNECTION_LOST"),
    ]
    _, meta = dab.normalize_events_with_stats(raw)
    assert meta["duplicate_rows_dropped"] == 1
    assert meta["normalized_event_count"] == 1
