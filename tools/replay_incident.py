#!/usr/bin/env python3
"""
Phase 7: Replay incident - load events from frontend_feed in window [incident_start - 60s, incident_end + 60s].
Phase 9: Tail-only read (last 20MB), no early break for out-of-order safety.
Usage: python tools/replay_incident.py <incident_id>
"""
import sys
from datetime import datetime, timezone, timedelta
from pathlib import Path

# Add project root
ROOT = Path(__file__).parent.parent
sys.path.insert(0, str(ROOT))

from modules.watchdog.incident_recorder import get_incident_by_id
from modules.watchdog.replay_helpers import load_incident_events


def main():
    if len(sys.argv) < 2:
        print("Usage: python tools/replay_incident.py <incident_id>")
        sys.exit(1)
    incident_id = sys.argv[1]
    incident = get_incident_by_id(incident_id)
    if not incident:
        print(f"Incident {incident_id} not found")
        sys.exit(1)
    start_str = incident.get("start_ts")
    end_str = incident.get("end_ts")
    if not start_str or not end_str:
        print("Incident missing start_ts or end_ts")
        sys.exit(1)
    start_dt = datetime.fromisoformat(start_str.replace("Z", "+00:00"))
    end_dt = datetime.fromisoformat(end_str.replace("Z", "+00:00"))
    if start_dt.tzinfo is None:
        start_dt = start_dt.replace(tzinfo=timezone.utc)
    if end_dt.tzinfo is None:
        end_dt = end_dt.replace(tzinfo=timezone.utc)
    window_start = start_dt - timedelta(seconds=60)
    window_end = end_dt + timedelta(seconds=60)

    print(f"Incident: {incident.get('type')} ({incident_id})")
    print(f"Duration: {incident.get('duration_sec')}s")
    print(f"Window: {window_start.isoformat()} -> {window_end.isoformat()}")
    print()

    events = load_incident_events(window_start, window_end)

    print(f"Found {len(events)} events")
    for ev in events:
        et = ev.get("event_type", "?")
        ts = ev.get("timestamp_utc") or ev.get("ts_utc") or ev.get("timestamp", "?")
        print(f"  {ts}  {et}")
    sys.exit(0)


if __name__ == "__main__":
    main()
