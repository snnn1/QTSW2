#!/usr/bin/env python3
"""
Execution Anomaly Validation — Automated subset.

Runs what can be automated without NinjaTrader:
- Watchdog-derived logic: stuck order, latency spike, recovery loop
- Event processor handling of ORDER_SUBMIT_SUCCESS, EXECUTION_FILLED, ORDER_CANCELLED

Robot-side events (ghost fill, protective drift, duplicate order, position drift)
require NinjaTrader + strategy — run those manually per EXECUTION_ANOMALY_VALIDATION_RUN.md.
"""
import json
import sys
import tempfile
from datetime import datetime, timezone, timedelta
from pathlib import Path

qtsw2_root = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(qtsw2_root))

from modules.watchdog.state_manager import WatchdogStateManager
from modules.watchdog.event_processor import EventProcessor
from modules.watchdog.config import (
    ORDER_STUCK_ENTRY_THRESHOLD_SECONDS,
    EXECUTION_LATENCY_SPIKE_THRESHOLD_MS,
    RECOVERY_LOOP_COUNT_THRESHOLD,
    RECOVERY_LOOP_WINDOW_SECONDS,
)


def _ts(offset_sec: float = 0) -> str:
    return (datetime.now(timezone.utc) + timedelta(seconds=offset_sec)).isoformat()


def _ev(event_type: str, data: dict, offset_sec: float = 0) -> dict:
    return {
        "event_type": event_type,
        "timestamp_utc": _ts(offset_sec),
        "run_id": "validation-run",
        "event_seq": 0,
        "data": data,
    }


def test_stuck_order():
    """ORDER_STUCK_DETECTED: order submitted, no fill/cancel within threshold."""
    print("\n--- 5. ORDER_STUCK_DETECTED ---")
    sm = WatchdogStateManager()
    # Submit order in the past (beyond threshold)
    past = datetime.now(timezone.utc) - timedelta(seconds=ORDER_STUCK_ENTRY_THRESHOLD_SECONDS + 10)
    sm.record_order_submitted("broker-stuck-001", past, "intent-A", "MNQ", "entry", "MES1")
    stuck = sm.check_stuck_orders()
    if stuck and len(stuck) == 1 and stuck[0].get("broker_order_id") == "broker-stuck-001":
        print("  [PASS] ORDER_STUCK_DETECTED fires once for order past threshold")
        print(f"  working_duration_seconds={stuck[0].get('working_duration_seconds')}, threshold={stuck[0].get('threshold_seconds')}")
    else:
        print("  [FAIL] Expected 1 stuck order, got:", stuck)
    # Second call should return empty (order was removed)
    stuck2 = sm.check_stuck_orders()
    if not stuck2:
        print("  [PASS] Does not fire repeatedly (order removed after first check)")
    else:
        print("  [FAIL] Fired again:", stuck2)


def test_latency_spike():
    """EXECUTION_LATENCY_SPIKE_DETECTED: submit->fill > threshold."""
    print("\n--- 6. EXECUTION_LATENCY_SPIKE_DETECTED ---")
    sm = WatchdogStateManager()
    past = datetime.now(timezone.utc) - timedelta(milliseconds=EXECUTION_LATENCY_SPIKE_THRESHOLD_MS + 1000)
    sm.record_order_submitted("broker-lat-001", past, "intent-B", "MNQ", "entry", "MES1")
    sm.record_order_filled("broker-lat-001", datetime.now(timezone.utc), qty=1, price=21000.0)
    events = sm.drain_pending_derived_events()
    found = [e for e in events if e.get("event_type") == "EXECUTION_LATENCY_SPIKE_DETECTED"]
    if found and len(found) == 1:
        d = found[0].get("data", found[0])
        if d.get("latency_ms", 0) >= EXECUTION_LATENCY_SPIKE_THRESHOLD_MS:
            print("  [PASS] EXECUTION_LATENCY_SPIKE_DETECTED fires with correct latency")
            print(f"  latency_ms={d.get('latency_ms')}, threshold_ms={d.get('threshold_ms')}")
        else:
            print("  [FAIL] Latency below threshold:", d.get("latency_ms"))
    else:
        print("  [FAIL] Expected 1 latency spike event, got:", events)


