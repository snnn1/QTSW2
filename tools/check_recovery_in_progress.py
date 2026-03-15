#!/usr/bin/env python3
"""
Investigate why watchdog shows "RECOVERY IN PROGRESS".

Checks:
1. Recovery events in frontend_feed.jsonl (DISCONNECT_RECOVERY_STARTED, DISCONNECT_RECOVERY_COMPLETE, etc.)
2. Event ordering - was COMPLETE received after STARTED?
3. Cursor position - are recovery events being processed?
4. Watchdog logs (RECOVERY_STATE_TIMEOUT, RECOVERY_STATE_AUTO_CLEAR)
"""
import json
import sys
from pathlib import Path
from datetime import datetime, timezone
import pytz

QTSW2_ROOT = Path(__file__).resolve().parent.parent
if str(QTSW2_ROOT) not in sys.path:
    sys.path.insert(0, str(QTSW2_ROOT))

CHICAGO_TZ = pytz.timezone("America/Chicago")
RECOVERY_EVENT_TYPES = (
    "DISCONNECT_FAIL_CLOSED_ENTERED",
    "DISCONNECT_RECOVERY_STARTED",
    "DISCONNECT_RECOVERY_COMPLETE",
    "DISCONNECT_RECOVERY_ABORTED",
    "DISCONNECT_RECOVERY_SKIPPED",
    "DISCONNECT_RECOVERY_WAITING_FOR_SYNC",
)


def parse_ts(s: str):
    if not s:
        return None
    try:
        dt = datetime.fromisoformat(s.replace("Z", "+00:00"))
        return dt.replace(tzinfo=timezone.utc) if dt.tzinfo is None else dt
    except Exception:
        return None


