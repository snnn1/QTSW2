#!/usr/bin/env python3
"""
Phase 0 — Forced Failure Chain Live Test

Scenario: Unmatched exposure → Recovery → Policy resolve (flatten or adopt)

Simulates the full chain by injecting synthetic events into robot logs,
running EventFeedGenerator, and verifying:
  A. frontend_feed.jsonl contains RECOVERY_POSITION_UNMATCHED
  B. WebSocket ring buffer would receive RECOVERY_POSITION_UNMATCHED, RECOVERY_DECISION_*, FORCED_FLATTEN_*
  C. No regression (feed volume, duplicates, missing events)

Run: python tools/phase0_forced_failure_chain_test.py
"""
import json
import os
import sys
import tempfile
import uuid
from datetime import datetime, timezone
from pathlib import Path

# Add project root
PROJECT_ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(PROJECT_ROOT))


def _robot_log_event(event: str, run_id: str, instrument: str = "MES", **extra) -> dict:
    """Emit event in robot log format (RobotEvents.EngineBase)."""
    return {
        "ts_utc": datetime.now(timezone.utc).isoformat(),
        "level": "WARN" if "UNMATCHED" in event or "FAIL" in event else "INFO",
        "source": "RobotEngine",
        "instrument": instrument,
        "trading_date": "2026-03-18",
        "stream": "__engine__",
        "session": "S2",
        "slot_time_chicago": "10:00",
        "account": None,
        "run_id": run_id,
        "event": event,
        "message": event,
        "data": extra or {},
    }


def main() -> int:
    run_id = f"phase0-test-{uuid.uuid4().hex[:12]}"
    print("=" * 70)
    print("Phase 0 — Forced Failure Chain Live Test")
    print("=" * 70)
    print(f"Run ID: {run_id}")
    print()

    with tempfile.TemporaryDirectory(prefix="phase0_test_") as tmpdir:
        tmp = Path(tmpdir)
        robot_log = tmp / "robot_ENGINE.jsonl"
        feed_file = tmp / "frontend_feed.jsonl"
        positions_file = tmp / "robot_log_read_positions.json"

        # 1. Write synthetic forced failure chain to robot log
        chain = [
            _robot_log_event("RECOVERY_POSITION_UNMATCHED", run_id, data={"unmatched_count": 1, "instrument": "MES"}),
            _robot_log_event("RECOVERY_DECISION_FLATTEN", run_id, data={"instrument": "MES", "reason": "ownership_not_proven"}),
            _robot_log_event("FORCED_FLATTEN_TRIGGERED", run_id, data={"instrument": "MES", "intent_id": "test-intent"}),
            _robot_log_event("FORCED_FLATTEN_POSITION_CLOSED", run_id, data={"instrument": "MES", "intent_id": "test-intent"}),
        ]
        with open(robot_log, "w", encoding="utf-8") as f:
            for ev in chain:
                f.write(json.dumps(ev) + "\n")

        # 2. Patch config to use temp dir
        import modules.watchdog.config as config
        orig_robot_dir = config.ROBOT_LOGS_DIR
        orig_feed = config.FRONTEND_FEED_FILE
        orig_positions = config.ROBOT_LOG_READ_POSITIONS_FILE
        config.ROBOT_LOGS_DIR = tmp
        config.FRONTEND_FEED_FILE = feed_file
        config.ROBOT_LOG_READ_POSITIONS_FILE = positions_file

        try:
            # 3. Run EventFeedGenerator
            from modules.watchdog.event_feed import EventFeedGenerator
            gen = EventFeedGenerator()
            gen._last_read_positions.clear()
            count = gen.process_new_events()
            gen._save_read_positions()

            # 4. Verify A: frontend_feed.jsonl contains RECOVERY_POSITION_UNMATCHED
            if not feed_file.exists():
                print("FAIL A: frontend_feed.jsonl was not created")
                return 1

            feed_events = []
            with open(feed_file, "r", encoding="utf-8") as f:
                for line in f:
                    line = line.strip()
                    if line:
                        feed_events.append(json.loads(line))

            types_in_feed = {e.get("event_type") for e in feed_events}
            required = {"RECOVERY_POSITION_UNMATCHED", "RECOVERY_DECISION_FLATTEN", "FORCED_FLATTEN_TRIGGERED", "FORCED_FLATTEN_POSITION_CLOSED"}
            missing = required - types_in_feed
            if missing:
                print(f"FAIL A: frontend_feed.jsonl missing events: {missing}")
                print(f"  Found: {types_in_feed}")
                return 1
            print("PASS A: frontend_feed.jsonl contains RECOVERY_POSITION_UNMATCHED, RECOVERY_DECISION_*, FORCED_FLATTEN_*")

            # 5. Verify B: Ring buffer would receive these (simulate _add_to_ring_buffer_if_important)
            from modules.watchdog.aggregator import WatchdogAggregator
            agg = WatchdogAggregator()
            ring_types = set()
            for ev in feed_events:
                agg._add_to_ring_buffer_if_important(ev)
            for e in agg._important_events_buffer:
                ring_types.add(e.get("type", ""))

            missing_ring = required - ring_types
            if missing_ring:
                print(f"FAIL B: WebSocket ring buffer missing: {missing_ring}")
                print(f"  In buffer: {ring_types}")
                return 1
            print("PASS B: WebSocket ring buffer receives RECOVERY_POSITION_UNMATCHED, RECOVERY_DECISION_*, FORCED_FLATTEN_*")

            # 6. Verify C: No explosion in feed volume, no duplicate spam
            if count != 4:
                print(f"FAIL C: Expected 4 events processed, got {count}")
                return 1
            if len(feed_events) != 4:
                print(f"FAIL C: Expected 4 events in feed, got {len(feed_events)}")
                return 1
            print("PASS C: No regression — 4 events, no duplicate spam")

        finally:
            config.ROBOT_LOGS_DIR = orig_robot_dir
            config.FRONTEND_FEED_FILE = orig_feed
            config.ROBOT_LOG_READ_POSITIONS_FILE = orig_positions

    print()
    print("=" * 70)
    print("Phase 0 forced failure chain test: PASSED")
    print("=" * 70)
    return 0


if __name__ == "__main__":
    sys.exit(main())