def test_recovery_loop():
    """RECOVERY_LOOP_DETECTED: N recoveries in window."""
    print("\n--- 7. RECOVERY_LOOP_DETECTED ---")
    sm = WatchdogStateManager()
    now = datetime.now(timezone.utc)
    for i in range(RECOVERY_LOOP_COUNT_THRESHOLD):
        sm.update_recovery_state("DISCONNECT_RECOVERY_STARTED", now - timedelta(seconds=i * 10))
    loop = sm.check_recovery_loop(now)
    if loop and loop.get("count", 0) >= RECOVERY_LOOP_COUNT_THRESHOLD:
        print("  [PASS] RECOVERY_LOOP_DETECTED fires when threshold exceeded")
        print(f"  count={loop.get('count')}, window_seconds={loop.get('window_seconds')}")
    else:
        print("  [FAIL] Expected recovery loop, got:", loop)


def test_event_processor_order_tracking():
    """Event processor records ORDER_SUBMIT_SUCCESS and clears on EXECUTION_FILLED."""
    print("\n--- Event processor: ORDER_SUBMIT_SUCCESS / EXECUTION_FILLED ---")
    sm = WatchdogStateManager()
    ep = EventProcessor(sm)
    ep.process_event(_ev("ORDER_SUBMIT_SUCCESS", {"broker_order_id": "ep-001", "intent_id": "i1", "instrument": "MNQ", "order_type": "ENTRY"}))
    if "ep-001" in sm._pending_orders:
        print("  [PASS] ORDER_SUBMIT_SUCCESS recorded")
    else:
        print("  [FAIL] Order not in pending")
    ep.process_event(_ev("EXECUTION_FILLED", {"broker_order_id": "ep-001", "quantity": 1, "price": 21000.0}))
    if "ep-001" not in sm._pending_orders:
        print("  [PASS] EXECUTION_FILLED removes from pending")
    else:
        print("  [FAIL] Order still in pending")
    # Latency under threshold should not produce event
    events = sm.drain_pending_derived_events()
    latency_events = [e for e in events if e.get("event_type") == "EXECUTION_LATENCY_SPIKE_DETECTED"]
    if not latency_events:
        print("  [PASS] No latency spike when fill is immediate")
    else:
        print("  [FAIL] Unexpected latency spike:", latency_events)


def test_engine_start_clears_pending():
    """ENGINE_START clears pending orders (no false stuck after restart)."""
    print("\n--- 8. Reconnect/bootstrap: ENGINE_START clears pending ---")
    sm = WatchdogStateManager()
    ep = EventProcessor(sm)
    ep.process_event(_ev("ORDER_SUBMIT_SUCCESS", {"broker_order_id": "pre-restart", "intent_id": "i1", "instrument": "MNQ"}))
    if "pre-restart" in sm._pending_orders:
        print("  [PASS] Order recorded before ENGINE_START")
    ep.process_event(_ev("ENGINE_START", {}))
    if "pre-restart" not in sm._pending_orders:
        print("  [PASS] ENGINE_START clears pending orders (no false stuck)")
    else:
        print("  [FAIL] Pending orders not cleared on ENGINE_START")


def main():
    print("=" * 60)
    print("Execution Anomaly Validation — Automated Subset")
    print("=" * 60)
    print("\nRobot-side events (1–4) require NinjaTrader. Run manually.")
    test_stuck_order()
    test_latency_spike()
    test_recovery_loop()
    test_event_processor_order_tracking()
    test_engine_start_clears_pending()
    print("\n" + "=" * 60)
    print("Done. Fill remaining scenarios in EXECUTION_ANOMALY_VALIDATION_RUN.md")
    print("=" * 60)


if __name__ == "__main__":
    main()