def main():
    feed_file = QTSW2_ROOT / "logs" / "robot" / "frontend_feed.jsonl"

    print("=" * 80)
    print("RECOVERY IN PROGRESS - INVESTIGATION")
    print("=" * 80)
    print(f"\nFeed file: {feed_file}")
    print(f"Current time: {datetime.now(timezone.utc).strftime('%Y-%m-%d %H:%M:%S')} UTC")

    if not feed_file.exists():
        print(f"\n[ERROR] Feed file not found")
        return 1

    # Read last 10000 lines
    with open(feed_file, "r", encoding="utf-8-sig") as f:
        lines = f.readlines()

    total_lines = len(lines)
    recent_lines = lines[-10000:] if total_lines > 10000 else lines
    print(f"Total feed lines: {total_lines:,}")
    print(f"Scanning last: {len(recent_lines):,} lines")

    # Extract recovery events with line index (for cursor correlation)
    recovery_events = []
    for i, line in enumerate(recent_lines):
        line = line.strip()
        if not line:
            continue
        try:
            event = json.loads(line)
            et = event.get("event_type", "")
            if et in RECOVERY_EVENT_TYPES:
                ts_str = event.get("timestamp_utc", "")
                run_id = (event.get("run_id") or "")[:8]
                event_seq = event.get("event_seq", 0)
                # Line index in full file
                global_idx = total_lines - len(recent_lines) + i
                recovery_events.append({
                    "event_type": et,
                    "timestamp_utc": ts_str,
                    "run_id": run_id,
                    "event_seq": event_seq,
                    "line_index": global_idx,
                })
        except json.JSONDecodeError:
            continue

    # Report
    print("\n" + "=" * 80)
    print("1. RECOVERY EVENTS IN FEED (chronological)")
    print("=" * 80)

    if not recovery_events:
        print("\n[OK] No recovery events in recent feed.")
        print("     If UI shows RECOVERY IN PROGRESS, state may be stale from tail replay")
        print("     or from an older run. Check watchdog logs for RECOVERY_STATE_TIMEOUT.")
    else:
        for ev in recovery_events[-20:]:
            ts = parse_ts(ev["timestamp_utc"])
            ts_str = ts.strftime("%H:%M:%S") if ts else ev["timestamp_utc"][:19]
            age = ""
            if ts:
                age_sec = (datetime.now(timezone.utc) - ts).total_seconds()
                age = f" ({age_sec:.0f}s ago)" if age_sec < 86400 else ""
            print(f"  {ts_str} UTC{age} | {ev['event_type']:35} | run_id={ev['run_id']}... seq={ev['event_seq']} line={ev['line_index']}")

        latest = recovery_events[-1]
        print(f"\n  >>> Latest recovery event: {latest['event_type']} at {latest['timestamp_utc'][:19]} UTC")

        # Diagnose
        print("\n" + "=" * 80)
        print("2. DIAGNOSIS")
        print("=" * 80)

        if latest["event_type"] == "DISCONNECT_RECOVERY_STARTED":
            complete_after = [e for e in recovery_events if e["event_type"] == "DISCONNECT_RECOVERY_COMPLETE"
                             and parse_ts(e["timestamp_utc"]) and parse_ts(latest["timestamp_utc"])
                             and parse_ts(e["timestamp_utc"]) > parse_ts(latest["timestamp_utc"])]
            if not complete_after:
                print("\n[ISSUE] DISCONNECT_RECOVERY_STARTED was received, but NO DISCONNECT_RECOVERY_COMPLETE after it.")
                print("        This sets recovery_state = RECOVERY_RUNNING -> 'RECOVERY IN PROGRESS'")
                print("\n        Possible causes:")
                print("        - Robot never completed recovery (stuck in sync wait, etc.)")
                print("        - DISCONNECT_RECOVERY_COMPLETE was emitted but not yet in feed")
                print("        - Event ordering: COMPLETE may be in feed but before STARTED (tail replay)")
            else:
                print("\n[OK] DISCONNECT_RECOVERY_COMPLETE exists after STARTED.")
                print("     If UI still shows RECOVERY IN PROGRESS, check cursor/processing order.")

        elif latest["event_type"] == "DISCONNECT_RECOVERY_COMPLETE":
            print("\n[OK] Latest recovery event is DISCONNECT_RECOVERY_COMPLETE.")
            print("     recovery_state should be RECOVERY_COMPLETE → CONNECTED_OK.")
            print("     If UI shows RECOVERY IN PROGRESS, watchdog may not have processed this event yet.")

        elif latest["event_type"] == "DISCONNECT_FAIL_CLOSED_ENTERED":
            print("\n[ISSUE] Latest event is DISCONNECT_FAIL_CLOSED_ENTERED.")
            print("        recovery_state = DISCONNECT_FAIL_CLOSED (shows FAIL-CLOSED, not RECOVERY IN PROGRESS).")
            print("        If UI shows RECOVERY IN PROGRESS, a more recent DISCONNECT_RECOVERY_STARTED")
            print("        may have been processed from a different run_id.")

    # Check watchdog logs
    print("\n" + "=" * 80)
    print("3. WATCHDOG LOGS TO CHECK")
    print("=" * 80)
    print("  - RECOVERY_STATE_TIMEOUT: Recovery ran >10 min without COMPLETE -> auto-cleared")
    print("  - RECOVERY_STATE_AUTO_CLEAR: Engine alive + ticks -> auto-cleared DISCONNECT_FAIL_CLOSED")
    print("  - Look in: automation/logs/ or wherever watchdog stdout is captured")

    # Cursor check
    cursor_file = QTSW2_ROOT / "data" / "frontend_cursor.json"
    if cursor_file.exists():
        print("\n" + "=" * 80)
        print("4. CURSOR STATE (frontend_cursor.json)")
        print("=" * 80)
        try:
            cursor = json.loads(cursor_file.read_text())
            print(f"  Run IDs in cursor: {len(cursor)}")
            if recovery_events:
                latest_run = recovery_events[-1].get("run_id")
                # Find full run_id from events
                for ev in reversed(recovery_events):
                    rid = ev.get("run_id", "")
                    if rid and rid in str(cursor.keys()):
                        for k, seq in list(cursor.items())[:3]:
                            if k.startswith(rid) or rid in k:
                                print(f"  Example: run_id {k[:16]}... -> seq {seq}")
                                break
                        break
        except Exception as e:
            print(f"  [ERROR] {e}")

    print("\n" + "=" * 80)
    return 0


if __name__ == "__main__":
    sys.exit(main())
